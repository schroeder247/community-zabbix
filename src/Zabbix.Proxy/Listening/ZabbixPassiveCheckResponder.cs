// This Source Code Form is subject to the terms of the Mozilla Public
// License, v. 2.0. Copyright (c) 2026 schroeder247. SPDX-License-Identifier: MPL-2.0
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using Community.Zabbix.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace Community.Zabbix.Listening;

/// <summary>The passive agent's JSON context.</summary>
[JsonSourceGenerationOptions(DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull, NumberHandling = JsonNumberHandling.AllowReadingFromString)]
[JsonSerializable(typeof(ZabbixPassiveCheckRequest))]
[JsonSerializable(typeof(ZabbixPassiveCheckResponse))]
internal sealed partial class ZabbixPassiveAgentJson : JsonSerializerContext
{
}

/// <summary>The refusal's JSON context: internal, as the refusal is.</summary>
[JsonSerializable(typeof(ZabbixPassiveCheckRefusal))]
internal sealed partial class ZabbixPassiveAgentRefusalJson : JsonSerializerContext
{
}

/// <summary>
/// Answers item keys as a listener's handler: the passive agent. Speaks both forms the server uses — the 7.x JSON
/// <c>passive checks</c> request (several keys, each with its timeout) and the classic one key per connection answered
/// with the value or <c>ZBX_NOTSUPPORTED\0reason</c>, which is what <see cref="ZabbixWireClient"/> sends.
/// </summary>
public sealed class ZabbixPassiveCheckResponder
{
    private static ReadOnlySpan<byte> NotSupported => "ZBX_NOTSUPPORTED\0"u8;

    private readonly ZabbixItemDictionary _handlers;
    private readonly TimeProvider _time;
    private readonly ILogger _logger;

    /// <summary>
    /// A passive agent answering with <paramref name="handlers"/>: a key may take Zabbix's 3 seconds when the request doesn't
    /// say, on the table's clock, and nothing is logged.
    /// </summary>
    /// <param name="handlers">The keys it answers.</param>
    /// <exception cref="ArgumentNullException"><paramref name="handlers"/> is <see langword="null"/>.</exception>
    public ZabbixPassiveCheckResponder(ZabbixItemDictionary handlers)
        : this(handlers, TimeSpan.FromSeconds(3), loggers: null, time: null)
    {
    }

    /// <summary>A passive agent answering with <paramref name="handlers"/>, with the application's logging and clock.</summary>
    /// <param name="handlers">The keys it answers.</param>
    /// <param name="checkTimeout">How long one key may take when the request doesn't say.</param>
    /// <param name="loggers">Where to log; nowhere when <see langword="null"/>. The caller keeps owning it.</param>
    /// <param name="time">The clock deadlines run on; the table's when <see langword="null"/>.</param>
    /// <exception cref="ArgumentNullException"><paramref name="handlers"/> is <see langword="null"/>.</exception>
    public ZabbixPassiveCheckResponder(ZabbixItemDictionary handlers, TimeSpan checkTimeout, ILoggerFactory? loggers, TimeProvider? time)
    {
        ArgumentNullException.ThrowIfNull(handlers);
        _handlers = handlers;
        CheckTimeout = checkTimeout;
        _time = time ?? handlers.Time;
        _logger = (ILogger?)loggers?.CreateLogger<ZabbixPassiveCheckResponder>() ?? NullLogger<ZabbixPassiveCheckResponder>.Instance;
    }

    /// <summary>How long one key may take when the request does not say; Zabbix's agent default.</summary>
    public TimeSpan CheckTimeout { get; }

    /// <summary>The handler a <see cref="ZabbixListener"/> takes.</summary>
    public ZabbixRequestCallback Handler => HandleAsync;

    private async ValueTask<ReadOnlyMemory<byte>> HandleAsync(ZabbixRequest request, CancellationToken cancellationToken)
    {
        ReadOnlyMemory<byte> payload = request.Payload;
        if (payload.Length > 0 && payload.Span[0] == (byte)'{' && IsJson(payload.Span))
        {
            // listener.c process_passive_checks_json (8.0:54-144): JSON that is not a readable request is answered in JSON
            // with the reason; JSON that does not parse at all falls to the classic form below, as upstream reads it as a key.
            ZabbixPassiveCheckRequest? frame;
            try { frame = JsonSerializer.Deserialize(payload.Span, ZabbixPassiveAgentJson.Default.ZabbixPassiveCheckRequest); }
            catch (JsonException unreadable) { return ErrorReply($"cannot read the passive checks request: {unreadable.Message}"); }
            if (frame?.Request is not { } kind) return ErrorReply("cannot find the \"request\" object in the received JSON object");
            if (kind != "passive checks") return ErrorReply($"unknown request \"{kind}\"");
            if (frame.Data is null) return ErrorReply("cannot find the \"data\" array in the received JSON object");
            if (frame.Data.Count == 0) return ErrorReply("received empty \"data\" tag");
            var results = new List<ZabbixPassiveCheckResult>(frame.Data.Count);
            foreach (ZabbixPassiveCheckItem item in frame.Data)
            {
                if (item.Timeout is < 1 or > 600)
                {
                    results.Add(new ZabbixPassiveCheckResult(null, "Unsupported timeout value."));   // zbx_validate_item_timeout
                    continue;
                }
                ZabbixAgentAnswer answer = await AnswerAsync(item.Key, item.Timeout is { } seconds ? TimeSpan.FromSeconds(seconds) : CheckTimeout, cancellationToken).ConfigureAwait(false);
                results.Add(new ZabbixPassiveCheckResult(answer.Value, answer.Error));
            }
            return JsonSerializer.SerializeToUtf8Bytes(new ZabbixPassiveCheckResponse(ZabbixWire.AgentVersion, ZabbixWire.AgentVariant, results), ZabbixPassiveAgentJson.Default.ZabbixPassiveCheckResponse);
        }

        // The classic form: one key, the value back, or ZBX_NOTSUPPORTED followed by a NUL and the reason.
        string text = Encoding.UTF8.GetString(payload.Span).TrimEnd('\r', '\n');
        ZabbixAgentAnswer one = await AnswerAsync(text, CheckTimeout, cancellationToken).ConfigureAwait(false);
        return one.Value is { } value ? Encoding.UTF8.GetBytes(value) : NotSupportedReply(one.Error ?? "not supported");
    }

    private async ValueTask<ZabbixAgentAnswer> AnswerAsync(string? text, TimeSpan timeout, CancellationToken cancellationToken)
    {
        if (!ZabbixItemKey.TryParse(text, out ZabbixItemKey key)) return ZabbixAgentAnswer.NotSupported(default, "Invalid item key format.");
        ZabbixAgentAnswer answer = await _handlers.AnswerLoggedAsync(key, timeout, _time, _logger, cancellationToken).ConfigureAwait(false);
        ListenerLog.PassiveCheckAnswered(_logger, key, answer.IsSupported);
        return answer;
    }

    private static ReadOnlyMemory<byte> ErrorReply(string error)
        => JsonSerializer.SerializeToUtf8Bytes(new ZabbixPassiveCheckRefusal(ZabbixWire.AgentVersion, ZabbixWire.AgentVariant, error), ZabbixPassiveAgentRefusalJson.Default.ZabbixPassiveCheckRefusal);

    private static bool IsJson(ReadOnlySpan<byte> payload)
    {
        var reader = new Utf8JsonReader(payload);
        try
        {
            if (!reader.Read() || reader.TokenType != JsonTokenType.StartObject) return false;
            reader.Skip();
            return !reader.Read();                                                          // one object, nothing after it
        }
        catch (JsonException)
        {
            return false;
        }
    }

    private static ReadOnlyMemory<byte> NotSupportedReply(string reason)
    {
        ReadOnlySpan<byte> prefix = NotSupported;
        byte[] text = Encoding.UTF8.GetBytes(reason);
        byte[] reply = new byte[prefix.Length + text.Length];
        prefix.CopyTo(reply);
        text.CopyTo(reply.AsSpan(prefix.Length));
        return reply;
    }
}
