// This Source Code Form is subject to the terms of the Mozilla Public
// License, v. 2.0. Copyright (c) 2026 schroeder247. SPDX-License-Identifier: MPL-2.0
using System.Diagnostics.Metrics;
using System.Globalization;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Threading.Channels;
using Community.Zabbix.Storage;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace Community.Zabbix.Listening;

/// <summary>
/// One row of a trapper frame, as upstream reads every row of <c>sender data</c> and <c>agent data</c> alike
/// (<c>proxy.c</c> <c>parse_history_data_row_value</c>): <c>clock</c>/<c>ns</c> are optional and default to arrival,
/// <c>state</c> 1 makes <c>value</c> the not-supported reason, and <c>id</c> is an agent's value id in its session. A
/// sender names the item by <c>host</c> and <c>key</c>; an agent of 4.4 or later by <c>itemid</c>, its host at the
/// frame. <c>host</c>, <c>key</c> and <c>value</c> are read as upstream reads them: any scalar's text
/// (<see cref="ZabbixScalarTextConverter"/>).
/// </summary>
/// <param name="Host">The host, for a row named by host and key.</param>
/// <param name="Key">The item key, for a row named by host and key.</param>
/// <param name="Value">The value, or the not-supported reason; <see langword="null"/> when the row carries none.</param>
/// <param name="Clock">Unix seconds; arrival when absent.</param>
/// <param name="Ns">Nanoseconds within the second.</param>
/// <param name="ItemId">The item, for an agent's row named by id.</param>
/// <param name="Id">The agent's value id in its session, which the receiver de-duplicates on.</param>
/// <param name="State">1 when the item is not supported.</param>
internal sealed record ZabbixTrapperItem(
    [property: JsonPropertyName("host"), JsonConverter(typeof(ZabbixScalarTextConverter))] string? Host,
    [property: JsonPropertyName("key"), JsonConverter(typeof(ZabbixScalarTextConverter))] string? Key,
    [property: JsonPropertyName("value"), JsonConverter(typeof(ZabbixScalarTextConverter))] string? Value,
    [property: JsonPropertyName("clock")] long? Clock,
    [property: JsonPropertyName("ns")] int? Ns,
    [property: JsonPropertyName("itemid")] ulong? ItemId = null,
    [property: JsonPropertyName("id")] long? Id = null,
    [property: JsonPropertyName("state")] int? State = null);

/// <summary>
/// The frame a sender or an active agent sends: <c>{"request":"sender data","data":[…]}</c>, or an agent's
/// <c>{"request":"agent data","session":…,"host":…,"version":…,"data":[…]}</c>.
/// </summary>
/// <param name="Request">The request kind.</param>
/// <param name="Data">The rows.</param>
/// <param name="Clock">The sender's clock, seconds.</param>
/// <param name="Ns">The sender's clock, nanoseconds.</param>
/// <param name="Host">An agent's host (4.4 and later); its rows are by item id.</param>
/// <param name="Session">An agent's session token: 32 characters, the scope its value ids are unique in.</param>
/// <param name="Version">An agent's version; absent is read as 4.2, whose rows are by host and key.</param>
internal sealed record ZabbixTrapperRequest(
    [property: JsonPropertyName("request")] string? Request,
    [property: JsonPropertyName("data")] IReadOnlyList<ZabbixTrapperItem>? Data,
    [property: JsonPropertyName("clock")] long? Clock,
    [property: JsonPropertyName("ns")] int? Ns,
    [property: JsonPropertyName("host"), JsonConverter(typeof(ZabbixScalarTextConverter))] string? Host = null,
    [property: JsonPropertyName("session"), JsonConverter(typeof(ZabbixScalarTextConverter))] string? Session = null,
    [property: JsonPropertyName("version"), JsonConverter(typeof(ZabbixScalarTextConverter))] string? Version = null);

/// <summary>The trapper's answer, in the server's own words.</summary>
internal sealed record ZabbixTrapperResponse(
    [property: JsonPropertyName("response")] string Response,
    [property: JsonPropertyName("info")] string Info);

/// <summary>The trapper's JSON context.</summary>
[JsonSourceGenerationOptions(DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull, NumberHandling = JsonNumberHandling.AllowReadingFromString)]
[JsonSerializable(typeof(ZabbixTrapperRequest))]
[JsonSerializable(typeof(ZabbixTrapperResponse))]
internal sealed partial class ZabbixTrapperJson : JsonSerializerContext
{
}

/// <summary>
/// Receives <c>sender data</c> and <c>agent data</c> frames as a listener's handler and writes their values into a
/// writer (a store, a channel), answering as the server does: <c>processed: N; failed: M; total: T; seconds spent: S</c>.
/// </summary>
/// <remarks>
/// A value the writer does not keep (refused or shed by a store's policy, full, completed) or throws on (a store's
/// backend fault, logged once per frame) counts as failed, so the sender knows. A store says which values it kept; any
/// other writer is offered each value without waiting, and its <see cref="ChannelWriter{T}.TryWrite"/> answer is taken as
/// it is, so a channel in a drop mode (whose <c>TryWrite</c> answers <see langword="true"/> for a value it drops) must
/// not be handed to a trapper: give it a channel in <see cref="BoundedChannelFullMode.Wait"/> mode. A
/// <see cref="ChannelWriter{T}"/> does not expose its full mode, so the trapper cannot refuse one in a drop mode. The counts are the <see cref="ValuesInstrument"/> counter (by
/// <see cref="OutcomeTag"/>); <see cref="Received"/>/<see cref="Failed"/> read the same totals atomically.
/// <para>A row's <c>state</c> 1 is a not-supported value (<see cref="ZabbixValue.IsSupported"/>); a row without a value
/// is processed but writes nothing, as <see cref="ZabbixValue"/> has no value-less form. A 4.4 or later agent names its
/// items by id, which only a receiver holding the configuration resolves (<see cref="Proxy.ZabbixProxyAdmission"/>); this
/// trapper alone reads rows by host and key.</para>
/// </remarks>
public sealed class ZabbixTrapper
{
    private static readonly ReadOnlyMemory<byte> s_notATrapperRequest = Encoding.UTF8.GetBytes("""{"response":"failed","info":"not a sender data request"}""");

    /// <summary>The counter of values a trapper took or refused, tagged <see cref="OutcomeTag"/>.</summary>
    public const string ValuesInstrument = "zabbix.listener.trapper.values";

    /// <summary>The value's outcome: <c>processed</c> or <c>failed</c>.</summary>
    public const string OutcomeTag = "zabbix.listener.trapper.outcome";

    private readonly ChannelWriter<ZabbixValue> _values;
    private readonly TimeProvider _time;
    private readonly ILogger _logger;
    private readonly Meter? _meter;                                                     // the host's, from its factory: not disposed here
    private readonly Counter<long>? _counted;
    private readonly IZabbixAgentItems? _agentItems;
    private long _received;
    private long _failed;

    /// <summary>A trapper writing into <paramref name="values"/>, on the system clock, silent and uncounted.</summary>
    /// <param name="values">Where the values go: a store's writer face, a channel's writer.</param>
    /// <exception cref="ArgumentNullException"><paramref name="values"/> is <see langword="null"/>.</exception>
    public ZabbixTrapper(ChannelWriter<ZabbixValue> values)
        : this(values, null, null, null)
    {
    }

    /// <summary>A trapper writing into <paramref name="values"/>, with the application's logging, clock and meters.</summary>
    /// <param name="values">Where the values go: a store's writer face, a channel's writer.</param>
    /// <param name="loggers">Where to log; nowhere when <see langword="null"/>. The caller keeps owning it.</param>
    /// <param name="time">The clock a value without its own is stamped by; the system's when <see langword="null"/>.</param>
    /// <param name="meters">Where to count (the <see cref="Diagnostics.ZabbixTelemetryNames.Listener"/> meter); uncounted when <see langword="null"/>.</param>
    /// <exception cref="ArgumentNullException"><paramref name="values"/> is <see langword="null"/>.</exception>
    public ZabbixTrapper(ChannelWriter<ZabbixValue> values, ILoggerFactory? loggers, TimeProvider? time, IMeterFactory? meters)
    {
        ArgumentNullException.ThrowIfNull(values);
        _values = values;
        _time = time ?? TimeProvider.System;
        _logger = (ILogger?)loggers?.CreateLogger<ZabbixTrapper>() ?? NullLogger<ZabbixTrapper>.Instance;
        _meter = meters?.Create(ZabbixListenerTelemetry.Name);
        _counted = _meter?.CreateCounter<long>(ValuesInstrument, unit: "{value}", description: "Values a Zabbix trapper took (processed) or refused (failed).");
    }

    /// <summary>A trapper that also resolves <c>agent data</c> by item id, through <paramref name="agentItems"/> (a proxy's configuration and sessions).</summary>
    internal ZabbixTrapper(ChannelWriter<ZabbixValue> values, IZabbixAgentItems agentItems, ILoggerFactory? loggers, TimeProvider? time, IMeterFactory? meters)
        : this(values, loggers, time, meters)
        => _agentItems = agentItems;

    /// <summary>The handler a <see cref="ZabbixListener"/> takes.</summary>
    public ZabbixRequestCallback Handler => HandleAsync;

    /// <summary>Values received since start; an atomic read of the counter's processed total.</summary>
    public long Received => Interlocked.Read(ref _received);

    /// <summary>Values refused since start (unnamed, or not taken by the writer); an atomic read of the failed total.</summary>
    public long Failed => Interlocked.Read(ref _failed);

    private const int NotSupported = 1;                                                 // ITEM_STATE_NOTSUPPORTED
    private const int SessionTokenLength = 32;                                          // ZBX_SESSION_TOKEN_SIZE
    private static readonly ZabbixComponentVersion s_byItemId = new(4, 4);                // agent data by item id, host at the frame

    /// <summary>What <see cref="TakeAsync"/> makes of one row: <see langword="false"/> refuses it (failed); <see langword="true"/> with a value writes it, with none processes it and writes nothing.</summary>
    private delegate bool RowReader(ZabbixTrapperItem? row, out ZabbixValue? value);

    private async ValueTask<ReadOnlyMemory<byte>> HandleAsync(ZabbixRequest request, CancellationToken cancellationToken)
    {
        long started = _time.GetTimestamp();
        ZabbixTrapperRequest? frame;
        try
        {
            frame = JsonSerializer.Deserialize(request.Payload.Span, ZabbixTrapperJson.Default.ZabbixTrapperRequest);
        }
        catch (JsonException failure)
        {
            ListenerLog.TrapperFrameRejected(_logger, request.Peer?.ToString(), failure.Message);
            return s_notATrapperRequest;
        }
        if (frame is { Request: "agent data" } && _agentItems is not null && IsByItemId(frame.Version))
            return await AgentDataAsync(request, frame, _agentItems, started, cancellationToken).ConfigureAwait(false);
        if (frame?.Request is not ("sender data" or "agent data") || frame.Data is null)
        {
            ListenerLog.TrapperFrameRejected(_logger, request.Peer?.ToString(), frame?.Request ?? "no request");
            return s_notATrapperRequest;
        }

        ZabbixClock now = _time.GetUtcNow();
        (int processed, int failed) = await TakeAsync(frame.Data, request, (ZabbixTrapperItem? row, out ZabbixValue? value) =>
        {
            value = null;
            return row is not null                                                        // "data":[null]: upstream cannot open the row either
                   && row.Host is { Length: > 0 } host && row.Key is { Length: > 0 } key && ZabbixItemKey.TryParse(key, null, out ZabbixItemKey parsed)
                   && TryRead(row, host, parsed, now, out value);
        }, cancellationToken).ConfigureAwait(false);
        return Answer("success", processed, failed, started);
    }

    /// <summary>A 4.4 or later agent: <c>zbx_process_agent_history_data</c> reads <c>version</c>, and an absent or unreadable one as 4.2.</summary>
    private static bool IsByItemId(string? version)
        => ZabbixComponentVersion.TryParse(version, CultureInfo.InvariantCulture, out ZabbixComponentVersion parsed) && parsed.WithoutPatch >= s_byItemId;

    /// <summary>
    /// <c>agent data</c> as <c>proxy.c</c> <c>zbx_process_agent_history_data</c> and <c>process_history_data_by_itemids</c>
    /// take it (7.0, 7.4 and 8.0 alike): the session token must be 32 characters (else <c>failed</c>), the frame must name
    /// its host (else <c>failed</c>), an unknown host is answered <c>success</c> so the agent does not re-send what no one
    /// will take; each row names its item by id, a row whose <c>id</c> the session already took (at or below its
    /// <c>last_id</c>) is a duplicate and fails, and the session's <c>last_id</c> then becomes the last row's id. The item
    /// need not be the frame host's (upstream does not check); the writer applies the rest of the rule (an active item,
    /// enabled, of a monitored host).
    /// </summary>
    private async ValueTask<ReadOnlyMemory<byte>> AgentDataAsync(ZabbixRequest request, ZabbixTrapperRequest frame, IZabbixAgentItems items, long started, CancellationToken cancellationToken)
    {
        if (frame.Data is null) return Answer("success", 0, 0, started);                      // no "data": upstream answers success
        if (frame.Session is { } token && token.Length != SessionTokenLength) return Refuse(request, string.Create(CultureInfo.InvariantCulture, $"invalid session token length {token.Length}"));
        if (frame.Host is not { } host) return Refuse(request, "cannot find \"host\" token");
        if (!items.TryGetHost(host, out ulong hostId))
            return JsonSerializer.SerializeToUtf8Bytes(new ZabbixTrapperResponse("success", $"unknown host '{host}'"), ZabbixTrapperJson.Default.ZabbixTrapperResponse);

        ZabbixAgentSession? session = frame.Session is { } open ? items.Session(hostId, open) : null;
        long taken = session?.LastId ?? 0;                                                   // last_id moves once, after the frame
        long last = 0;
        ZabbixClock now = _time.GetUtcNow();
        (int processed, int failed) = await TakeAsync(frame.Data, request, (ZabbixTrapperItem? row, out ZabbixValue? value) =>
        {
            value = null;
            if (row?.ItemId is not { } itemId || row.Ns is < 0 or >= 1_000_000_000) return false;   // not parsed: no item id, a bad ns
            last = row.Id is > 0 and var id ? id : 0;
            if (!items.TryGetItem(itemId, out string? itemHost, out ZabbixItemKey key)) return false;
            if (session is not null && last != 0 && last <= taken) return false;             // a value this session already delivered
            return TryRead(row, itemHost, key, now, out value);
        }, cancellationToken).ConfigureAwait(false);
        if (session is not null && last != 0 && !session.TryAdvance(last, out long held)) ListenerLog.TrapperAgentIdBehind(_logger, host, last, held);
        return Answer("success", processed, failed, started);
    }

    /// <summary>The value half of a row: its clock (arrival when absent), its state, and its text; a not-supported row must carry its reason.</summary>
    private static bool TryRead(ZabbixTrapperItem row, string host, ZabbixItemKey key, ZabbixClock now, out ZabbixValue? value)
    {
        value = null;
        if (row.Ns is < 0 or >= 1_000_000_000) return false;
        bool supported = row.State != NotSupported;
        if (row.Value is not { } text) return supported;                                     // no value: processed, nothing to write; a not-supported row without a reason is failed (upstream's THIS_SHOULD_NEVER_HAPPEN)
        value = new ZabbixValue(host, key, text, row.Clock is { } unix ? new ZabbixClock(unix, row.Ns ?? 0) : now) { IsSupported = supported };
        return true;
    }

    /// <summary>
    /// Reads every row through <paramref name="read"/> and writes what it yields; the counts, the fault log and the
    /// instruments are one place for both requests. A store (or a writer in front of one) is written durably: a value
    /// counts as processed once it is committed, so the answer never says "processed" for a value a durable store lost.
    /// The first value is awaited alone (it opens the store if this is its first use); the rest are handed over in row
    /// order and awaited together, so one frame is one commit.
    /// </summary>
    private async ValueTask<(int Processed, int Failed)> TakeAsync(IReadOnlyList<ZabbixTrapperItem> rows, ZabbixRequest request, RowReader read, CancellationToken cancellationToken)
    {
        int processed = 0;
        int failed = 0;
        int faulted = 0;
        Exception? fault = null;
        var durable = _values as IZabbixDurableWriter<ZabbixValue>;
        List<Task<bool>>? writes = null;
        foreach (ZabbixTrapperItem? row in rows)
        {
            if (!read(row, out ZabbixValue? value))
            {
                failed++;
                continue;
            }
            if (value is null)
            {
                processed++;
                continue;
            }
            if (durable is not null && writes is not null)
            {
                writes.Add(Durably(durable, value, cancellationToken).AsTask());
                continue;
            }
            Count(durable is null ? Synchronously(value) : await Durably(durable, value, cancellationToken).ConfigureAwait(false));
            if (durable is not null) writes = [];
        }
        foreach (Task<bool> write in writes ?? []) Count(await write.ConfigureAwait(false));
        if (fault is not null) ListenerLog.TrapperWriteFaulted(_logger, request.Peer?.ToString(), faulted, fault);
        Interlocked.Add(ref _received, processed);
        Interlocked.Add(ref _failed, failed);
        if (processed > 0) _counted?.Add(processed, new KeyValuePair<string, object?>(OutcomeTag, "processed"));
        if (failed > 0) _counted?.Add(failed, new KeyValuePair<string, object?>(OutcomeTag, "failed"));
        if (_logger.IsEnabled(LogLevel.Debug))
        {
            string? peer = request.Peer?.ToString();
            ListenerLog.TrapperFrameReceived(_logger, peer, processed, failed);
        }
        return (processed, failed);

        void Count(bool? taken)
        {
            if (taken == true) processed++;
            else failed++;
        }

        // A backend fault mid-frame (a full disk, a refused row): the values before it are kept, so the answer must say
        // so, or the sender re-sends them. Counted failed and logged once per frame.
        bool? Synchronously(ZabbixValue value)
        {
            try
            {
                return _values.TryWrite(value);
            }
            catch (Exception failure) when (failure is not OutOfMemoryException)
            {
                fault ??= failure;
                faulted++;
                return null;
            }
        }

        async ValueTask<bool> Durably(IZabbixDurableWriter<ZabbixValue> writer, ZabbixValue value, CancellationToken cancellation)
        {
            try
            {
                return await writer.TryWriteDurablyAsync(value, cancellation).ConfigureAwait(false);
            }
            catch (Exception failure) when (!(failure is OperationCanceledException && cancellation.IsCancellationRequested))
            {
                fault ??= failure;
                faulted++;
                return false;
            }
        }
    }

    private ReadOnlyMemory<byte> Answer(string response, int processed, int failed, long started)
    {
        double seconds = _time.GetElapsedTime(started).TotalSeconds;
        var answer = new ZabbixTrapperResponse(response,
            $"processed: {processed}; failed: {failed}; total: {processed + failed}; seconds spent: {seconds.ToString("F6", CultureInfo.InvariantCulture)}");
        return JsonSerializer.SerializeToUtf8Bytes(answer, ZabbixTrapperJson.Default.ZabbixTrapperResponse);
    }

    /// <summary>A frame upstream answers <c>failed</c> and logs as invalid agent history data.</summary>
    private ReadOnlyMemory<byte> Refuse(ZabbixRequest request, string info)
    {
        ListenerLog.TrapperFrameRejected(_logger, request.Peer?.ToString(), info);
        return JsonSerializer.SerializeToUtf8Bytes(new ZabbixTrapperResponse("failed", info), ZabbixTrapperJson.Default.ZabbixTrapperResponse);
    }
}
