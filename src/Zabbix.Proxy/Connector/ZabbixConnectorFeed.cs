// This Source Code Form is subject to the terms of the Mozilla Public
// License, v. 2.0. Copyright (c) 2026 schroeder247. SPDX-License-Identifier: MPL-2.0
using System.Net;
using System.Runtime.CompilerServices;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization.Metadata;
using System.Threading.Channels;
using Community.Zabbix.Listening;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace Community.Zabbix.Connector;

/// <summary>
/// Reads a Zabbix connector's newline-delimited JSON stream (one item value or one event per line) as records, and
/// hosts the endpoint the connector posts it to.
/// </summary>
/// <remarks>
/// Named apart from the generated <c>ZabbixConnector</c> (the API's <c>connector</c> object). Lines are read as
/// top-level JSON values, each into the <see cref="ZabbixConnectorRecord"/> its fields name; <see cref="MapZabbixConnector"/> maps one
/// minimal-API endpoint that writes each record into a channel. The endpoint fails closed: it is mapped with a bearer
/// token, a peer list or an explicit anonymous opt-in (<see cref="ZabbixConnectorOptions"/>).
/// <para>Delivery is at least once, never exactly once. The server re-posts the whole body on any answer outside
/// 200–204 and 400/401/403/404/405/415/422, and on a transport failure (<c>zbx_http_request_sync_perform</c>,
/// <c>src/libs/zbxhttp/http.c</c>), so records can arrive twice: a consumer dedups by
/// <c>itemid</c>+<c>clock</c>+<c>ns</c> or by <c>eventid</c>. A body with a bad line is answered 400 with
/// <c>{"error":…}</c> before any record is written, since the server does not retry 400: it logs the <c>error</c> tag
/// (<c>connector_worker.c</c>) and drops the batch, so writing half of it would only make the loss partial and silent.</para>
/// </remarks>
public static class ZabbixConnectorFeed
{
    private static readonly JsonTypeInfo<ZabbixConnectorRecord> s_record = ZabbixConnectorJson.Default.ZabbixConnectorRecord;

    /// <summary>One line as a record; <see cref="JsonException"/> when it is neither an item value nor an event.</summary>
    /// <param name="line">The JSON.</param>
    public static ZabbixConnectorRecord Parse(ReadOnlySpan<byte> line)
        => JsonSerializer.Deserialize(line, s_record) ?? throw new JsonException("A connector line is an object, not null.");

    /// <summary>The records in a body of NDJSON, as they arrive.</summary>
    /// <param name="body">The stream.</param>
    /// <param name="cancellationToken">Stops reading.</param>
    public static IAsyncEnumerable<ZabbixConnectorRecord> ReadAsync(Stream body, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(body);
        return ReadCoreAsync(body, cancellationToken);
    }

    private static async IAsyncEnumerable<ZabbixConnectorRecord> ReadCoreAsync(Stream body, [EnumeratorCancellation] CancellationToken cancellationToken)
    {
        await foreach (ZabbixConnectorRecord? record in JsonSerializer.DeserializeAsyncEnumerable(body, s_record, topLevelValues: true, cancellationToken).ConfigureAwait(false))
            yield return record ?? throw new JsonException("A connector line is an object, not null.");
    }

    /// <summary>The records in a text of NDJSON — for a file or a test; <see cref="JsonException"/> at the first bad line.</summary>
    /// <param name="ndjson">The lines.</param>
    public static IReadOnlyList<ZabbixConnectorRecord> Parse(string ndjson)
    {
        ArgumentNullException.ThrowIfNull(ndjson);
        var reader = new Utf8JsonReader(Encoding.UTF8.GetBytes(ndjson), new JsonReaderOptions { AllowMultipleValues = true });
        var records = new List<ZabbixConnectorRecord>();
        while (reader.Read())
            records.Add(JsonSerializer.Deserialize(ref reader, s_record) ?? throw new JsonException("A connector line is an object, not null."));
        return records;
    }

    /// <summary>
    /// The connector's endpoint: the server POSTs its stream here. A request is first admitted by
    /// <paramref name="options"/> (a missing or wrong bearer token is answered 401, a peer outside the list 403, neither
    /// retried by the server). The body is then read whole: a bad line is answered 400 with <c>{"error":…}</c> and nothing
    /// is written. Then every record goes into <paramref name="sink"/> (<c>Wait</c> on a full sink paces the server and
    /// loses nothing; a drop policy loses and counts); a completed sink is answered 503 with <c>{"error":…}</c>, and the
    /// server re-sends the whole body (see the type's remarks: at least once).
    /// </summary>
    /// <param name="endpoints">The route builder.</param>
    /// <param name="pattern">The path, e.g. <c>/zabbix/connector</c>.</param>
    /// <param name="sink">Where the records go.</param>
    /// <param name="options">Who may post: a bearer token, a peer list, or an explicit <see cref="ZabbixConnectorOptions.AllowAnonymous"/>.</param>
    /// <returns>The endpoint's builder, for the application's own conventions (authorization, rate limiting).</returns>
    /// <exception cref="ArgumentException"><paramref name="options"/> names none of the three, or its peer list does not parse: the endpoint fails closed.</exception>
    public static IEndpointConventionBuilder MapZabbixConnector(this IEndpointRouteBuilder endpoints, string pattern, ChannelWriter<ZabbixConnectorRecord> sink, ZabbixConnectorOptions options)
    {
        ArgumentNullException.ThrowIfNull(endpoints);
        ArgumentNullException.ThrowIfNull(sink);
        ArgumentNullException.ThrowIfNull(options);
        Admission admission = Admission.Of(options);
        // The RequestDelegate overload: the delegate-parameter form reflects over the handler and is not AOT-safe.
        return endpoints.MapPost(pattern, async http =>
        {
            ILogger logger = http.RequestServices.GetRequiredService<ILoggerFactory>().CreateLogger(typeof(ZabbixConnectorFeed));
            if (await admission.RefusalAsync(http).ConfigureAwait(false) is { } refusal)
            {
                IPAddress peer = http.Connection.RemoteIpAddress ?? IPAddress.None;
                ListenerLog.ConnectorRefused(logger, peer, refusal.Reason);
                if (refusal.Status == StatusCodes.Status401Unauthorized) http.Response.Headers.WWWAuthenticate = "Bearer";
                await AnswerAsync(http, refusal.Status, refusal.Reason).ConfigureAwait(false);
                return;
            }
            var records = new List<ZabbixConnectorRecord>();
            try
            {
                await foreach (ZabbixConnectorRecord record in ReadAsync(http.Request.Body, http.RequestAborted).ConfigureAwait(false))
                    records.Add(record);
            }
            catch (JsonException failure)
            {
                ListenerLog.ConnectorLineRejected(logger, failure.Message);
                await AnswerAsync(http, StatusCodes.Status400BadRequest, failure.Message).ConfigureAwait(false);
                return;
            }
            int taken = 0;
            try
            {
                foreach (ZabbixConnectorRecord record in records)
                {
                    await sink.WriteAsync(record, http.RequestAborted).ConfigureAwait(false);
                    taken++;
                }
            }
            catch (ChannelClosedException)
            {
                ListenerLog.ConnectorSinkClosed(logger, taken, records.Count);
                await AnswerAsync(http, StatusCodes.Status503ServiceUnavailable, "the receiver is not taking records").ConfigureAwait(false);
                return;
            }
            ListenerLog.ConnectorReceived(logger, taken);
            http.Response.StatusCode = StatusCodes.Status200OK;
        });
    }

    /// <summary>
    /// The endpoint's admission, read once at mapping. The token is compared as SHA-256 digests with
    /// <see cref="CryptographicOperations.FixedTimeEquals"/>, so neither its bytes nor its length show in the timing.
    /// </summary>
    private sealed class Admission
    {
        private readonly byte[]? _token;
        private readonly ZabbixAllowedPeers? _peers;

        private Admission(byte[]? token, ZabbixAllowedPeers? peers)
        {
            _token = token;
            _peers = peers;
        }

        public static Admission Of(ZabbixConnectorOptions options)
        {
            bool token = !string.IsNullOrEmpty(options.BearerToken);
            bool peers = options.AllowedPeers.Count > 0;
            if (!token && !peers && !options.AllowAnonymous)
                throw new ArgumentException("A connector endpoint needs a bearer token, a peer list, or AllowAnonymous set: it fails closed.", nameof(options));
            ZabbixAllowedPeers? allowed = null;
            if (peers && !ZabbixAllowedPeers.TryParse(string.Join(',', options.AllowedPeers), out allowed, out string? error))
                throw new ArgumentException(error, nameof(options));
            return new Admission(token ? SHA256.HashData(Encoding.UTF8.GetBytes(options.BearerToken!)) : null, allowed);
        }

        public async ValueTask<(int Status, string Reason)?> RefusalAsync(HttpContext http)
        {
            if (_peers is not null && !await _peers.ContainsAsync(http.Connection.RemoteIpAddress ?? IPAddress.None, http.RequestAborted).ConfigureAwait(false))
                return (StatusCodes.Status403Forbidden, "the peer is not allowed");
            if (_token is not null && !Presents(http.Request.Headers.Authorization.ToString()))
                return (StatusCodes.Status401Unauthorized, "the bearer token is missing or wrong");
            return null;
        }

        private bool Presents(string authorization)
        {
            const string Scheme = "Bearer ";
            if (!authorization.StartsWith(Scheme, StringComparison.OrdinalIgnoreCase)) return false;
            Span<byte> presented = stackalloc byte[SHA256.HashSizeInBytes];
            SHA256.HashData(Encoding.UTF8.GetBytes(authorization[Scheme.Length..].Trim()), presented);
            return CryptographicOperations.FixedTimeEquals(presented, _token);
        }
    }

    private static Task AnswerAsync(HttpContext http, int status, string error)
    {
        http.Response.StatusCode = status;
        return http.Response.WriteAsJsonAsync(new ZabbixConnectorError(error), ZabbixConnectorJson.Default.ZabbixConnectorError, contentType: null, http.RequestAborted);
    }
}
