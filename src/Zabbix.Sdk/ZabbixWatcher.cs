// This Source Code Form is subject to the terms of the Mozilla Public
// License, v. 2.0. Copyright (c) 2026 schroeder247. SPDX-License-Identifier: MPL-2.0
using System.Collections.Frozen;
using System.Diagnostics;
using System.Threading.Channels;
using Community.Zabbix.Diagnostics;
using Microsoft.Extensions.Logging;

namespace Community.Zabbix;

/// <summary>
/// Observes a node. From construction, a <see cref="PeriodicTimer"/> on the client's clock ticks at the plan's interval,
/// and each tick reads the <c>history.get</c> of the node's items since the newest clock it has seen (less the allowed
/// lateness), so every value an item collects is yielded, whatever the item's own <c>delay</c>; a sampler of
/// <c>lastvalue</c> would skip them. The item set is read at start (its latest values seed <see cref="Current"/>) and
/// again every <see cref="ZabbixWatchPlan.RefreshInterval"/>, never per tick, because <c>item.get</c> has no cursor and
/// cannot be paged. Values reach each reader in clock order on its own bounded channel, so a slow reader loses counted
/// values and never delays the poll. A failed tick is counted, logged (5003) and skipped. The watcher stops when it or
/// its client is disposed.
/// </summary>
/// <remarks>
/// A <c>ChannelReader</c> distributes rather than broadcasts, so the fan-out is one bounded channel per reader; the
/// channel owns order, the bound and the drop count.
/// </remarks>
internal sealed class ZabbixWatcher : IZabbixWatcher
{
    private readonly ZabbixNode _node;
    private readonly ZabbixTreeTelemetry _telemetry;
    private readonly TimeProvider _time;
    private readonly ILogger _logger;
    private readonly string _scope;
    private readonly CancellationToken _serverStopping;
    private readonly CancellationTokenSource _stopping;
    private readonly ZabbixFanOut<ZabbixValue> _readers = new();
    private readonly ZabbixItemKeyReport _keys;
    private readonly long _lateness;
    private readonly ZabbixClock _started;

    // The poll loop is the only writer of what follows; readers see Current through one volatile snapshot.
    private readonly Dictionary<ulong, WatchedItem> _items = [];
    private Dictionary<(string HostName, ZabbixItemKey Key), WatchedItem>? _byKey;
    private Dictionary<ulong, (string HostName, ZabbixItemKey Key, ZabbixItemValueType Type)>? _ids;
    private readonly Dictionary<(string HostName, ZabbixItemKey Key), ZabbixValue> _latest = [];
    private FrozenDictionary<(string HostName, ZabbixItemKey Key), ZabbixValue> _current = FrozenDictionary<(string HostName, ZabbixItemKey Key), ZabbixValue>.Empty;
    private long _highWater;
    private long _refreshedAt;
    private bool _seeded;
    private bool _dirty;
    private volatile bool _stopped;

    private readonly Task _polling;
    private long _succeeded;
    private long _failed;
    private long _dropped;
    private int _disposed;

    internal ZabbixWatcher(ZabbixNode node, ZabbixWatchPlan plan, ILogger logger)
    {
        if (plan.WhenFull == BoundedChannelFullMode.Wait)
            throw new ArgumentException("A reader may not pace the poll: the plan's full mode must drop, not wait.", nameof(plan));
        if (!Enum.IsDefined(plan.WhenFull))
            throw new ArgumentOutOfRangeException(nameof(plan), plan.WhenFull, "The plan's full mode is not a BoundedChannelFullMode.");
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(plan.MaxItems, nameof(plan));
        ArgumentOutOfRangeException.ThrowIfLessThanOrEqual(plan.Interval, TimeSpan.Zero);
        ArgumentOutOfRangeException.ThrowIfLessThanOrEqual(plan.RefreshInterval, TimeSpan.Zero);
        if (plan.AllowedLateness is { } late) ArgumentOutOfRangeException.ThrowIfLessThan(late, TimeSpan.Zero);
        ZabbixClient client = node.Client;
        _serverStopping = client.Stopping;
        ObjectDisposedException.ThrowIf(_serverStopping.IsCancellationRequested, client);
        _node = node;
        Plan = plan;
        _telemetry = client.TreeTelemetry;
        _time = client.Time;
        _logger = logger;
        _scope = node.Selection.Scope;
        _keys = new ZabbixItemKeyReport(logger);
        _lateness = (long)Math.Ceiling((plan.AllowedLateness ?? plan.Interval).TotalSeconds);
        _stopping = CancellationTokenSource.CreateLinkedTokenSource(_serverStopping);
        // The second the watch began, whole: a collector's clock and this one agree to the second at best, and a value
        // stamped earlier in that second (the source's own clock) was still collected after Watch was called.
        _started = new ZabbixClock(_time.GetUtcNow().ToUnixTimeSeconds(), 0);
        _polling = PollAsync();
    }

    /// <inheritdoc/>
    public ZabbixWatchPlan Plan { get; }

    /// <inheritdoc/>
    public IReadOnlyDictionary<(string HostName, ZabbixItemKey Key), ZabbixValue> Current => Volatile.Read(ref _current);

    /// <inheritdoc/>
    public long SucceededPolls => Interlocked.Read(ref _succeeded);

    /// <inheritdoc/>
    public long FailedPolls => Interlocked.Read(ref _failed);

    /// <inheritdoc/>
    public long DroppedCount => Interlocked.Read(ref _dropped);

    /// <summary>How many readers are attached.</summary>
    public int Readers => _readers.Count;

    private async Task PollAsync()
    {
        Activity.Current = null;                                                          // a tick is a root span, not a child of whoever called Watch
        CancellationToken stopping = _stopping.Token;
        TreeLog.WatcherStarted(_logger, _scope, Plan.Interval.TotalSeconds);
        try
        {
            using var timer = new PeriodicTimer(Plan.Interval, _time);
            do
            {
                await TickAsync(stopping).ConfigureAwait(false);
            }
            while (await timer.WaitForNextTickAsync(stopping).ConfigureAwait(false));
        }
        catch (OperationCanceledException) when (stopping.IsCancellationRequested)
        {
            // stopped: disposed, or the server was
        }
        finally
        {
            _stopped = true;
            _readers.Complete();
            TreeLog.WatcherStopped(_logger, _scope, _serverStopping.IsCancellationRequested ? "its client was disposed" : "it was disposed");
        }
    }

    private async Task TickAsync(CancellationToken stopping)
    {
        ZabbixOperation operation = _telemetry.StartWatcherTick(_scope);
        try
        {
            bool seeding = !_seeded;
            bool collectedSinceWatch = false;
            if (seeding || _time.GetElapsedTime(_refreshedAt) >= Plan.RefreshInterval)
            {
                long started = _time.GetTimestamp();
                collectedSinceWatch = await RefreshAsync(stopping).ConfigureAwait(false);
                _refreshedAt = started;
                _seeded = true;
            }
            if (!seeding || collectedSinceWatch) await ReadAsync(stopping).ConfigureAwait(false);
            Publish();
            Interlocked.Increment(ref _succeeded);
            _telemetry.Polled(_scope);
            _telemetry.Finish(operation);
        }
        catch (Exception failure) when (!stopping.IsCancellationRequested)
        {
            Publish();                                                                    // what was yielded before the failure is current
            Interlocked.Increment(ref _failed);
            _telemetry.PollFailed(_scope, failure);
            TreeLog.WatcherTickFailed(_logger, _scope, ZabbixErrorType.Of(failure), failure);
            _telemetry.Finish(operation, failure);
        }
        catch (OperationCanceledException cancelled)
        {
            _telemetry.Finish(operation, cancelled);
            throw;
        }
    }

    /// <summary>
    /// Reads the item set: new items join (their latest value is their state now), deleted ones leave. At the seed, an
    /// item whose latest value was collected since the watch began is read from history instead (every value
    /// collected between <c>Watch</c> and this first <c>item.get</c> is yielded, not only the latest); the answer says
    /// whether one was, so the seeding tick reads that history at once. "Since the watch began" is from the start of the
    /// second it began, on this client's clock against the collector's; a value stamped by a clock behind by more than that
    /// still seeds as the latest value.
    /// </summary>
    private async Task<bool> RefreshAsync(CancellationToken stopping)
    {
        bool seeding = !_seeded;
        bool collectedSinceWatch = false;
        long high = _highWater;
        var present = new HashSet<ulong>();
        var joined = new List<ZabbixValue>();
        await foreach (ZabbixItem item in _node.GetItemsAsync(stopping).ConfigureAwait(false))
        {
            if (item is not { ItemId: { } id, ValueType: { } type, HostName: { } host } || !_keys.TryRead(item, out ZabbixItemKey key)) continue;
            present.Add(id);
            if (_items.TryGetValue(id, out WatchedItem? known) && known.Key == key && known.Host == host) continue;
            var watched = new WatchedItem(host, key, type);
            if (known is not null) _latest.Remove((known.Host, known.Key));
            _items[id] = watched;
            (_byKey, _ids) = (null, null);
            if (seeding && item.LastClockExact is { Seconds: > 0 } since && since >= _started)
            {
                watched.LastSeen = JustBefore(_started);                                   // ReadAsync yields from the watch's start on
                collectedSinceWatch = true;
            }
            else if (item is { LastClockExact: { Seconds: > 0 } clock, LastValue: { } value })
            {
                watched.LastSeen = clock;
                high = Math.Max(high, clock.Seconds);
                joined.Add(new ZabbixValue(watched.Host, key, value, clock));
            }
        }
        foreach (ulong gone in _items.Keys.Where(id => !present.Contains(id)).ToList())
        {
            WatchedItem item = _items[gone];
            _items.Remove(gone);
            (_byKey, _ids) = (null, null);
            _dirty |= _latest.Remove((item.Host, item.Key));
        }
        foreach (ZabbixValue value in joined.OrderBy(value => value.Clock)) Emit(value);
        _highWater = high > 0 ? high : Math.Max(_highWater, _time.GetUtcNow().ToUnixTimeSeconds());
        if (collectedSinceWatch) _highWater = Math.Min(_highWater, _started.Seconds + _lateness);   // the read window reaches back to the start
        return collectedSinceWatch;
    }

    private static ZabbixClock JustBefore(ZabbixClock clock)
        => clock.Nanoseconds > 0 ? new ZabbixClock(clock.Seconds, clock.Nanoseconds - 1) : new ZabbixClock(clock.Seconds - 1, 999_999_999);

    /// <summary>Reads every value the known items collected since the newest clock seen, less the allowed lateness; each item's own last clock de-duplicates.</summary>
    private async Task ReadAsync(CancellationToken stopping)
    {
        if (_items.Count == 0) return;
        long high = _highWater;
        // The two views of the item set, rebuilt only when a refresh changed it, not on every tick.
        Dictionary<(string HostName, ZabbixItemKey Key), WatchedItem> byKey = _byKey ??= _items.Values.ToDictionary(item => (item.Host, item.Key));
        Dictionary<ulong, (string HostName, ZabbixItemKey Key, ZabbixItemValueType Type)> ids = _ids ??= _items.ToDictionary(item => item.Key, item => (item.Value.Host, item.Value.Key, item.Value.Type));
        await foreach (ZabbixValue value in _node.ReadHistoryAsync(ids, _highWater - _lateness, to: null, stopping).ConfigureAwait(false))
        {
            WatchedItem item = byKey[(value.HostName, value.Key)];
            if (value.Clock <= item.LastSeen) continue;                                  // read on an earlier tick
            item.LastSeen = value.Clock;
            high = Math.Max(high, value.Clock.Seconds);
            Emit(value);
        }
        _highWater = high;                                                                // committed only when the whole read succeeded
    }

    private void Emit(ZabbixValue value)
    {
        _latest[(value.HostName, value.Key)] = value;
        _dirty = true;
        _readers.Write(value);                                                            // a dropped value is counted by OnDropped
    }

    /// <summary>Replaces the <see cref="Current"/> snapshot, once per tick that changed it (never per read).</summary>
    private void Publish()
    {
        if (!_dirty) return;
        Volatile.Write(ref _current, _latest.ToFrozenDictionary());
        _dirty = false;
    }

    private void OnDropped(ZabbixValue value)
    {
        long dropped = Interlocked.Increment(ref _dropped);
        _telemetry.Dropped(_scope);
        if (ZabbixLogSampling.IsPowerOfTen(dropped)) TreeLog.WatcherDropping(_logger, _scope, dropped);
    }

    /// <inheritdoc/>
    /// <remarks>
    /// The reader attaches here, eagerly — not when the first value is asked for — so a reader that attaches and then
    /// waits sees what arrives from that moment; and it detaches on dispose even if nothing was ever read, which an
    /// async iterator's <c>finally</c> would not do (the body of an iterator disposed before its first move never runs).
    /// </remarks>
    public IAsyncEnumerator<ZabbixValue> GetAsyncEnumerator(CancellationToken cancellationToken = default)
    {
        Channel<ZabbixValue> channel = Channel.CreateBounded<ZabbixValue>(
            new BoundedChannelOptions(Plan.MaxItems) { FullMode = Plan.WhenFull, SingleReader = true },
            OnDropped);
        _readers.Attach(channel);
        if (_stopped) _ = channel.Writer.TryComplete();                                   // a stopped watcher has nothing more to say
        return new Reader(this, channel, cancellationToken);
    }

    private sealed class WatchedItem(string host, ZabbixItemKey key, ZabbixItemValueType type)
    {
        public string Host { get; } = host;

        public ZabbixItemKey Key { get; } = key;

        public ZabbixItemValueType Type { get; } = type;

        public ZabbixClock LastSeen { get; set; }
    }

    private sealed class Reader(ZabbixWatcher watcher, Channel<ZabbixValue> channel, CancellationToken cancellationToken) : IAsyncEnumerator<ZabbixValue>
    {
        public ZabbixValue Current { get; private set; } = null!;

        public async ValueTask<bool> MoveNextAsync()
        {
            while (await channel.Reader.WaitToReadAsync(cancellationToken).ConfigureAwait(false))
            {
                if (channel.Reader.TryRead(out ZabbixValue? value))
                {
                    Current = value;
                    return true;
                }
            }
            return false;
        }

        public ValueTask DisposeAsync()
        {
            watcher._readers.Detach(channel);
            _ = channel.Writer.TryComplete();                                             // false: the watcher stopped first
            return ValueTask.CompletedTask;
        }
    }

    /// <inheritdoc/>
    public async ValueTask DisposeAsync()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0) return;
        await _stopping.CancelAsync().ConfigureAwait(false);
        await _polling.ConfigureAwait(false);
        _stopping.Dispose();
    }
}
