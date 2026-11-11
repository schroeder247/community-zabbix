// This Source Code Form is subject to the terms of the Mozilla Public
// License, v. 2.0. Copyright (c) 2026 schroeder247. SPDX-License-Identifier: MPL-2.0
using System.Globalization;
using System.Text.Json;
using System.Text.Json.Serialization;
using Community.Zabbix.Diagnostics;
using Community.Zabbix.Protocol;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace Community.Zabbix;

/// <summary>The server wire's JSON context.</summary>
[JsonSourceGenerationOptions(DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull, NumberHandling = JsonNumberHandling.AllowReadingFromString)]
[JsonSerializable(typeof(ZabbixServerReply))]
[JsonSerializable(typeof(ZabbixServerStatistics))]
[JsonSerializable(typeof(ZabbixStatusSections))]
[JsonSerializable(typeof(ZabbixItemTestRequest))]
[JsonSerializable(typeof(ZabbixItemTestResult))]
[JsonSerializable(typeof(ZabbixReportTestRequest))]
[JsonSerializable(typeof(ZabbixReportTestResult))]
[JsonSerializable(typeof(ZabbixAlertSendResult))]
[JsonSerializable(typeof(IReadOnlyList<ZabbixExpressionResult>))]
[JsonSerializable(typeof(IReadOnlyList<ZabbixQueueDelays>))]
[JsonSerializable(typeof(IReadOnlyList<ZabbixQueuedItem>))]
[JsonSerializable(typeof(JsonElement))]
internal sealed partial class ZabbixServerWireJson : JsonSerializerContext
{
}

/// <summary>
/// Sends the server's own wire requests: what the frontend's <c>CZabbixServer.php</c> asks a Zabbix server on its
/// trapper port with a session id (a <c>user.login</c> session, or an API token, which the trapper accepts as one) —
/// <c>queue.get</c>, <c>status.get</c>, <c>item.test</c>, <c>alert.send</c>, <c>report.test</c>,
/// <c>expressions.evaluate</c> — and <c>zabbix.stats</c>, which needs no session but an allowed address
/// (<c>StatsAllowedIP</c>). Each request is built in the shape <c>trapper.c</c> reads: <c>type</c> and <c>limit</c> at
/// the root, <c>data</c> only where the handler reads it. A <c>failed</c> reply throws
/// <see cref="ZabbixServerRefusedException"/>; a reply that is not the server's shape throws
/// <see cref="InvalidDataException"/>; a failed exchange throws what the sender and the getter throw for it
/// (<see cref="System.Net.Sockets.SocketException"/>, <see cref="IOException"/>,
/// <see cref="System.Security.Authentication.AuthenticationException"/>, <see cref="TimeoutException"/>), with the request
/// and the endpoint in its <see cref="Exception.Data"/> under <c>Zabbix.Request</c> and <c>Zabbix.Node</c>. Every request is
/// a span and a duration on the client's telemetry (<see cref="Diagnostics.ZabbixTelemetryNames.Wire"/>), which name the
/// request and the endpoint.
/// </summary>
/// <remarks>
/// A server wire is thread-safe: its session and node are fixed when it is built, and each request opens a connection of
/// its own.
/// </remarks>
internal sealed class ZabbixServerWire : IDisposable
{
    private readonly ZabbixExchangeCallback _exchange;
    private readonly ZabbixServerNode? _node;
    private readonly string _nodeName;

    // The keys a failed exchange's Data carries; the class summary documents them.
    private const string RequestDataKey = "Zabbix.Request";
    private const string NodeDataKey = "Zabbix.Node";
    private readonly ILogger _logger;
    private readonly ZabbixWireTelemetry _telemetry;
    private readonly bool _ownsTelemetry;
    private readonly ZabbixNodeLink? _link;
    private volatile bool _disposed;

    // A credential: sent with every request that needs a session, never exposed or logged.
    [ZabbixSecret]
    private readonly string _sessionId;

    /// <summary>A client over <paramref name="exchange"/>, with <paramref name="sessionId"/> for the requests that need one, that logs nothing.</summary>
    public ZabbixServerWire(ZabbixExchangeCallback exchange, [ZabbixSecret] string? sessionId)
        : this(exchange, sessionId, logger: null, node: null)
    {
    }

    /// <summary>A client over <paramref name="exchange"/>, with <paramref name="sessionId"/> for the requests that need one.</summary>
    public ZabbixServerWire(ZabbixExchangeCallback exchange, [ZabbixSecret] string? sessionId, ILogger? logger, ZabbixServerNode? node)
        : this(exchange, node, sessionId, logger, new ZabbixWireTelemetry(null, null), ownsTelemetry: true)
    {
    }

    // The container's wire telemetry (the host's meters); the client owns one (and disposes it) when null.
    internal ZabbixServerWire(ZabbixExchangeCallback exchange, [ZabbixSecret] string? sessionId, ILogger? logger, ZabbixWireTelemetry? telemetry, ZabbixServerNode? node)
        : this(exchange, node, sessionId, logger, telemetry ?? new ZabbixWireTelemetry(null, null), ownsTelemetry: telemetry is null)
    {
    }

    /// <summary>A client to <paramref name="node"/>.</summary>
    public ZabbixServerWire(ZabbixServerNode node)
        : this(node, sessionId: null)
    {
    }

    /// <summary>A client to the server at <paramref name="node"/>: on <see cref="ZabbixServerNode.DefaultPort"/> unless it names a port.</summary>
    public ZabbixServerWire(string node)
        : this(node, sessionId: null)
    {
    }

    /// <summary>A client to the server at <paramref name="node"/>: on <see cref="ZabbixServerNode.DefaultPort"/> unless it names a port.</summary>
    public ZabbixServerWire(string node, [ZabbixSecret] string? sessionId)
        : this(ZabbixServerNode.Parse(node, ZabbixServerNode.DefaultPort, nameof(node)), sessionId)
    {
    }

    /// <summary>A client to <paramref name="node"/>.</summary>
    public ZabbixServerWire(ZabbixServerNode node, [ZabbixSecret] string? sessionId)
        : this(node, sessionId, connection: null)
    {
    }

    /// <summary>A client to <paramref name="node"/>.</summary>
    public ZabbixServerWire(ZabbixServerNode node, [ZabbixSecret] string? sessionId, ZabbixConnectionOptions? connection)
        : this(node, sessionId, connection, time: null, logger: null)
    {
    }

    /// <summary>A client to <paramref name="node"/>.</summary>
    public ZabbixServerWire(ZabbixServerNode node, [ZabbixSecret] string? sessionId, ZabbixConnectionOptions? connection, TimeProvider? time, ILogger? logger)
        : this(node, sessionId, connection, time, logger, telemetry: null)
    {
    }

    // The container's wire telemetry (the host's meters); the client owns one (and disposes it) when null.
    internal ZabbixServerWire(ZabbixServerNode node, [ZabbixSecret] string? sessionId, ZabbixConnectionOptions? connection, TimeProvider? time, ILogger? logger, ZabbixWireTelemetry? telemetry)
        : this(node, sessionId, connection, time, logger, telemetry ?? new ZabbixWireTelemetry(null, time), ownsTelemetry: telemetry is null)
    {
    }

    // The exchange reports on the client's own telemetry, so its spans and durations go where the client's requests go.
    private ZabbixServerWire(ZabbixServerNode node, [ZabbixSecret] string? sessionId, ZabbixConnectionOptions? connection, TimeProvider? time, ILogger? logger, ZabbixWireTelemetry telemetry, bool ownsTelemetry)
        : this(OwnedLink(node, connection, time, logger, telemetry, ownsTelemetry), node, sessionId, logger, telemetry, ownsTelemetry)
    {
    }

    private ZabbixServerWire(ZabbixNodeLink link, ZabbixServerNode node, [ZabbixSecret] string? sessionId, ILogger? logger, ZabbixWireTelemetry telemetry, bool ownsTelemetry)
        : this(link.Exchange(followRedirects: false), node, sessionId, logger, telemetry, ownsTelemetry)
    {
        _link = link;
    }

    private ZabbixServerWire(ZabbixExchangeCallback exchange, ZabbixServerNode? node, [ZabbixSecret] string? sessionId, ILogger? logger, ZabbixWireTelemetry telemetry, bool ownsTelemetry)
    {
        _telemetry = telemetry;
        _ownsTelemetry = ownsTelemetry;
        if (exchange is null)
        {
            if (ownsTelemetry) telemetry.Dispose();
            throw new ArgumentNullException(nameof(exchange));
        }
        _exchange = exchange;
        _node = node;
        _nodeName = node?.ToString() ?? "(unresolved)";
        _sessionId = sessionId ?? string.Empty;
        _logger = logger ?? NullLogger.Instance;
    }

    /// <summary>The connection this client built and owns; <see langword="null"/> over a given exchange.</summary>
    internal ZabbixNodeLink? Link => _link;

    /// <summary><c>zabbix.stats</c>: the server's internal statistics (<c>StatsAllowedIP</c> must admit this address).</summary>
    public Task<ZabbixServerStatistics> GetStatisticsAsync(CancellationToken cancellationToken = default)
        => ExchangeAsync("zabbix.stats", static writer => { }, session: false,
            static (reply, _) => Required(reply.Data, "zabbix.stats", "data").Deserialize(ZabbixServerWireJson.Default.ZabbixServerStatistics) ?? throw Invalid("zabbix.stats", "the data is null."), cancellationToken);

    /// <summary>
    /// <c>zabbix.stats</c> as the server sent it: what the agent's <c>zabbix.stats[...]</c> item answers, which hands the
    /// reply on as text.
    /// </summary>
    public Task<JsonElement> GetStatisticsDocumentAsync(CancellationToken cancellationToken = default)
        => ExchangeAsync("zabbix.stats", static writer => { }, session: false, static (reply, _) => Required(reply.Data, "zabbix.stats", "data"), cancellationToken);

    /// <summary>
    /// <c>zabbix.stats</c> with <c>type: queue</c>: how many items are overdue by at least <paramref name="fromDelay"/> (6 s by
    /// default) and, when given, at most <paramref name="toDelay"/>.
    /// </summary>
    public Task<long> GetQueueSizeAsync(TimeSpan? fromDelay = null, TimeSpan? toDelay = null, CancellationToken cancellationToken = default)
    {
        return ExchangeAsync("zabbix.stats", writer =>
        {
            writer.WriteString("type", "queue");
            writer.WriteStartObject("params");
            if (fromDelay is { } f) writer.WriteString("from", Seconds(f));
            if (toDelay is { } t) writer.WriteString("to", Seconds(t));
            writer.WriteEndObject();
        }, session: false, static (_, root) => root.TryGetProperty("queue", out JsonElement queue) && queue.ValueKind == JsonValueKind.Number && queue.TryGetInt64(out long size)
            ? size
            : throw Invalid("zabbix.stats", "the reply has no queue count."), cancellationToken);
    }

    /// <summary><c>queue.get</c> as an overview: overdue items counted by delay, grouped by item type or by proxy (super admin only).</summary>
    public Task<IReadOnlyList<ZabbixQueueDelays>> GetQueueAsync(ZabbixQueueOverview overview = ZabbixQueueOverview.ByItemType, CancellationToken cancellationToken = default)
    {
        string type = overview switch
        {
            ZabbixQueueOverview.ByItemType => "overview",
            ZabbixQueueOverview.ByProxy => "overview by proxy",
            _ => throw new ArgumentOutOfRangeException(nameof(overview), overview, "The value is not a queue overview."),
        };
        return ExchangeAsync<IReadOnlyList<ZabbixQueueDelays>>("queue.get", writer => writer.WriteString("type", type), session: true,
            static (reply, _) => Required(reply.Data, "queue.get", "data").Deserialize(ZabbixServerWireJson.Default.IReadOnlyListZabbixQueueDelays) ?? [], cancellationToken);
    }

    /// <summary><c>queue.get</c> in detail: the most overdue items, up to <paramref name="limit"/>, and the total (super admin only).</summary>
    public Task<ZabbixQueueDetails> GetQueueDetailsAsync(int limit, CancellationToken cancellationToken = default)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(limit);
        return ExchangeAsync("queue.get", writer =>
        {
            writer.WriteString("type", "details");
            writer.WriteNumber("limit", limit);
        }, session: true, static (reply, _) =>
        {
            IReadOnlyList<ZabbixQueuedItem> items = Required(reply.Data, "queue.get", "data").Deserialize(ZabbixServerWireJson.Default.IReadOnlyListZabbixQueuedItem) ?? [];
            return new ZabbixQueueDetails(items, reply.Total ?? items.Count);
        }, cancellationToken);
    }

    /// <summary><c>status.get</c> with <c>type: full</c>: the "System information" statistics (empty for a non-super-admin session).</summary>
    public Task<ZabbixServerStatus> GetStatusAsync(CancellationToken cancellationToken = default)
        => ExchangeAsync("status.get", static writer => writer.WriteString("type", "full"), session: true,
            static (reply, _) => ZabbixServerStatus.From(Required(reply.Data, "status.get", "data").Deserialize(ZabbixServerWireJson.Default.ZabbixStatusSections) ?? throw Invalid("status.get", "the data is null.")), cancellationToken);

    /// <summary><c>status.get</c> with <c>type: ping</c>: whether the server is running and takes this session (the frontend's <c>isRunning</c>).</summary>
    public Task PingAsync(CancellationToken cancellationToken = default)
        => ExchangeAsync("status.get", static writer => writer.WriteString("type", "ping"), session: true, static (reply, _) => reply, cancellationToken);

    /// <summary><c>item.test</c>: the server runs one item's collection and preprocessing as the frontend's "Test" does.</summary>
    public Task<ZabbixItemTestResult> TestItemAsync(ZabbixItemTestRequest request, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        return ExchangeAsync("item.test", writer =>
        {
            writer.WritePropertyName("data");
            JsonSerializer.Serialize(writer, request, ZabbixServerWireJson.Default.ZabbixItemTestRequest);
        }, session: true, static (reply, _) => Required(reply.Data, "item.test", "data").Deserialize(ZabbixServerWireJson.Default.ZabbixItemTestResult) ?? throw Invalid("item.test", "the data is null."), cancellationToken);
    }

    /// <summary>
    /// <c>alert.send</c>: a test message through a media type, as the frontend's "Test" sends it
    /// (<c>CControllerMediatypeTestSend</c>): a script media type's parameters as a list of values, a webhook's as an object
    /// of name to value (the server reads <c>parameters</c> as the brackets they are, <c>trapper_process_alert_send</c>).
    /// </summary>
    public Task<ZabbixAlertSendResult> SendAlertAsync(ulong mediaTypeId, string sendTo, string subject, string message, ZabbixAlertParameters? parameters = null, CancellationToken cancellationToken = default)
    {
        return ExchangeAsync("alert.send", writer =>
        {
            writer.WriteStartObject("data");
            writer.WriteString("mediatypeid", mediaTypeId.ToString(CultureInfo.InvariantCulture));
            writer.WriteString("sendto", sendTo);
            writer.WriteString("subject", subject);
            writer.WriteString("message", message);
            parameters?.WriteTo(writer);
            writer.WriteEndObject();
        }, session: true, static (_, root) => root.Deserialize(ZabbixServerWireJson.Default.ZabbixAlertSendResult) ?? throw Invalid("alert.send", "the reply is null."), cancellationToken);
    }

    /// <summary><c>report.test</c>: a scheduled report generated and sent now.</summary>
    public Task<ZabbixReportTestResult> TestReportAsync(ZabbixReportTestRequest request, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        return ExchangeAsync("report.test", writer =>
        {
            writer.WritePropertyName("data");
            JsonSerializer.Serialize(writer, request, ZabbixServerWireJson.Default.ZabbixReportTestRequest);
        }, session: true, static (reply, _) => Required(reply.Data, "report.test", "data").Deserialize(ZabbixServerWireJson.Default.ZabbixReportTestResult) ?? throw Invalid("report.test", "the data is null."), cancellationToken);
    }

    /// <summary><c>expressions.evaluate</c>: trigger expressions evaluated by the server.</summary>
    public Task<IReadOnlyList<ZabbixExpressionResult>> EvaluateExpressionsAsync(IEnumerable<string> expressions, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(expressions);
        return ExchangeAsync<IReadOnlyList<ZabbixExpressionResult>>("expressions.evaluate", writer =>
        {
            writer.WriteStartObject("data");
            writer.WriteStartArray("expressions");
            foreach (string expression in expressions) writer.WriteStringValue(expression);
            writer.WriteEndArray();
            writer.WriteEndObject();
        }, session: true, static (reply, _) => Required(reply.Data, "expressions.evaluate", "data").Deserialize(ZabbixServerWireJson.Default.IReadOnlyListZabbixExpressionResult) ?? [], cancellationToken);
    }

    /// <summary>Any request whose handler reads a <c>data</c> object, in the server's own form, with the session id.</summary>
    public Task<ZabbixServerReply> SendRequestAsync(string request, JsonElement data, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(request);
        return ExchangeAsync(request, writer =>
        {
            writer.WritePropertyName("data");
            data.WriteTo(writer);
        }, session: true, static (reply, _) => reply, cancellationToken);
    }

    /// <summary>
    /// One request: framed, exchanged, and read by <paramref name="read"/> inside the span, so a reply that says success
    /// but lacks what the request promises fails the span and the duration too.
    /// </summary>
    private Task<T> ExchangeAsync<T>(string request, Action<Utf8JsonWriter> body, bool session, Func<ZabbixServerReply, JsonElement, T> read, CancellationToken cancellationToken)
    {
        // A usage error, so it is thrown here and not inside the returned task.
        ObjectDisposedException.ThrowIf(_disposed, this);
        return ExchangeCoreAsync(request, body, session, read, cancellationToken);
    }

    private async Task<T> ExchangeCoreAsync<T>(string request, Action<Utf8JsonWriter> body, bool session, Func<ZabbixServerReply, JsonElement, T> read, CancellationToken cancellationToken)
    {
        // Written straight into the buffer the exchange sends, never copied out of a stream.
        var buffer = new System.Buffers.ArrayBufferWriter<byte>();
        using (var writer = new Utf8JsonWriter(buffer))
        {
            writer.WriteStartObject();
            writer.WriteString("request", request);
            if (session && _sessionId.Length > 0) writer.WriteString("sid", _sessionId);
            body(writer);
            writer.WriteEndObject();
        }
        ReadOnlyMemory<byte> payload = buffer.WrittenMemory;

        ZabbixOperation operation = _telemetry.StartServerWireRequest(request, _node);
        try
        {
            ReadOnlyMemory<byte> answer = await _exchange(payload, cancellationToken).ConfigureAwait(false);
            JsonElement root;
            try
            {
                using JsonDocument document = JsonDocument.Parse(answer);
                root = document.RootElement.Clone();
            }
            catch (JsonException malformed)
            {
                throw new InvalidDataException($"The server's reply to {request} is not JSON.", malformed);
            }
            if (root.ValueKind != JsonValueKind.Object) throw Invalid(request, "the reply is not an object.");
            ZabbixServerReply reply;
            try
            {
                reply = root.Deserialize(ZabbixServerWireJson.Default.ZabbixServerReply) ?? throw Invalid(request, "the reply is null.");
            }
            catch (JsonException wrong)
            {
                throw Invalid(request, wrong.Message, wrong);                  // a member of the wrong kind ("info": 5)
            }
            if (string.Equals(reply.Response, "failed", StringComparison.Ordinal) && reply.Info is not null)
            {
                WireLog.ServerWireRefused(_logger, request, reply.Info);
                throw new ZabbixServerRefusedException(request, reply.Info);
            }
            if (!string.Equals(reply.Response, "success", StringComparison.Ordinal))
                throw Invalid(request, "the reply is neither success nor a failure with info.");
            T result;
            try
            {
                result = read(reply, root);
            }
            catch (Exception wrong) when (wrong is JsonException or InvalidOperationException && wrong is not ZabbixServerRefusedException)
            {
                // The typed read of `data` (a string where a list belongs, "abc" as an item id) is JsonException, and a
                // JsonElement accessor on the wrong kind is InvalidOperationException: both are this reply, not the caller.
                throw Invalid(request, wrong.Message, wrong);
            }
            WireLog.ServerWireAnswered(_logger, request, "success");
            _telemetry.Finish(operation);
            return result;
        }
        catch (ZabbixServerRefusedException refused)
        {
            _telemetry.Finish(operation, refused);
            throw;
        }
        catch (Exception failure) when (failure is not OperationCanceledException)
        {
            // A failed exchange is thrown as the exchange raised it, the most specific standard type (a TimeoutException is
            // a TimeoutException here as from the sender and the getter). Its own message names neither the server nor
            // the call, so both go on its Data, the log event and the span.
            failure.Data[RequestDataKey] = request;
            failure.Data[NodeDataKey] = _nodeName;
            WireLog.ServerWireFailed(_logger, request, _nodeName, failure);
            _telemetry.Finish(operation, failure);
            throw;
        }
        catch (OperationCanceledException cancelled)
        {
            _telemetry.Finish(operation, cancelled);
            throw;
        }
    }

    private static JsonElement Required(JsonElement? data, string request, string field)
        => data is { ValueKind: not (JsonValueKind.Undefined or JsonValueKind.Null) } value ? value : throw Invalid(request, $"the reply has no {field}.");

    private static InvalidDataException Invalid(string request, string why, Exception? inner = null) => new($"The server's reply to {request} is not its reply shape: {why}", inner);

    private static string Seconds(TimeSpan span) => ((long)span.TotalSeconds).ToString(CultureInfo.InvariantCulture);

    /// <summary>
    /// Releases what this client owns: the connection it built for a node (its certificate trust) and its telemetry; a client
    /// over a given exchange, or given the container's telemetry, owns neither.
    /// </summary>
    public void Dispose()
    {
        _disposed = true;
        _link?.Dispose();
        if (_ownsTelemetry) _telemetry.Dispose();
    }

    // A link whose certificate files fail to load leaves nothing behind: the telemetry the client was about to own goes too.
    private static ZabbixNodeLink OwnedLink(ZabbixServerNode node, ZabbixConnectionOptions? connection, TimeProvider? time, ILogger? logger, ZabbixWireTelemetry telemetry, bool ownsTelemetry)
    {
        try { return new ZabbixNodeLink([node], connection, time, logger, telemetry); }
        catch
        {
            if (ownsTelemetry) telemetry.Dispose();
            throw;
        }
    }
}
