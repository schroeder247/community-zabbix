// This Source Code Form is subject to the terms of the Mozilla Public
// License, v. 2.0. Copyright (c) 2026 schroeder247. SPDX-License-Identifier: MPL-2.0
using System.Collections.Frozen;
using System.Diagnostics.Metrics;
using System.Security.Cryptography;
using System.Text.Json;
using Community.Zabbix.Diagnostics;
using Community.Zabbix.Protocol;
using Community.Zabbix.Storage;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace Community.Zabbix.Hosting;

/// <summary>
/// Runs a Zabbix active agent: asks the server for its <c>active checks</c>, collects each item on its own delay, and
/// uploads the values as <c>agent data</c> through the store and the send loop.
/// </summary>
/// <remarks>
/// <para>
/// A refresh loop asks for the item list; when the revision moves, a new check loop replaces the old one. The check loop
/// keeps the items on a due queue keyed by the instant on the injected <see cref="TimeProvider"/> and schedules each by
/// its <see cref="ZabbixItemDelay"/>, as upstream's <c>active_checks.c</c> does. A reply other than <c>success</c> keeps
/// the batch and retries it, and <c>"upload":"disabled"</c> holds history until a later reply says otherwise. A delay
/// that does not parse makes the item not supported, and it is not scheduled. The due queue is the loop's local state,
/// not a scheduler type: a refresh replaces the loop rather than editing the queue, so there is no coordination to own.
/// </para>
/// </remarks>
internal sealed class ZabbixActiveAgentDestination : IHostedLifecycleService, IAsyncDisposable, IDisposable
{
    private const string Disabled = "disabled";

    private readonly string _name;
    private readonly ZabbixActiveAgentOptions _options;
    private readonly ZabbixItemDictionary _handlers;
    private readonly ILoggerFactory _loggers;
    private readonly ILogger _logger;
    private readonly TimeProvider _time;
    private readonly IMeterFactory? _meters;
    private readonly ZabbixAgentTelemetry _telemetry;
    private readonly bool _ownsTelemetry;
    private readonly ZabbixWireTelemetry? _wire;
    private readonly CancellationTokenSource _stoppingLoops = new();
    private readonly CancellationTokenSource _stoppingDrain = new();
    private readonly CancellationTokenSource _abandoningDrain = new();     // closes the drain window at once: disposal, or a host that gave up
    private ZabbixNodeLink? _link;
    private ZabbixExchangeCallback? _exchange;
    private ZabbixNotificationCallback? _notify;
    private ZabbixStore<ZabbixAgentDataItem>? _store;
    private FrozenDictionary<ulong, ZabbixItemAssignment> _assignments = FrozenDictionary<ulong, ZabbixItemAssignment>.Empty;
    private Dictionary<ulong, Due> _due = [];
    private long _configRevision;
    private long _nextId;
    private int _uploadPaused;
    // The check loop's own token source, linked to _stoppingLoops. Whoever takes it out from under the gate owns it (the
    // refresh replacing it, a disposal ending it) and is the only one to cancel or dispose it.
    private readonly Lock _generationGate = new();
    private CancellationTokenSource? _generation;
    private Task _checking = Task.CompletedTask;
    private Task _refreshing = Task.CompletedTask;
    private Task _heartbeat = Task.CompletedTask;
    private Task _draining = Task.CompletedTask;
    private int _started;
    private int _disposed;

    /// <summary>An active agent named <paramref name="name"/> with its session id, its host's meter factory and its <c>ServerActive</c> destination.</summary>
    /// <param name="name">The registration's name.</param>
    /// <param name="options">The agent's options.</param>
    /// <param name="handlers">The keys it can answer.</param>
    /// <param name="loggers">Where to log.</param>
    /// <param name="time">The clock, and through its <see cref="TimeProvider.LocalTimeZone"/> the zone delays are read in.</param>
    /// <param name="session">The session id; a new random one when <see langword="null"/>.</param>
    /// <param name="meters">The host's meter factory; the agent owns its meter when <see langword="null"/>.</param>
    /// <param name="destination">Which <c>ServerActive</c> destination this agent talks to.</param>
    /// <exception cref="ArgumentOutOfRangeException"><paramref name="destination"/> is not one of the options' destinations.</exception>
    public ZabbixActiveAgentDestination(string name, ZabbixActiveAgentOptions options, ZabbixItemDictionary handlers, ILoggerFactory loggers, TimeProvider time, string? session, IMeterFactory? meters,
        int destination)
        : this(name, options, handlers, loggers, time, session, meters, destination, telemetry: null, wire: null)
    {
    }

    /// <summary>An active agent named <paramref name="name"/> for one <c>ServerActive</c> destination, with a new random session id and its own meter.</summary>
    /// <param name="name">The registration's name.</param>
    /// <param name="options">The agent's options.</param>
    /// <param name="handlers">The keys it can answer.</param>
    /// <param name="loggers">Where to log.</param>
    /// <param name="time">The clock, and through its <see cref="TimeProvider.LocalTimeZone"/> the zone delays are read in.</param>
    /// <param name="destination">Which <c>ServerActive</c> destination this agent talks to.</param>
    /// <exception cref="ArgumentOutOfRangeException"><paramref name="destination"/> is not one of the options' destinations.</exception>
    public ZabbixActiveAgentDestination(string name, ZabbixActiveAgentOptions options, ZabbixItemDictionary handlers, ILoggerFactory loggers, TimeProvider time, int destination)
        : this(name, options, handlers, loggers, time, session: null, meters: null, destination)
    {
    }

    /// <summary>An active agent named <paramref name="name"/> for the first <c>ServerActive</c> destination, with a new random session id and its own meter.</summary>
    /// <param name="name">The registration's name.</param>
    /// <param name="options">The agent's options.</param>
    /// <param name="handlers">The keys it can answer.</param>
    /// <param name="loggers">Where to log.</param>
    /// <param name="time">The clock, and through its <see cref="TimeProvider.LocalTimeZone"/> the zone delays are read in.</param>
    public ZabbixActiveAgentDestination(string name, ZabbixActiveAgentOptions options, ZabbixItemDictionary handlers, ILoggerFactory loggers, TimeProvider time)
        : this(name, options, handlers, loggers, time, session: null, meters: null, destination: 0)
    {
    }

    /// <summary>An active agent named <paramref name="name"/> that reports to the container's telemetry.</summary>
    /// <param name="name">The registration's name.</param>
    /// <param name="options">The agent's options.</param>
    /// <param name="handlers">The keys it can answer.</param>
    /// <param name="loggers">Where to log.</param>
    /// <param name="time">The clock.</param>
    /// <param name="session">The session id; a new random one when <see langword="null"/>.</param>
    /// <param name="meters">The host's meter factory; the agent owns its meter when <see langword="null"/> and no <paramref name="telemetry"/> is given.</param>
    /// <param name="destination">Which <c>ServerActive</c> destination this agent talks to.</param>
    /// <param name="telemetry">The agents' telemetry from the container; the agent owns one when <see langword="null"/>.</param>
    /// <param name="wire">The wire's telemetry from the container; the process-wide one when <see langword="null"/>.</param>
    internal ZabbixActiveAgentDestination(string name, ZabbixActiveAgentOptions options, ZabbixItemDictionary handlers, ILoggerFactory loggers, TimeProvider time, string? session, IMeterFactory? meters,
        int destination, ZabbixAgentTelemetry? telemetry, ZabbixWireTelemetry? wire)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(name);
        ArgumentNullException.ThrowIfNull(options);
        ArgumentNullException.ThrowIfNull(handlers);
        ArgumentNullException.ThrowIfNull(loggers);
        ArgumentNullException.ThrowIfNull(time);
        _name = name;
        _options = options;
        _handlers = handlers;
        _loggers = loggers;
        _logger = loggers.CreateLogger<ZabbixActiveAgentDestination>();
        _time = time;
        _meters = meters;
        _ownsTelemetry = telemetry is null;
        _telemetry = telemetry ?? new ZabbixAgentTelemetry(meters, time);
        _wire = wire;
        ZabbixServerActive serverActive = ZabbixServerActiveValidation.Parse(_options.ServerActive);
        ArgumentOutOfRangeException.ThrowIfNegative(destination);
        if (serverActive.Count > 0) ArgumentOutOfRangeException.ThrowIfGreaterThanOrEqual(destination, serverActive.Count);
        DestinationIndex = destination;
        Destination = serverActive.Count > 0 ? serverActive[destination] : [];
        StoreName = serverActive.Count > 1 ? $"{name}[{string.Join(';', Destination)}]" : name;
        Session = session ?? Convert.ToHexStringLower(RandomNumberGenerator.GetBytes(16));
        Status = new ZabbixAgentStatus(StoreName, _telemetry);
    }

    /// <summary>The destination's HA nodes, in order (one <c>,</c>-separated element of <c>ServerActive</c>).</summary>
    public IReadOnlyList<ZabbixServerNode> Destination { get; }

    /// <summary>Which destination of <c>ServerActive</c> this is.</summary>
    internal int DestinationIndex { get; }

    /// <summary>The name its store and status carry: the agent's, or with several destinations the agent's and the destination's.</summary>
    internal string StoreName { get; }

    /// <summary>The registration's name.</summary>
    public string Name => _name;

    /// <summary>The link the item list, the uploads and the heartbeat share; <see langword="null"/> before start and after disposal.</summary>
    internal ZabbixNodeLink? Link => Volatile.Read(ref _link);

    /// <summary>The session the server de-duplicates values on.</summary>
    public string Session { get; }

    /// <summary>The items the server has assigned, by item id.</summary>
    public IReadOnlyDictionary<ulong, ZabbixItemAssignment> Assignments => Volatile.Read(ref _assignments);

    /// <summary>Whether the server has paused history upload (<c>"upload":"disabled"</c>); values are held, not sent, meanwhile.</summary>
    public bool IsUploadPaused => Volatile.Read(ref _uploadPaused) != 0;

    /// <summary>What the agent is doing.</summary>
    public ZabbixAgentStatus Status { get; }

    /// <inheritdoc/>
    public async Task StartingAsync(CancellationToken cancellationToken)
    {
        _store = new ZabbixMemoryStore<ZabbixAgentDataItem>(
            new ZabbixStorePolicy<ZabbixAgentDataItem> { MaxItems = _options.MaxItems ?? ZabbixStorePolicy<ZabbixAgentDataItem>.DefaultMaxItems, Overflow = _options.Overflow },
            new ZabbixStoreServices { Name = StoreName, LoggerFactory = _loggers, Meters = _meters, Time = _time });
        Status.Attach(_store);
        ILogger wireLogger = _loggers.CreateLogger<ZabbixActiveAgentDestination>();
        // One address list for the item list, the uploads and the heartbeat, as upstream's active-checks thread has one
        // addrs vector: a redirect one of them follows is where the others go too.
        // The host owns the link (a certificate's key among it) and disposes it with itself.
        var link = new ZabbixNodeLink(Destination, _options.Connection, _time, wireLogger, _wire);
        Interlocked.Exchange(ref _link, link)?.Dispose();
        _exchange = link.Exchange(followRedirects: true);
        _notify = link.NotifyAsync;                                                      // answered only to redirect it
        await ZabbixAgentLoop.PassAsync(_name, "refresh", Status, _logger, RefreshAsync, cancellationToken).ConfigureAwait(false);   // the first item list before the loops start
    }

    /// <inheritdoc/>
    /// <remarks>A second call does nothing: one agent runs one set of loops, and the first start's are the ones stopped.</remarks>
    public async Task StartAsync(CancellationToken cancellationToken)
    {
        if (Interlocked.Exchange(ref _started, 1) != 0) return;
        if (_store is null) await StartingAsync(cancellationToken).ConfigureAwait(false);
        Status.Started(_time.GetUtcNow());
        ZabbixStore<ZabbixAgentDataItem> store = _store ?? throw new System.Diagnostics.UnreachableException("StartingAsync builds the store.");
        _draining = ZabbixAgentLoop.PassAsync(_name, "send", Status, _logger,
            async stopping => await ZabbixSendLoop.RunAsync(store, DeliverAsync, new ZabbixSendLoopOptions { Send = _options.Send, Time = _time, Logger = _logger, Progress = Status }, stopping, _abandoningDrain.Token).ConfigureAwait(false),
            _stoppingDrain.Token);
        _refreshing = RefreshLoopAsync(_stoppingLoops.Token);
        _heartbeat = _options.HeartbeatInterval > TimeSpan.Zero ? HeartbeatLoopAsync(_stoppingLoops.Token) : Task.CompletedTask;
    }

    /// <inheritdoc/>
    public Task StartedAsync(CancellationToken cancellationToken) => Task.CompletedTask;

    /// <inheritdoc/>
    public async Task StoppingAsync(CancellationToken cancellationToken)
    {
        await _stoppingLoops.CancelAsync().ConfigureAwait(false);                          // the checks (linked), the refresh and the heartbeat end
    }

    /// <inheritdoc/>
    public async Task StopAsync(CancellationToken cancellationToken)
    {
        if (!_stoppingLoops.IsCancellationRequested) await StoppingAsync(cancellationToken).ConfigureAwait(false);
        foreach (Task loop in new[] { _checking, _refreshing, _heartbeat })
        {
            try { await loop.WaitAsync(cancellationToken).ConfigureAwait(false); }
            catch (OperationCanceledException) when (_stoppingLoops.IsCancellationRequested || cancellationToken.IsCancellationRequested) { /* stopped */ }
        }
        // Nothing more is collected: stopping the send loop starts its drain window, in which it delivers what the store holds.
        _store?.Complete();
        await _stoppingDrain.CancelAsync().ConfigureAwait(false);
        try { await _draining.WaitAsync(cancellationToken).ConfigureAwait(false); }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            await _abandoningDrain.CancelAsync().ConfigureAwait(false);                  // the host gave up waiting: so does the drain
        }
    }

    /// <inheritdoc/>
    /// <remarks>The drain ends before the store it reads is disposed; a drain still running is told to stop at once.</remarks>
    public async Task StoppedAsync(CancellationToken cancellationToken)
    {
        if (!_draining.IsCompleted)
        {
            await _stoppingDrain.CancelAsync().ConfigureAwait(false);
            await _abandoningDrain.CancelAsync().ConfigureAwait(false);
        }
        await _draining.ConfigureAwait(ConfigureAwaitOptions.SuppressThrowing);            // faults were logged where they happened
        if (_store is not null) await _store.DisposeAsync().ConfigureAwait(false);
        _store = null;
    }

    /// <inheritdoc/>
    public async ValueTask DisposeAsync()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0) return;
        await _stoppingLoops.CancelAsync().ConfigureAwait(false);
        await _stoppingDrain.CancelAsync().ConfigureAwait(false);
        await _abandoningDrain.CancelAsync().ConfigureAwait(false);
        // The checks and the drain end before the store and the link they use are disposed. Disposal is not a stop: the
        // drain window is closed at once, and a batch in flight goes back to the store. Faults were logged where they happened.
        foreach (Task loop in new[] { _checking, _refreshing, _heartbeat, _draining }) await loop.ConfigureAwait(ConfigureAwaitOptions.SuppressThrowing);
        TakeGeneration()?.Dispose();
        if (_store is not null) await _store.DisposeAsync().ConfigureAwait(false);
        Interlocked.Exchange(ref _link, null)?.Dispose();
        _stoppingLoops.Dispose();
        _stoppingDrain.Dispose();
        _abandoningDrain.Dispose();
        if (_ownsTelemetry) _telemetry.Dispose();
    }

    /// <summary>The synchronous form, for a container disposed synchronously.</summary>
    public void Dispose()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0) return;
        _stoppingLoops.Cancel();
        _stoppingDrain.Cancel();
        _abandoningDrain.Cancel();
        TakeGeneration()?.Dispose();
        Interlocked.Exchange(ref _link, null)?.Dispose();
        _stoppingLoops.Dispose();
        _stoppingDrain.Dispose();
        _abandoningDrain.Dispose();
        if (_ownsTelemetry) _telemetry.Dispose();
    }

    // ----- the item list ---------------------------------------------------------------------------------------

    private async Task RefreshLoopAsync(CancellationToken stopping)
    {
        using var timer = new PeriodicTimer(_options.RefreshInterval, _time);
        while (await timer.WaitForNextTickAsync(stopping).ConfigureAwait(false))
            await ZabbixAgentLoop.PassAsync(_name, "refresh", Status, _logger, RefreshAsync, stopping).ConfigureAwait(false);
    }

    private async Task RefreshAsync(CancellationToken cancellationToken)
    {
        var request = new ZabbixActiveChecksRequest("active checks", _options.Hostname, _options.HostMetadata, ZabbixWire.AgentVersion, ZabbixWire.AgentVariant, Volatile.Read(ref _configRevision), Session);
        ZabbixOperation exchange = _telemetry.StartExchange(_name, request.Request);
        ZabbixActiveChecksResponse? response;
        try
        {
            ReadOnlyMemory<byte> reply = await _exchange!(JsonSerializer.SerializeToUtf8Bytes(request, ZabbixActiveAgentJson.Default.ZabbixActiveChecksRequest), cancellationToken).ConfigureAwait(false);
            response = JsonSerializer.Deserialize(reply.Span, ZabbixActiveAgentJson.Default.ZabbixActiveChecksResponse);
        }
        catch (Exception failure)
        {
            _telemetry.Finish(exchange, failure);
            if (!ZabbixAgentLoop.IsExchangeFailure(failure, cancellationToken)) throw;
            AgentLog.ActiveChecksFailed(_logger, _name, failure);
            return;
        }
        if (response is null || !string.Equals(response.Response, "success", StringComparison.Ordinal))
        {
            _telemetry.Finish(exchange, errorType: "refused");
            AgentLog.ActiveChecksRefused(_logger, _name, response?.Info ?? "no reply");
            return;
        }
        _telemetry.Finish(exchange);
        Upload(response.Upload);
        if (response.Data is null) return;                                                // the revision matched: nothing changed
        FrozenDictionary<ulong, ZabbixItemAssignment> assignments = Assign(response.Data);
        Volatile.Write(ref _configRevision, response.ConfigRevision ?? 0);
        Volatile.Write(ref _assignments, assignments);
        AgentLog.ActiveChecksReceived(_logger, _name, assignments.Count, response.ConfigRevision ?? 0);
        await RestartChecksAsync(assignments).ConfigureAwait(false);
    }

    /// <summary>
    /// The item list by id, the last row of a repeated id winning. Upstream's <c>add_check</c> merges a repeated item into
    /// the one it holds and never refuses the list, so neither does this; the repeat is warned about once per list.
    /// </summary>
    private FrozenDictionary<ulong, ZabbixItemAssignment> Assign(IEnumerable<ZabbixItemAssignment> data)
    {
        var byId = new Dictionary<ulong, ZabbixItemAssignment>();
        int rows = 0;
        ulong repeated = 0UL;
        foreach (ZabbixItemAssignment assignment in data)
        {
            rows++;
            if (byId.ContainsKey(assignment.ItemId) && repeated == 0) repeated = assignment.ItemId;
            byId[assignment.ItemId] = assignment;
        }
        if (repeated != 0) AgentLog.ActiveChecksRepeatItem(_logger, _name, repeated, rows - byId.Count);
        return byId.ToFrozenDictionary();
    }

    /// <summary>The server's word on history upload, from either reply.</summary>
    private void Upload(string? upload)
    {
        int paused = string.Equals(upload, Disabled, StringComparison.Ordinal) ? 1 : 0;
        if (Interlocked.Exchange(ref _uploadPaused, paused) != paused)
            AgentLog.UploadStateChanged(_logger, _name, paused == 1 ? "paused" : "resumed");
    }

    // ----- the checks ------------------------------------------------------------------------------------------

    /// <summary>A check's next due instant, kept across a refresh for as long as its key and delay do not change.</summary>
    private readonly record struct Due(string Key, ZabbixInterval Delay, DateTimeOffset At);

    /// <summary>An assigned item ready to collect.</summary>
    private sealed record Check(ulong ItemId, ZabbixItemKey Key, ZabbixItemDelay Delay, TimeSpan Timeout, ZabbixItemAssignment Assignment);

    private async Task RestartChecksAsync(FrozenDictionary<ulong, ZabbixItemAssignment> assignments)
    {
        if (TakeGeneration() is { } previous)
        {
            await previous.CancelAsync().ConfigureAwait(false);
            try { await _checking.ConfigureAwait(false); } catch (OperationCanceledException) when (previous.IsCancellationRequested) { /* replaced */ }
            previous.Dispose();
        }
        CancellationToken token;
        lock (_generationGate)
        {
            if (Volatile.Read(ref _disposed) != 0) return;                                 // disposed while the old loop ended
            _generation = CancellationTokenSource.CreateLinkedTokenSource(_stoppingLoops.Token);
            token = _generation.Token;
        }
        _checking = CheckLoopAsync(assignments, _due, token);
    }

    private CancellationTokenSource? TakeGeneration()
    {
        lock (_generationGate)
        {
            CancellationTokenSource? taken = _generation;
            _generation = null;
            return taken;
        }
    }

    private async Task CheckLoopAsync(FrozenDictionary<ulong, ZabbixItemAssignment> assignments, Dictionary<ulong, Due> previous, CancellationToken stopping)
    {
        TimeZoneInfo zone = _time.LocalTimeZone;
        DateTimeOffset now = _time.GetUtcNow();
        var queue = new PriorityQueue<Check, DateTimeOffset>(assignments.Count);
        var due = new Dictionary<ulong, Due>(assignments.Count);
        foreach (ZabbixItemAssignment assignment in assignments.Values)
        {
            if (!ZabbixItemKey.TryParse(assignment.Key, null, out ZabbixItemKey key))
            {
                Admit(assignment.ItemId, "Invalid item key format.", supported: false, now);
                continue;
            }
            if (!ZabbixItemDelay.TryParse(assignment.Delay.ToString(), out ZabbixItemDelay? delay, out string? error))
            {
                AgentLog.ItemDelayInvalid(_logger, _name, assignment.ItemId, assignment.Key, error);
                Admit(assignment.ItemId, error, supported: false, now);
                continue;
            }
            TimeSpan timeout = assignment.Timeout?.Length is { } length && length > TimeSpan.Zero ? length : _options.CheckTimeout;
            var check = new Check(assignment.ItemId, key, delay, timeout, assignment);
            // Upstream (add_check): an unchanged item keeps its schedule; a new one is collected at once, unless its
            // delay is a scheduling one, which waits for its first instant.
            DateTimeOffset? first = previous.TryGetValue(assignment.ItemId, out Due kept) && kept.Key == assignment.Key && kept.Delay == assignment.Delay
                ? kept.At
                : delay.IsScheduling ? delay.GetNextCheck(now, zone, unchecked((long)assignment.ItemId)) : now;
            if (first is not { } at) continue;
            queue.Enqueue(check, at);
            due[assignment.ItemId] = new Due(assignment.Key, assignment.Delay, at);
        }
        _due = due;

        DateTimeOffset last = now;                                                       // the last wall reading, and its monotonic twin
        long lastAt = _time.GetTimestamp();
        while (queue.TryPeek(out _, out DateTimeOffset next))
        {
            TimeSpan wait = next - _time.GetUtcNow();
            if (wait > TimeSpan.Zero) await Task.Delay(wait, _time, stopping).ConfigureAwait(false);
            now = _time.GetUtcNow();                                                     // one clock read per round
            long nowAt = _time.GetTimestamp();
            if (Step(last, lastAt, now, nowAt) is { } back) Shift(queue, due, back);
            var round = new List<Check>();
            while (queue.TryPeek(out Check? check, out DateTimeOffset at) && at <= now) round.Add(queue.Dequeue());
            DateTimeOffset tick = now;
            await ZabbixAgentLoop.PassAsync(_name, "checks", Status, _logger,
                token => Parallel.ForEachAsync(round, new ParallelOptions { MaxDegreeOfParallelism = _options.MaxConcurrentChecks, CancellationToken = token },
                    (check, cancellation) => CollectAsync(check, tick, cancellation)), stopping).ConfigureAwait(false);
            DateTimeOffset after = _time.GetUtcNow();
            long afterAt = _time.GetTimestamp();
            if (Step(now, nowAt, after, afterAt) is { } during)                          // stepped back during the collection
            {
                Shift(queue, due, during);
                now += during;
            }
            last = after;
            lastAt = afterAt;
            foreach (Check check in round)
            {
                long seed = unchecked((long)check.ItemId);
                DateTimeOffset? again = check.Delay.GetNextCheck(now, zone, seed);
                if (again <= after) again = check.Delay.GetNextCheck(after, zone, seed);      // the collection overran its next check
                if (again is { } at)
                {
                    queue.Enqueue(check, at);
                    due[check.ItemId] = new Due(check.Assignment.Key, check.Assignment.Delay, at);
                }
                else
                {
                    due.Remove(check.ItemId);                                           // never due again (upstream's ZBX_JAN_2038)
                }
            }
        }
    }

    /// <summary>
    /// How far the wall clock was stepped back between two paired readings (wall and monotonic): the wall's move less the
    /// monotonic time that passed, when that is more than a second back (upstream's own resolution: its loop looks every
    /// second); <see langword="null"/> otherwise, so reading jitter and a slewed clock are not steps.
    /// </summary>
    private TimeSpan? Step(DateTimeOffset from, long fromAt, DateTimeOffset to, long toAt)
        => (to - from) - _time.GetElapsedTime(fromAt, toAt) is var step && step < -TimeSpan.FromSeconds(1) ? step : null;

    /// <summary>
    /// The wall clock was stepped back by <paramref name="delta"/> (negative, from <see cref="Step"/>): every due instant
    /// moves with it, so collection goes on at its cadence instead of pausing for the size of the step. Upstream
    /// active_checks.c does the same ("the system time has been pushed back, adjusting active check schedule",
    /// <c>update_schedule</c>). The due instants stay on the wall clock because a scheduling delay (<c>md1-5h9</c>) names
    /// wall-clock instants; a forward step needs nothing, since the overdue checks are collected once and rescheduled
    /// from the new time.
    /// </summary>
    private void Shift(PriorityQueue<Check, DateTimeOffset> queue, Dictionary<ulong, Due> due, TimeSpan delta)
    {
        AgentLog.ClockSteppedBack(_logger, _name, -delta);
        (Check Check, DateTimeOffset At)[] held = [.. queue.UnorderedItems.Select(entry => (entry.Element, entry.Priority))];
        queue.Clear();
        foreach ((Check check, DateTimeOffset at) in held)
        {
            queue.Enqueue(check, at + delta);
            if (due.TryGetValue(check.ItemId, out Due kept)) due[check.ItemId] = kept with { At = at + delta };
        }
    }

    private async ValueTask CollectAsync(Check check, DateTimeOffset now, CancellationToken cancellationToken)
    {
        ZabbixAgentAnswer answer = await _handlers.AnswerLoggedAsync(check.Key, check.Timeout, _time, _logger, cancellationToken).ConfigureAwait(false);
        if (!answer.IsSupported)
        {
            AgentLog.CheckNotSupported(_logger, _name, check.Key, answer.Error ?? "not supported");
            Admit(check.ItemId, answer.Error ?? "not supported", supported: false, now);
        }
        else if (answer.Value is { } value)
        {
            Admit(check.ItemId, value, supported: true, now);
        }
        // A supported key with no value this time sends nothing: the item keeps its last value and its state.
    }

    /// <summary>
    /// A value enters the buffer: its id is assigned here, once, and its state travels with it. A refusal by the store's
    /// policy is the store's to count; a store already completed (the agent stopping) is said here.
    /// </summary>
    private void Admit(ulong itemId, string value, bool supported, ZabbixClock clock)
    {
        if (_store is not { } store) return;
        if (!store.TryWrite(new ZabbixAgentDataItem(Interlocked.Increment(ref _nextId), itemId, value, clock.Seconds, clock.Nanoseconds, supported ? null : 1)) && store.IsCompleted)
            AgentLog.CollectedAfterClose(_logger, _name, itemId);
    }

    // ----- the uploads -----------------------------------------------------------------------------------------

    private async ValueTask<ZabbixDelivery> DeliverAsync(IReadOnlyList<ZabbixAgentDataItem> batch, CancellationToken cancellationToken)
    {
        if (IsUploadPaused) return ZabbixDelivery.Held;                                  // the server's word: hold, do not send
        var request = new ZabbixAgentDataRequest("agent data", batch, Session, _options.Hostname, ZabbixWire.AgentVersion, ZabbixWire.AgentVariant);
        ZabbixOperation exchange = _telemetry.StartExchange(_name, request.Request);
        ZabbixAgentResponse? response;
        try
        {
            ReadOnlyMemory<byte> reply = await _exchange!(JsonSerializer.SerializeToUtf8Bytes(request, ZabbixActiveAgentJson.Default.ZabbixAgentDataRequest), cancellationToken).ConfigureAwait(false);
            response = JsonSerializer.Deserialize(reply.Span, ZabbixActiveAgentJson.Default.ZabbixAgentResponse);
        }
        catch (Exception failure)
        {
            _telemetry.Finish(exchange, failure);
            if (!ZabbixAgentLoop.IsExchangeFailure(failure, cancellationToken)) throw;   // the send loop logs it as a fault and keeps the batch
            return ZabbixDelivery.Retried;
        }
        Upload(response?.Upload);
        if (response is not null && string.Equals(response.Response, "success", StringComparison.Ordinal))
        {
            _telemetry.Finish(exchange);
            return ZabbixDelivery.Delivered;
        }
        if (IsUploadPaused)
        {
            _telemetry.Finish(exchange, errorType: "upload_disabled");
            return ZabbixDelivery.Held;
        }
        _telemetry.Finish(exchange, errorType: "refused");
        AgentLog.AgentDataRefused(_logger, _name, response?.Info ?? "no reply");
        return ZabbixDelivery.Retried;                                                     // upstream keeps the buffer and sends it again
    }

    private async Task HeartbeatLoopAsync(CancellationToken stopping)
    {
        using var timer = new PeriodicTimer(_options.HeartbeatInterval, _time);
        var request = new ZabbixHeartbeatRequest("active check heartbeat", _options.Hostname, (int)_options.HeartbeatInterval.TotalSeconds, ZabbixWire.AgentVersion, ZabbixWire.AgentVariant);
        byte[] frame = JsonSerializer.SerializeToUtf8Bytes(request, ZabbixActiveAgentJson.Default.ZabbixHeartbeatRequest);
        while (await timer.WaitForNextTickAsync(stopping).ConfigureAwait(false))
        {
            try { await _notify!(frame, stopping).ConfigureAwait(false); }
            catch (Exception failure) when (ZabbixFrames.IsExchangeFailure(failure))
            {
                AgentLog.HeartbeatFailed(_logger, _name, failure);
            }
        }
    }
}
