// This Source Code Form is subject to the terms of the Mozilla Public
// License, v. 2.0. Copyright (c) 2026 schroeder247. SPDX-License-Identifier: MPL-2.0
using System.Collections.Concurrent;
using Community.Zabbix.Hosting;
using Community.Zabbix.Storage;

namespace Community.Zabbix;

/// <summary>
/// Represents an agent's state as a health check sees it: how full its store is, what its send loop holds, when it last delivered or
/// was refused, and whether one of its loops is faulting. Written by the agent's send loop through
/// <see cref="IProgress{T}"/> (every report also counted on the agents' <c>zabbix.agent.values</c> instrument when
/// the host built it), read by <see cref="ZabbixAgentHealthCheck"/> or anything the host wires.
/// </summary>
public sealed class ZabbixAgentStatus : IProgress<ZabbixSendProgress>
{
    private readonly ZabbixAgentTelemetry? _telemetry;
    private readonly ConcurrentDictionary<string, Exception> _faults = new(StringComparer.Ordinal);
    private Func<int>? _stored;
    private int? _maxItems;
    private long _delivered;
    private long _rejected;
    private long _abandoned;
    private int _held;
    private long _startedTicks;
    private long _lastDeliveredTicks;
    private long _lastRejectedTicks;
    private readonly Lock _watch = new();
    private TimeProvider? _watchClock;         // the clock the last silence was measured on
    private long _watchedTicks;                // the proof instant (UTC ticks) it was measured from
    private long _watchedAt;                   // that clock's monotonic timestamp when the proof was first seen
    private TimeSpan _watchedAge;              // the proof's age then

    /// <summary>A status for the agent named <paramref name="name"/>; its reports are not counted anywhere.</summary>
    /// <param name="name">The agent's name.</param>
    public ZabbixAgentStatus(string name)
        : this(name, telemetry: null)
    {
    }

    // The hosts pass the agents' telemetry, so every report is also counted on the host's meters.
    internal ZabbixAgentStatus(string name, ZabbixAgentTelemetry? telemetry)
    {
        Name = name;
        _telemetry = telemetry;
    }

    /// <summary>The agent's name.</summary>
    public string Name { get; }

    /// <summary>Values held in the store now.</summary>
    public int Stored => _stored?.Invoke() ?? 0;

    /// <summary>The most values the store holds (its policy's <c>MaxItems</c>), when it has a cap.</summary>
    public int? MaxItems => _maxItems;

    /// <summary>Values the send loop has taken and holds for another attempt (a failed or paused delivery).</summary>
    public int Held => Volatile.Read(ref _held);

    /// <summary>Values acknowledged since start.</summary>
    public long Delivered => Interlocked.Read(ref _delivered);

    /// <summary>Values refused for good since start.</summary>
    public long Rejected => Interlocked.Read(ref _rejected);

    /// <summary>Values lost at stop since start (neither delivered nor taken back by the source).</summary>
    public long Abandoned => Interlocked.Read(ref _abandoned);

    /// <summary>When the agent started; <see langword="null"/> before.</summary>
    public DateTimeOffset? StartedAt => At(ref _startedTicks);

    /// <summary>When a batch was last acknowledged; <see langword="null"/> before the first. A rejected batch does not move it.</summary>
    public DateTimeOffset? LastDelivered => At(ref _lastDeliveredTicks);

    /// <summary>When a batch was last refused for good; <see langword="null"/> if none was.</summary>
    public DateTimeOffset? LastRejected => At(ref _lastRejectedTicks);

    /// <summary>A loop's latest fault, while it lasts (until that loop's next good pass); <see langword="null"/> when none is faulting.</summary>
    public Exception? Fault => _faults.Values.FirstOrDefault();

    /// <inheritdoc/>
    void IProgress<ZabbixSendProgress>.Report(ZabbixSendProgress value) => Report(value);

    /// <summary>Counts a delivery outcome the agent's send loop reports.</summary>
    /// <param name="value">The outcome, how many values and when.</param>
    internal void Report(ZabbixSendProgress value)
    {
        switch (value.Outcome)
        {
            case ZabbixSendOutcome.Delivered:
                Interlocked.Add(ref _delivered, value.Count);
                Interlocked.Exchange(ref _lastDeliveredTicks, value.At.UtcTicks);
                Volatile.Write(ref _held, 0);
                break;
            case ZabbixSendOutcome.Rejected:
                Interlocked.Add(ref _rejected, value.Count);
                Interlocked.Exchange(ref _lastRejectedTicks, value.At.UtcTicks);
                Volatile.Write(ref _held, 0);
                break;
            case ZabbixSendOutcome.Retried:
            case ZabbixSendOutcome.Held:
                Volatile.Write(ref _held, value.Count);
                break;
            case ZabbixSendOutcome.Returned:
                Volatile.Write(ref _held, 0);
                break;
            case ZabbixSendOutcome.Abandoned:
                Interlocked.Add(ref _abandoned, value.Count);
                Volatile.Write(ref _held, 0);
                break;
            default:
                throw new ArgumentOutOfRangeException(nameof(value), value.Outcome, "The delivery outcome is not one the agent knows.");
        }
        _telemetry?.Values(Name, value.Outcome, value.Count);
    }

    /// <summary>Watches <paramref name="store"/> for its depth and cap.</summary>
    /// <typeparam name="T">The store's record.</typeparam>
    /// <param name="store">The store.</param>
    internal void Attach<T>(ZabbixStore<T> store)
    {
        ArgumentNullException.ThrowIfNull(store);
        _maxItems = store.Policy.MaxItems;
        _stored = () => store.Count;
    }

    /// <summary>Marks the start, from which a never-delivering agent's grace is measured.</summary>
    /// <param name="at">The instant.</param>
    internal void Started(DateTimeOffset at) => Interlocked.Exchange(ref _startedTicks, at.UtcTicks);

    /// <summary>
    /// How long since the last proof that the destination works (the last delivery, else the start), measured on
    /// <paramref name="time"/>'s monotonic clock; <see langword="null"/> before the start. The wall instants name
    /// <i>when</i> and are kept for display; a duration read off them would move with every step of the wall clock.
    /// A report carries only its wall instant, so a proof is put on the monotonic clock when a reader first sees it:
    /// its age then is the wall difference, clamped to between zero and the monotonic time since the reader last looked
    /// (the proof is newer than that look), and from there it grows on <see cref="TimeProvider.GetElapsedTime(long)"/>.
    /// A health check is built for each run, so the watch lives here, with the status.
    /// </summary>
    /// <param name="time">The reader's clock.</param>
    /// <returns>The silence, or <see langword="null"/>.</returns>
    internal TimeSpan? Silence(TimeProvider time)
    {
        if ((LastDelivered ?? StartedAt) is not { } since) return null;
        lock (_watch)
        {
            if (!ReferenceEquals(_watchClock, time) || _watchedTicks != since.UtcTicks)
            {
                TimeSpan age = time.GetUtcNow() - since;
                if (age < TimeSpan.Zero) age = TimeSpan.Zero;
                if (ReferenceEquals(_watchClock, time) && time.GetElapsedTime(_watchedAt) is var looked && age > looked) age = looked;
                _watchClock = time;
                _watchedTicks = since.UtcTicks;
                _watchedAt = time.GetTimestamp();
                _watchedAge = age;
            }
            return _watchedAge + time.GetElapsedTime(_watchedAt);
        }
    }

    /// <summary>A pass of <paramref name="loop"/> faulted.</summary>
    internal void Faulted(string loop, Exception failure) => _faults[loop] = failure;

    /// <summary>A pass of <paramref name="loop"/> completed: its fault, if any, is over.</summary>
    internal void Recovered(string loop) => _faults.TryRemove(loop, out _);

    private static DateTimeOffset? At(ref long ticks)
        => Interlocked.Read(ref ticks) is var value && value > 0 ? new DateTimeOffset(value, TimeSpan.Zero) : null;
}
