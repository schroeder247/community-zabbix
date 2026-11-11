// This Source Code Form is subject to the terms of the Mozilla Public
// License, v. 2.0. Copyright (c) 2026 schroeder247. SPDX-License-Identifier: MPL-2.0
using System.Diagnostics;
using System.Diagnostics.Metrics;
using System.Threading.Channels;
using Community.Zabbix.Diagnostics;
using Microsoft.Extensions.Logging;

namespace Community.Zabbix.Storage;

/// <summary>
/// Represents a store: a writer face and two read faces. Producers write to it as a <see cref="ChannelWriter{T}"/>; the
/// one consumer reads it either as an <see cref="IAsyncEnumerable{T}"/> (a record leaves as it is read) or through the
/// acknowledged face (<see cref="WaitToLeaseAsync"/>, <see cref="TryLease"/>, <see cref="TryPeek"/>,
/// <see cref="Acknowledge"/>), where a leased record stays held until its destination says it has it.
/// </summary>
/// <remarks>
/// <para>The policy is stated once, at construction. A durable store keeps a leased record in its backend until it is
/// acknowledged, so a batch in flight when the process stops is leased again, with the same sequences, by the next
/// process; a memory store keeps nothing across a restart.</para>
/// <para>Every record the store does not keep is counted on <c>zabbix.store.dropped</c> (meter
/// <see cref="ZabbixTelemetryNames.Store"/>), tagged with the store's name and the reason (<c>refused</c>,
/// <c>discarded</c>, <c>expired</c>, or the overflow mode that shed it: <see cref="ZabbixStoreDropReason"/>).</para>
/// </remarks>
/// <typeparam name="T">The record.</typeparam>
public abstract class ZabbixStore<T> : ChannelWriter<T>, IAsyncEnumerable<T>, IAsyncDisposable, IZabbixDurableWriter<T>, IZabbixTakingWriter<T>
{
    private readonly ZabbixStoreTelemetry _telemetry;

    /// <summary>Initialises the store's name, logging and telemetry.</summary>
    /// <param name="services">The store's services; <see cref="ZabbixStoreServices.Default"/> when <see langword="null"/>.</param>
    protected ZabbixStore(ZabbixStoreServices? services = null)
    {
        Services = services ?? ZabbixStoreServices.Default;
        Logger = Services.LoggerFactory.CreateLogger(GetType());
        _telemetry = new ZabbixStoreTelemetry(Services.Meters, Name, () => Count);
    }

    /// <summary>The services the store was built with.</summary>
    public ZabbixStoreServices Services { get; }

    /// <summary>The store's name.</summary>
    public string Name => Services.Name;

    /// <summary>Where the store logs, categorised to the concrete type; a subclass may log through it too.</summary>
    protected ILogger Logger { get; }

    /// <summary>The policy this store enforces.</summary>
    public abstract ZabbixStorePolicy<T> Policy { get; }

    /// <summary>Records currently held; on a durable store this includes records leased and not yet acknowledged.</summary>
    public abstract int Count { get; }

    /// <summary>Whether the writer face has been completed: nothing more will be admitted.</summary>
    public abstract bool IsCompleted { get; }

    /// <summary>Records the store did not keep, over its lifetime (every reason).</summary>
    public long DroppedCount => _telemetry.Total;

    /// <inheritdoc/>
    public abstract IAsyncEnumerator<T> GetAsyncEnumerator(CancellationToken cancellationToken = default);

    /// <summary>
    /// Waits until a record can be leased: <see langword="true"/> when one is ready, <see langword="false"/> when the store is
    /// complete and every record has been leased. At that point it throws the completion error, if the writer completed
    /// with one, as <see cref="ChannelReader{T}.WaitToReadAsync"/> does.
    /// </summary>
    /// <param name="cancellationToken">Stops the wait.</param>
    /// <returns>Whether a record is ready.</returns>
    public abstract ValueTask<bool> WaitToLeaseAsync(CancellationToken cancellationToken = default);

    /// <summary>
    /// Hands out the oldest record not yet leased; the store keeps it until <see cref="Acknowledge"/> covers its sequence.
    /// A record held longer than <see cref="ZabbixStorePolicy{T}.MaxAge"/>, or matching the discard rule, is dropped
    /// (counted) instead of handed out.
    /// </summary>
    /// <param name="entry">The record and its sequence.</param>
    /// <returns>Whether a record was ready.</returns>
    public abstract bool TryLease(out ZabbixStoreEntry<T> entry);

    /// <summary>The record <see cref="TryLease"/> would hand out next, without leasing it: whether there is more, and how old it is.</summary>
    /// <param name="entry">The record and its sequence.</param>
    /// <returns>Whether a record was ready.</returns>
    public abstract bool TryPeek(out ZabbixStoreEntry<T> entry);

    /// <summary>
    /// Acknowledges cumulatively: the destination has every leased record up to and including
    /// <paramref name="sequence"/>, so they leave the store — not only the one <paramref name="sequence"/> names, everything leased at or below it. A sequence
    /// beyond what was leased acknowledges only what was leased; a sequence at or below what is already gone does nothing.
    /// </summary>
    /// <param name="sequence">The highest sequence the destination took.</param>
    public abstract void Acknowledge(long sequence);

    /// <summary>
    /// Rewinds the lease cursor. Every record leased above <paramref name="sequence"/> that is not yet acknowledged is
    /// handed out again: the next <see cref="TryLease"/> calls return it, oldest first, with the same sequence, before
    /// anything not yet leased. For a consumer that abandons a leased batch without acknowledging or keeping it; unlike
    /// <see cref="Acknowledge"/>, nothing here removes a record. A
    /// <paramref name="sequence"/> at or above the cursor releases nothing.
    /// </summary>
    /// <param name="sequence">The lease cursor's new position; everything leased above it goes back.</param>
    public abstract void Release(long sequence);

    /// <summary>
    /// Writes <paramref name="item"/>, waiting for room under <see cref="BoundedChannelFullMode.Wait"/>. A record the policy
    /// refuses is counted and the call returns: it is taken and dropped, as a bounded channel's drop modes are. Completion
    /// is answered first: after it, every write throws <see cref="ChannelClosedException"/>, refused or not.
    /// </summary>
    /// <param name="item">The record.</param>
    /// <param name="cancellationToken">Stops the wait.</param>
    /// <returns>When the record is written or dropped.</returns>
    /// <exception cref="ChannelClosedException">The writer face was completed.</exception>
    public override ValueTask WriteAsync(T item, CancellationToken cancellationToken = default)
    {
        if (!IsCompleted && GetRefusalReason(item) is { } reason)
        {
            Drop(reason);
            return ValueTask.CompletedTask;
        }
        return base.WriteAsync(item, cancellationToken);
    }

    /// <summary>
    /// Writes without waiting for room, and answers once the record is as safe as this store makes it: kept in memory for
    /// a memory store, committed for a durable one. <see langword="false"/> when the policy did not keep it.
    /// </summary>
    /// <param name="item">The record.</param>
    /// <param name="cancellationToken">Stops the wait for the commit.</param>
    /// <returns>Whether the record was kept.</returns>
    internal virtual ValueTask<bool> TryWriteDurablyAsync(T item, CancellationToken cancellationToken) => ValueTask.FromResult(TryWrite(item));

    ValueTask<bool> IZabbixDurableWriter<T>.TryWriteDurablyAsync(T item, CancellationToken cancellationToken) => TryWriteDurablyAsync(item, cancellationToken);

    /// <summary>
    /// Offers without waiting: <see langword="true"/> when the record was taken, kept or dropped by the policy (refused,
    /// discarded or shed, each counted once); <see langword="false"/> when the store is full under
    /// <see cref="BoundedChannelFullMode.Wait"/> or completed. A store whose <see cref="ChannelWriter{T}.TryWrite"/>
    /// already answers so needs no override.
    /// </summary>
    /// <param name="item">The record.</param>
    /// <returns>Whether the record was taken.</returns>
    internal virtual bool TryTake(T item) => TryWrite(item);

    /// <summary>Whether what the store holds outlives the process (a backend), rather than going with its disposal.</summary>
    internal virtual bool IsDurable => false;

    bool IZabbixTakingWriter<T>.TryTake(T item) => TryTake(item);

    /// <inheritdoc/>
    public virtual ValueTask DisposeAsync()
    {
        _telemetry.Dispose();
        GC.SuppressFinalize(this);
        return ValueTask.CompletedTask;
    }

    /// <summary>Gets why the policy's gate keeps a record out of the store, if it does.</summary>
    /// <param name="item">The record.</param>
    /// <returns>
    /// <see cref="ZabbixStoreDropReason.Refused"/> or <see cref="ZabbixStoreDropReason.Discarded"/>; <see langword="null"/>
    /// when the record may enter.
    /// </returns>
    protected ZabbixStoreDropReason? GetRefusalReason(T item)
        => Policy.Admit is { } admit && !admit(item) ? ZabbixStoreDropReason.Refused
        : Policy.Discard is { } discard && discard(item) ? ZabbixStoreDropReason.Discarded
        : null;

    /// <summary>Gets why the policy drops a held record instead of handing it out, if it does.</summary>
    /// <param name="entry">The held record.</param>
    /// <returns>
    /// <see cref="ZabbixStoreDropReason.Expired"/> or <see cref="ZabbixStoreDropReason.Discarded"/>; <see langword="null"/>
    /// when the record may be handed out.
    /// </returns>
    protected ZabbixStoreDropReason? GetExpiryReason(in ZabbixStoreEntry<T> entry)
        => Policy.MaxAge is { } age && Services.Time.GetUtcNow() - entry.Stored > age ? ZabbixStoreDropReason.Expired
        : Policy.Discard is { } discard && discard(entry.Record) ? ZabbixStoreDropReason.Discarded
        : null;

    /// <summary>Counts one record the store didn't keep, and warns at 1, 10, 100, … of each reason.</summary>
    /// <param name="reason">Why the store didn't keep it.</param>
    /// <exception cref="ArgumentOutOfRangeException"><paramref name="reason"/> isn't one of <see cref="ZabbixStoreDropReason"/>'s.</exception>
    protected void Drop(ZabbixStoreDropReason reason)
    {
        string tag = TagOf(reason);
        long dropped = _telemetry.Dropped(Name, tag);
        if (ZabbixLogSampling.IsPowerOfTen(dropped)) StoreLog.StorageDropping(Logger, Name, tag, dropped);
    }

    /// <summary>The reason as the <c>zabbix.store.reason</c> tag spells it.</summary>
    /// <param name="reason">Why the store dropped items.</param>
    /// <returns>The tag's value.</returns>
    internal static string TagOf(ZabbixStoreDropReason reason) => reason switch
    {
        ZabbixStoreDropReason.Refused => "refused",
        ZabbixStoreDropReason.Discarded => "discarded",
        ZabbixStoreDropReason.Expired => "expired",
        ZabbixStoreDropReason.DropOldest => "drop_oldest",
        ZabbixStoreDropReason.DropNewest => "drop_newest",
        ZabbixStoreDropReason.DropWrite => "drop_write",
        ZabbixStoreDropReason.Faulted => "faulted",
        _ => throw new ArgumentOutOfRangeException(nameof(reason), reason, "The drop reason is not one the store knows."),
    };

    /// <summary>Counts records the consumer acknowledged.</summary>
    /// <param name="count">How many.</param>
    protected void Acknowledged(long count) => _telemetry.Acknowledged(Name, count);

    /// <summary>The drop reason a channel full mode sheds under.</summary>
    /// <param name="mode">The mode.</param>
    /// <returns>The reason.</returns>
    private protected static ZabbixStoreDropReason DropReasonOf(BoundedChannelFullMode mode) => mode switch
    {
        BoundedChannelFullMode.DropOldest => ZabbixStoreDropReason.DropOldest,
        BoundedChannelFullMode.DropNewest => ZabbixStoreDropReason.DropNewest,
        BoundedChannelFullMode.DropWrite => ZabbixStoreDropReason.DropWrite,
        BoundedChannelFullMode.Wait => throw new UnreachableException("The Wait mode sheds nothing."),
        _ => throw new ArgumentOutOfRangeException(nameof(mode), mode, "The full mode is not one the store knows."),
    };

}

/// <summary>
/// The store layer's instruments (meter <see cref="ZabbixTelemetryNames.Store"/>): <c>zabbix.store.dropped</c> by reason,
/// <c>zabbix.store.acknowledged</c>, and <c>zabbix.store.pending</c>, an observable up-down counter of the records held
/// (the store's <see cref="ZabbixStore{T}.Count"/>, read when collected, so the write path pays nothing for it).
/// </summary>
internal sealed class ZabbixStoreTelemetry : IDisposable
{
    public const string DroppedInstrument = "zabbix.store.dropped";
    public const string AcknowledgedInstrument = "zabbix.store.acknowledged";
    public const string PendingInstrument = "zabbix.store.pending";
    public const string NameTag = "zabbix.store.name";
    public const string ReasonTag = "zabbix.store.reason";

    private readonly Meter? _owned;
    private readonly Meter _meter;                     // the host factory's, which disposes it; or _owned
    private readonly Counter<long> _dropped;
    private readonly Counter<long> _acknowledged;
    private readonly System.Collections.Concurrent.ConcurrentDictionary<string, long> _byReason = new(StringComparer.Ordinal);
    private long _total;
    private volatile bool _disposed;

    public ZabbixStoreTelemetry(IMeterFactory? meters, string store, Func<int> held)
    {
        _meter = meters?.Create(ZabbixTelemetryNames.Store) ?? (_owned = new Meter(ZabbixTelemetryNames.Store));
        _dropped = _meter.CreateCounter<long>(DroppedInstrument, unit: "{record}", description: "Records a store did not keep, by reason.");
        _acknowledged = _meter.CreateCounter<long>(AcknowledgedInstrument, unit: "{record}", description: "Records a store's consumer acknowledged: the destination has them.");
        // One observable instrument per store (its callback is this store's count); a disposed store reports nothing.
        Func<IEnumerable<Measurement<long>>> observe = () => _disposed ? [] : [new Measurement<long>(held(), new KeyValuePair<string, object?>(NameTag, store))];
        _meter.CreateObservableUpDownCounter(PendingInstrument, observe, unit: "{record}", description: "Records a store holds, leased or not.");
    }

    public long Total => Interlocked.Read(ref _total);

    /// <summary>Counts one drop and returns how many of this reason the store has dropped.</summary>
    public long Dropped(string store, string reason)
    {
        _dropped.Add(1, new TagList { { NameTag, store }, { ReasonTag, reason } });
        Interlocked.Increment(ref _total);
        return _byReason.AddOrUpdate(reason, 1, static (_, count) => count + 1);
    }

    public void Acknowledged(string store, long count)
    {
        if (count > 0) _acknowledged.Add(count, new TagList { { NameTag, store } });
    }

    public void Dispose()
    {
        _disposed = true;
        _owned?.Dispose();
    }
}
