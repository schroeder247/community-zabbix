// This Source Code Form is subject to the terms of the Mozilla Public
// License, v. 2.0. Copyright (c) 2026 schroeder247. SPDX-License-Identifier: MPL-2.0
using System.Buffers;
using System.Text;
using System.Text.Json;
using Community.Zabbix.Protocol;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace Community.Zabbix;

/// <summary>
/// Queries a passive agent over <c>zabbix_get</c>'s protocol, one key per connection. As <c>zabbix_get</c> does (7.0+,
/// <c>src/zabbix_get/zabbix_get.c</c> <c>get_value</c>, <c>zbx_agent_handle_response</c>), it asks in JSON first —
/// <c>{"request":"passive checks","data":[{"key":…,"timeout":…}]}</c>, so the agent is told the check's timeout — and
/// when the reply is not JSON (an agent older than 7.0 read the request as a key) it asks that agent in plaintext from then
/// on: the value's text, <c>ZBX_NOTSUPPORTED\0reason</c>, or <c>ZBX_ERROR</c>. A façade over a <see cref="ZabbixExchangeCallback"/>.
/// </summary>
/// <remarks>
/// <para>A getter built for a node owns the connection it built, and with it the certificate and key certificate mode read
/// from its files: dispose the getter to release them. A getter over an exchange it was given owns nothing.</para>
/// <para>A getter is thread-safe: each query opens a connection of its own, and what it learns of the agent (that it asks
/// in plaintext) is one flag every call reads.</para>
/// </remarks>
internal sealed class ZabbixGetter : IDisposable
{
    /// <summary>The agent's passive port.</summary>
    public const int DefaultPort = 10050;

    private static ReadOnlySpan<byte> NotSupported => "ZBX_NOTSUPPORTED"u8;
    private static ReadOnlySpan<byte> AgentError => "ZBX_ERROR"u8;
    private readonly ZabbixExchangeCallback _exchange;
    private readonly ZabbixNodeLink? _link;
    private readonly ILogger _logger;
    private volatile bool _plaintext;
    private volatile bool _disposed;

    /// <summary>A client over <paramref name="exchange"/>, which the caller keeps owning, that logs nothing.</summary>
    public ZabbixGetter(ZabbixExchangeCallback exchange, string destination)
        : this(exchange, destination, logger: null)
    {
    }

    /// <summary>A client over <paramref name="exchange"/>, which the caller keeps owning.</summary>
    public ZabbixGetter(ZabbixExchangeCallback exchange, string destination, ILogger? logger)
        : this(exchange, link: null, destination, logger)
    {
    }

    /// <summary>
    /// A client for the agent at <paramref name="host"/>: on its passive port, <see cref="DefaultPort"/>, unless it names a
    /// port. The client owns the connection: dispose it.
    /// </summary>
    public ZabbixGetter(string host)
        : this(ZabbixServerNode.Parse(host, DefaultPort, nameof(host)))
    {
    }

    /// <summary>A client for the agent at <paramref name="host"/>:<paramref name="port"/>, in plaintext with the default deadline. The client owns the connection: dispose it.</summary>
    public ZabbixGetter(string host, int port)
        : this(new ZabbixServerNode(host, port))
    {
    }

    /// <summary>A client for the agent at <paramref name="node"/>. The client owns the connection: dispose it.</summary>
    public ZabbixGetter(ZabbixServerNode node)
        : this(node, connection: null)
    {
    }

    /// <summary>A client for the agent at <paramref name="node"/>. The client owns the connection: dispose it.</summary>
    public ZabbixGetter(ZabbixServerNode node, ZabbixConnectionOptions? connection)
        : this(node, connection, time: null, logger: null)
    {
    }

    /// <summary>A client for the agent at <paramref name="node"/>. The client owns the connection: dispose it.</summary>
    public ZabbixGetter(ZabbixServerNode node, ZabbixConnectionOptions? connection, TimeProvider? time, ILogger? logger)
        : this(node, connection, time, logger, telemetry: null)
    {
    }

    // The hosts pass the container's wire telemetry (the host's meters); a client built by hand reports on the wire's own.
    internal ZabbixGetter(ZabbixServerNode node, ZabbixConnectionOptions? connection, TimeProvider? time, ILogger? logger, ZabbixWireTelemetry? telemetry)
        : this(new ZabbixNodeLink([node], connection, time, logger, telemetry), node.ToString(), logger)
    {
    }

    private ZabbixGetter(ZabbixNodeLink link, string destination, ILogger? logger)
        : this(link.Exchange(followRedirects: false), link, destination, logger)
    {
    }

    private ZabbixGetter(ZabbixExchangeCallback exchange, ZabbixNodeLink? link, string destination, ILogger? logger)
    {
        _link = link;
        try
        {
            ArgumentNullException.ThrowIfNull(exchange);
            ArgumentException.ThrowIfNullOrWhiteSpace(destination);
        }
        catch
        {
            link?.Dispose();
            throw;
        }
        _exchange = exchange;
        Destination = destination;
        _logger = logger ?? NullLogger.Instance;
    }

    /// <summary>The connection this client built and owns; <see langword="null"/> over a given exchange.</summary>
    internal ZabbixNodeLink? Link => _link;

    /// <summary>What the agent is called in logs.</summary>
    public string Destination { get; }

    /// <summary>
    /// The check's timeout the JSON request carries: whole seconds, 1 to 600, as the agent validates it
    /// (<c>zbx_validate_item_timeout</c>); 3 s, the agent's own default, unless said otherwise.
    /// </summary>
    public TimeSpan CheckTimeout { get; init; } = TimeSpan.FromSeconds(3);

    /// <summary>Asks the agent for one key.</summary>
    public Task<ZabbixAgentAnswer> GetAsync(ZabbixItemKey key, CancellationToken cancellationToken)
    {
        // A usage error, so it is thrown here and names the client, not the connection beneath it.
        ObjectDisposedException.ThrowIf(_disposed, this);
        return AskAsync(key, cancellationToken);
    }

    private async Task<ZabbixAgentAnswer> AskAsync(ZabbixItemKey key, CancellationToken cancellationToken)
    {
        ZabbixAgentAnswer answer;
        try
        {
            ZabbixAgentAnswer? asked = _plaintext ? null : await AskInJsonAsync(key, cancellationToken).ConfigureAwait(false);
            answer = asked ?? await AskInPlaintextAsync(key, cancellationToken).ConfigureAwait(false);
        }
        catch (ZabbixAgentRefusedException refused)
        {
            WireLog.GetterRefused(_logger, Destination, key, refused.Error);
            throw;
        }
        catch (Exception failure) when (ZabbixFrames.IsExchangeFailure(failure))
        {
            WireLog.GetterFailed(_logger, Destination, key, failure);
            throw;
        }
        WireLog.GetterAnswered(_logger, Destination, key, answer.IsSupported);
        return answer;
    }

    // zbx_agent_prepare_request + the 7.0+ half of zbx_agent_handle_response; null when the reply is not JSON (an older agent).
    private async Task<ZabbixAgentAnswer?> AskInJsonAsync(ZabbixItemKey key, CancellationToken cancellationToken)
    {
        var request = new ArrayBufferWriter<byte>();
        using (var json = new Utf8JsonWriter(request))
        {
            json.WriteStartObject();
            json.WriteString("request", "passive checks");
            json.WriteStartArray("data");
            json.WriteStartObject();
            json.WriteString("key", key.ToString());
            json.WriteNumber("timeout", (long)CheckTimeout.TotalSeconds);
            json.WriteEndObject();
            json.WriteEndArray();
            json.WriteEndObject();
        }
        ReadOnlyMemory<byte> reply = await _exchange(request.WrittenMemory, cancellationToken).ConfigureAwait(false);
        JsonDocument document;
        try { document = JsonDocument.Parse(reply); }
        catch (JsonException)
        {
            _plaintext = true;                                                             // an agent before 7.0: plaintext from now on
            return null;
        }
        using (document)
        {
            JsonElement root = document.RootElement;
            if (root.ValueKind != JsonValueKind.Object)
            {
                _plaintext = true;
                return null;
            }
            if (root.TryGetProperty("error", out JsonElement refused))
                throw new ZabbixAgentRefusedException(key, TextOf(refused));
            if (!root.TryGetProperty("data", out JsonElement data) || data.ValueKind != JsonValueKind.Array || data.GetArrayLength() == 0
                || data[0] is not { ValueKind: JsonValueKind.Object } row)
                throw new InvalidDataException("The agent's reply has no \"data\" row.");
            if (row.TryGetProperty("error", out JsonElement error))
            {
                string reason = TextOf(error);
                return new ZabbixAgentAnswer(key, null, reason.Length > 0 ? reason : "Not supported by Zabbix Agent");
            }
            if (!row.TryGetProperty("value", out JsonElement value)) throw new InvalidDataException("The agent's reply row has neither a value nor an error.");
            return new ZabbixAgentAnswer(key, value.ValueKind == JsonValueKind.Null ? null : TextOf(value), null);
        }
    }

    private static string TextOf(JsonElement element) => element.ValueKind == JsonValueKind.String ? element.GetString() ?? string.Empty : element.GetRawText();

    // The pre-7.0 half of zbx_agent_handle_response.
    private async Task<ZabbixAgentAnswer> AskInPlaintextAsync(ZabbixItemKey key, CancellationToken cancellationToken)
    {
        ReadOnlyMemory<byte> reply = await _exchange(Encoding.UTF8.GetBytes(key.ToString()), cancellationToken).ConfigureAwait(false);
        if (reply.Span.StartsWith(NotSupported))
        {
            ReadOnlySpan<byte> rest = reply.Span[NotSupported.Length..];
            int separator = rest.IndexOf((byte)0);
            string reason = separator >= 0 ? Encoding.UTF8.GetString(rest[(separator + 1)..]) : string.Empty;
            return new ZabbixAgentAnswer(key, null, reason.Length > 0 ? reason : "Not supported by Zabbix Agent");
        }
        if (reply.Span.SequenceEqual(AgentError)) return new ZabbixAgentAnswer(key, null, "Zabbix Agent non-critical error");   // never a value
        return new ZabbixAgentAnswer(key, Encoding.UTF8.GetString(reply.Span), null);
    }

    /// <inheritdoc cref="GetAsync(ZabbixItemKey, CancellationToken)"/>
    public Task<ZabbixAgentAnswer> GetAsync(ZabbixItemKey key) => GetAsync(key, CancellationToken.None);

    /// <inheritdoc cref="GetAsync(ZabbixItemKey, CancellationToken)"/>
    public Task<ZabbixAgentAnswer> GetAsync(string key) => GetAsync(key, CancellationToken.None);

    /// <inheritdoc cref="GetAsync(ZabbixItemKey, CancellationToken)"/>
    public Task<ZabbixAgentAnswer> GetAsync(string key, CancellationToken cancellationToken) => GetAsync(ZabbixItemKey.Parse(key), cancellationToken);

    /// <summary>Releases the connection the client built (its certificate trust); a client over a given exchange has none.</summary>
    public void Dispose()
    {
        _disposed = true;
        _link?.Dispose();
    }
}
