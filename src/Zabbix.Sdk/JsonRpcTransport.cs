// This Source Code Form is subject to the terms of the Mozilla Public
// License, v. 2.0. Copyright (c) 2026 schroeder247. SPDX-License-Identifier: MPL-2.0
using System.Buffers;
using System.Collections.Frozen;
using System.Globalization;
using System.Net.Http.Headers;
using System.Text.Json;
using System.Text.Json.Serialization.Metadata;
using Community.Zabbix.Diagnostics;
using Microsoft.Extensions.Logging;
using Polly.CircuitBreaker;
using Polly.RateLimiting;
using Polly.Timeout;

namespace Community.Zabbix;

/// <summary>
/// The JSON-RPC 2.0 transport of one named client, over the named <see cref="HttpClient"/> the factory mints. One
/// request id per transport, echoed back and checked; the envelope written straight to UTF-8 with the caller's type
/// info; the reply read once, its <c>error</c> object thrown as <see cref="ZabbixApiException"/>, its <c>result</c>
/// deserialized. A call is one <see cref="ZabbixApiTelemetry"/> operation from the first byte out to the result in hand,
/// so a reply the model cannot read is a failure on the span, the duration and the log alike.
/// </summary>
/// <remarks>
/// StreamJsonRpc, including its <c>HttpClientMessageHandler</c>, is not used. It is a JSON-RPC peer with its own request-id bookkeeping and error mapping (a server error surfaces as its
/// <c>RemoteInvocationException</c>), so the Zabbix error family would become a translation of a translation, and it adds
/// a package for what is left here once the platform's HTTP pipeline owns resilience, deadlines and the socket pool: a
/// four-member envelope and an id check.
/// </remarks>
internal sealed class JsonRpcTransport(
    string name,
    Uri endpoint,
    IHttpClientFactory clients,
    ZabbixCredential credential,
    IReadOnlyDictionary<string, TimeSpan> timeouts,
    ILogger<JsonRpcTransport> logger,
    ZabbixApiTelemetry telemetry,
    TimeProvider time) : IZabbixTransport
{
    private static readonly MediaTypeHeaderValue s_jsonRpc = new("application/json-rpc") { CharSet = "utf-8" };
    private readonly FrozenDictionary<string, TimeSpan> _timeouts = timeouts.ToFrozenDictionary(StringComparer.OrdinalIgnoreCase);
    private long _lastId;

    /// <inheritdoc/>
    public Task<TResult> SendAsync<TParameters, TResult>(
        string method, TParameters parameters, JsonTypeInfo<TParameters> parametersInfo, JsonTypeInfo<TResult> resultInfo, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(resultInfo);
        return ExchangeAsync(method, parameters, parametersInfo, static (ref Utf8JsonReader result, JsonTypeInfo<TResult> info, string method) =>
            JsonSerializer.Deserialize(ref result, info) ?? throw new JsonException($"The {method} reply carries a null result where {typeof(TResult).Name} was expected."), resultInfo, cancellationToken);
    }

    /// <inheritdoc/>
    public Task<JsonElement> SendAsync<TParameters>(
        string method, TParameters parameters, JsonTypeInfo<TParameters> parametersInfo, CancellationToken cancellationToken = default)
        => ExchangeAsync(method, parameters, parametersInfo, static (ref Utf8JsonReader result, object? _, string _) => JsonElement.ParseValue(ref result), (object?)null, cancellationToken);

    /// <summary>Reads the reply's <c>result</c>, the reader on its first token.</summary>
    private delegate T ResultReader<in TState, out T>(ref Utf8JsonReader result, TState state, string method);

    private async Task<T> ExchangeAsync<TParameters, T, TState>(
        string method, TParameters parameters, JsonTypeInfo<TParameters> parametersInfo, ResultReader<TState, T> read, TState state, CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(method);
        ArgumentNullException.ThrowIfNull(parametersInfo);

        long id = Interlocked.Increment(ref _lastId);
        // Written once, before the call starts: a request that cannot be written is the caller's argument, not a failed
        // call, and a renewed session sends the same bytes again (the token travels in a header).
        ReadOnlyMemory<byte> envelope = ZabbixRequestJson.Envelope(method, parameters, parametersInfo, id);
        ZabbixOperation operation = telemetry.Start(method, endpoint);
        ApiLog.Requesting(logger, method, id);
        try
        {
            T result = await SendRenewingOnceAsync(method, envelope, read, state, id, operation, cancellationToken).ConfigureAwait(false);
            telemetry.Finish(operation);
            if (logger.IsEnabled(LogLevel.Debug))
            {
                double elapsed = time.GetElapsedTime(operation.StartTimestamp).TotalMilliseconds;
                ApiLog.Replied(logger, method, id, elapsed);
            }
            return result;
        }
        catch (ZabbixApiException refusal)
        {
            ApiLog.ApiError(logger, method, id, refusal.Code, refusal.Kind, refusal.ServerMessage ?? refusal.Message, refusal.Detail);
            telemetry.Finish(operation, refusal);
            throw;
        }
        catch (OperationCanceledException cancelled) when (cancellationToken.IsCancellationRequested)
        {
            telemetry.Finish(operation, cancelled);        // the caller stopped asking: not an error
            throw;
        }
        catch (Exception failure)
        {
            string errorType = failure switch
            {
                TimeoutRejectedException => "timeout",
                BrokenCircuitException => "circuit_open",
                RateLimiterRejectedException => "rate_limited",
                InvalidDataException => "invalid_response",
                _ => ZabbixErrorType.Of(failure),
            };
            Exception surfaced = Translate(failure, method);
            ApiLog.Failed(logger, method, id, errorType, surfaced);
            telemetry.Finish(operation, surfaced, errorType);
            if (ReferenceEquals(surfaced, failure)) throw;
            throw surfaced;
        }
    }

    // The resilience pipeline's own rejections are Polly's types, which derive from Exception alone: a deadline is not a
    // TimeoutException and an open circuit is not an HttpRequestException. Each is surfaced as the standard type that
    // carries its meaning, with Polly's exception inside, so the API plane throws what the framed wire throws.
    private static Exception Translate(Exception failure, string method) => failure switch
    {
        TimeoutRejectedException timeout => new TimeoutException($"The {method} call passed its deadline of {timeout.Timeout.TotalSeconds.ToString("0.###", CultureInfo.InvariantCulture)} s.", timeout),
        BrokenCircuitException open => new HttpRequestException(HttpRequestError.Unknown, $"The {method} call was not sent: the circuit to the server is open after repeated failures.", open),
        RateLimiterRejectedException limited => new HttpRequestException(HttpRequestError.Unknown, $"The {method} call was not sent: the pipeline's concurrency limit was reached.", limited),
        _ => failure,
    };

    // A session the server refused before running the method is renewed once: the credential forgets it, the request goes
    // again with the next token, and a second refusal is the caller's (ZabbixCredential.Invalidate).
    private async Task<T> SendRenewingOnceAsync<T, TState>(
        string method, ReadOnlyMemory<byte> envelope, ResultReader<TState, T> read, TState state,
        long id, ZabbixOperation operation, CancellationToken cancellationToken)
    {
        for (int attempt = 0; ; attempt++)
        {
            string? token = ZabbixHttp.IsAnonymous(method)
                ? null
                : await credential.GetTokenAsync(new ZabbixTokenRequestContext(this, method, time), cancellationToken).ConfigureAwait(false);
            try
            {
                return await SendOnceAsync(method, envelope, read, state, id, token, cancellationToken).ConfigureAwait(false);
            }
            catch (ZabbixApiException refusal) when (attempt == 0 && token is { Length: > 0 } && credential.Invalidate(token, refusal))
            {
                ApiLog.SessionRenewing(logger, method, id, refusal.Kind);
                ZabbixLayerTelemetry.Event(operation, "zabbix.api.session.renew");
            }
        }
    }

    private async Task<T> SendOnceAsync<T, TState>(
        string method, ReadOnlyMemory<byte> envelope, ResultReader<TState, T> read, TState state,
        long id, string? token, CancellationToken cancellationToken)
    {
        using var request = new HttpRequestMessage(HttpMethod.Post, endpoint)
        {
            Content = new ReadOnlyMemoryContent(envelope) { Headers = { ContentType = s_jsonRpc } },
        };
        request.Options.Set(ZabbixHttp.Method, method);
        if (_timeouts.TryGetValue(method, out TimeSpan deadline)) request.Options.Set(ZabbixHttp.Deadline, deadline);
        if (token is { Length: > 0 }) request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);

        // The body is already in memory: the innermost handler buffered it inside the pipeline's deadlines.
        using HttpResponseMessage response = await clients.CreateClient(ZabbixHttp.GetClientName(name))
            .SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cancellationToken).ConfigureAwait(false);

        // The API answers its own failures as 200 + an error object; only a reply that is not JSON at all is an
        // HTTP failure, so the status is checked after the body is known not to be an envelope.
        if (!response.IsSuccessStatusCode && response.Content.Headers.ContentType?.MediaType is not ("application/json" or "application/json-rpc"))
            response.EnsureSuccessStatusCode();

        ReadOnlySequence<byte> body = response.Content is ZabbixBufferedContent buffered
            ? buffered.Body
            : new(await response.Content.ReadAsByteArrayAsync(cancellationToken).ConfigureAwait(false));   // a pipeline without the buffering handler
        if (!OpensAnObject(body)) throw NotTheApi(method, id, response.Content.Headers.ContentType?.MediaType);
        return Read(body, method, id, read, state);
    }

    // The API answers every call with a JSON object. A body that opens with anything else is not the API's: an endpoint
    // that names another of the frontend's scripts (index.php, zabbix.php) reaches its pages, which answer 200 with HTML,
    // and the reader's "'<' is an invalid start of a value" would name neither the endpoint nor the cause. (A folder is
    // the frontend's address, and ZabbixClientOptions.GetApiEndpoint has already made it api_jsonrpc.php.)
    private static bool OpensAnObject(ReadOnlySequence<byte> body)
    {
        var reader = new SequenceReader<byte>(body);
        reader.IsNext("﻿"u8, advancePast: true);                                           // a proxy's UTF-8 byte order mark
        reader.AdvancePastAny((byte)' ', (byte)'\t', (byte)'\r', (byte)'\n');
        return reader.TryPeek(out byte first) && first == (byte)'{';
    }

    // The message shows the endpoint without its query, and suggests the api_jsonrpc.php in the script's folder.
    private InvalidDataException NotTheApi(string method, long id, string? mediaType)
    {
        string path = endpoint.AbsolutePath;
        string address = endpoint.GetLeftPart(UriPartial.Path);
        string cause = path.EndsWith("/" + ZabbixClientOptions.ApiScript, StringComparison.OrdinalIgnoreCase)
            ? "the endpoint names api_jsonrpc.php, so something other than the frontend's API answered it"
            : $"the API is the frontend's api_jsonrpc.php, such as {new UriBuilder(address) { Path = path[..(path.LastIndexOf('/') + 1)] + ZabbixClientOptions.ApiScript }.Uri}";
        return new(string.Create(CultureInfo.InvariantCulture, $"The reply to {method} #{id} from {address} is not JSON-RPC ({mediaType ?? "no content type"}): {cause}."));
    }

    // One pass over the buffered bytes, no document: the envelope's members are found and skipped over, then the id is
    // checked first (an error that answers another request is not this call's), then the error, then the result is read
    // in place by the caller's type info. Every member is checked for its JSON kind before it is read, so a reply of the
    // wrong shape is InvalidDataException (error.type invalid_response), never a JsonElement accessor's
    // InvalidOperationException; a reply that opens an object and then is not JSON is the reader's JsonException.
    private static T Read<T, TState>(ReadOnlySequence<byte> body, string method, long id, ResultReader<TState, T> read, TState state)
    {
        // A proxy's UTF-8 byte order mark: JsonDocument skipped it, the reader does not.
        Span<byte> head = stackalloc byte[3];
        if (body.Length >= 3)
        {
            body.Slice(0, 3).CopyTo(head);
            if (head.SequenceEqual("\uFEFF"u8)) body = body.Slice(3);
        }
        var reader = new Utf8JsonReader(body);
        if (!reader.Read() || reader.TokenType != JsonTokenType.StartObject) throw NotTheReply(method, id, "is not a JSON object");

        Utf8JsonReader result = default, error = default;
        bool hasResult = false, hasError = false, hasId = false;
        JsonTokenType echoedKind = JsonTokenType.None;
        long echoed = 0;
        while (reader.Read() && reader.TokenType == JsonTokenType.PropertyName)
        {
            // A member written twice leaves the reply's meaning to whichever a reader keeps (RFC 8259 4: names SHOULD be
            // unique); this reply is not the frontend's, which writes each once (CJsonRpc.php).
            if (reader.ValueTextEquals("result"u8))
            {
                if (hasResult) throw NotTheReply(method, id, "repeats its result");
                reader.Read();
                result = reader;
                hasResult = true;
            }
            else if (reader.ValueTextEquals("error"u8))
            {
                if (hasError) throw NotTheReply(method, id, "repeats its error");
                reader.Read();
                error = reader;
                hasError = true;
            }
            else if (reader.ValueTextEquals("id"u8))
            {
                if (hasId) throw NotTheReply(method, id, "repeats its id");
                hasId = true;
                reader.Read();
                echoedKind = reader.TokenType;
                if (echoedKind == JsonTokenType.Number && !reader.TryGetInt64(out echoed)) echoedKind = JsonTokenType.None;
            }
            else
            {
                reader.Read();
            }
            reader.Skip();
        }
        if (reader.Read()) throw NotTheReply(method, id, "has more after its envelope");       // the reader throws first on most trailing bytes

        // JSON-RPC 2.0 §5: the id is the request's; only an error may carry null, when the server could not read the request's.
        bool ours = echoedKind == JsonTokenType.Number && echoed == id;
        if (!ours && !(hasError && echoedKind == JsonTokenType.Null))
            throw NotTheReply(method, id, "does not carry its id");
        if (hasError) throw Refusal(ref error, method, id);
        if (!hasResult) throw NotTheReply(method, id, "carries neither a result nor an error");
        return read(ref result, state, method);
    }

    private static ZabbixApiException Refusal(ref Utf8JsonReader error, string method, long id)
    {
        if (error.TokenType != JsonTokenType.StartObject) throw NotTheReply(method, id, "carries an error that is not an object");
        using JsonDocument document = JsonDocument.ParseValue(ref error);
        JsonElement refusal = document.RootElement;
        int code = refusal.TryGetProperty("code", out JsonElement c) && c.ValueKind == JsonValueKind.Number && c.TryGetInt32(out int value) ? value : 0;
        string message = !refusal.TryGetProperty("message", out JsonElement m) || m.ValueKind == JsonValueKind.Null ? "Zabbix API error"
            : m.ValueKind == JsonValueKind.String ? m.GetString() ?? string.Empty : throw NotTheReply(method, id, "carries an error message that is not text");
        string? detail = refusal.TryGetProperty("data", out JsonElement d) && d.ValueKind == JsonValueKind.String ? d.GetString() : null;
        return new ZabbixApiException(code, message, detail, method);
    }

    private static InvalidDataException NotTheReply(string method, long id, string why)
        => new(string.Create(CultureInfo.InvariantCulture, $"The reply to {method} #{id} {why}."));
}
