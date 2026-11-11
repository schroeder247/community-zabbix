// This Source Code Form is subject to the terms of the Mozilla Public
// License, v. 2.0. Copyright (c) 2026 schroeder247. SPDX-License-Identifier: MPL-2.0
using System.Diagnostics.CodeAnalysis;
using System.Threading.Channels;

namespace Community.Zabbix.Storage;

/// <summary>
/// Represents an in-memory store: a <see cref="Channel{T}"/> with the policy as its options.
/// <see cref="ZabbixStorePolicy{T}.MaxItems"/> is the bound and <see cref="ZabbixStorePolicy{T}.Overflow"/> is the
/// channel's own <see cref="BoundedChannelFullMode"/>; a drop is the channel's <c>itemDropped</c> callback, counted.
/// <see cref="BoundedChannelFullMode.DropWrite"/> is the one mode the store sheds itself, over a channel that waits, so a
/// receiver that answers per value (a trapper) learns which value was not kept. No cap means an unbounded channel. The predicates gate the door (<c>Admit</c>, <c>Discard</c>); <c>Discard</c> and
/// <c>MaxAge</c> also filter the way out; each is counted.
/// </summary>
/// <remarks>
/// Entries are stamped with their arrival time at the door and numbered on the way out, so sequences follow the
/// channel's own order. Numbering at the door would let concurrent writers race into the channel with out-of-order
/// sequences, and a Zabbix server that de-duplicates on <c>id &gt; last_id</c> silently drops those values. A record the
/// channel sheds never takes a sequence. <see cref="Acknowledge"/> only counts: a restart loses a lease with everything
/// else in memory. <see cref="Release"/> keeps the entry in the store (the channel gave it up on read), so
/// <see cref="TryLease"/> hands it out again, oldest first, ahead of the channel, with the same sequence.
/// </remarks>
/// <typeparam name="T">The record.</typeparam>
public sealed class ZabbixMemoryStore<T> : ZabbixStore<T>
{
    private readonly Channel<ZabbixStoreEntry<T>> _channel;
    private readonly ZabbixStorePolicy<T> _policy;
    private readonly List<ZabbixStoreEntry<T>> _leased = [];          // leased and not yet acknowledged, ascending sequence
    private readonly Queue<ZabbixStoreEntry<T>> _released = new();   // released, ahead of the channel: TryLease drains this first
    private readonly Lock _gate = new();
    private long _sequence;                                                          // the last sequence handed out; under _gate
    private volatile bool _completed;
    private volatile bool _disposed;

    /// <summary>A store with <paramref name="policy"/> (the defaults when <see langword="null"/>: 100,000 records, drop oldest).</summary>
    /// <param name="policy">The policy.</param>
    /// <param name="services">The store's name, logging, meters and clock; the defaults when <see langword="null"/>.</param>
    /// <exception cref="ArgumentOutOfRangeException"><c>MaxItems</c> is below 1.</exception>
    public ZabbixMemoryStore(ZabbixStorePolicy<T>? policy = null, ZabbixStoreServices? services = null)
        : base(services)
    {
        _policy = policy ?? new ZabbixStorePolicy<T>();
        if (_policy.MaxItems is < 1) throw new ArgumentOutOfRangeException(nameof(policy), "MaxItems must be at least 1 when set");

        // DropWrite sheds the incoming record, and the channel's TryWrite answers true for it as for a kept one: the store
        // sheds it itself over a waiting channel instead, so it knows.
        BoundedChannelFullMode mode = _policy.Overflow == BoundedChannelFullMode.DropWrite ? BoundedChannelFullMode.Wait : _policy.Overflow;
        // The channel calls back only when it sheds, which a waiting channel never does.
        ZabbixStoreDropReason shed = mode == BoundedChannelFullMode.Wait ? ZabbixStoreDropReason.DropWrite : DropReasonOf(mode);
        // SingleReader is a promise: only the consumer reads the channel. The store's bound is its item cap,
        // which the channel enforces itself; nothing sheds by reading from a writer's thread.
        _channel = _policy.MaxItems is int cap
            ? Channel.CreateBounded<ZabbixStoreEntry<T>>(new BoundedChannelOptions(cap) { FullMode = mode, SingleReader = true }, _ => Drop(shed))
            : Channel.CreateUnbounded<ZabbixStoreEntry<T>>(new UnboundedChannelOptions { SingleReader = true });
    }

    /// <inheritdoc/>
    public override ZabbixStorePolicy<T> Policy => _policy;

    /// <summary>Records held and not leased: those in the channel, and those released back to it.</summary>
    public override int Count
    {
        get
        {
            lock (_gate) return _channel.Reader.Count + _released.Count;
        }
    }

    /// <inheritdoc/>
    public override bool IsCompleted => _completed;

    /// <inheritdoc/>
    public override bool TryWrite(T item) => Offer(item) is Offered.Kept or Offered.Shed;   // refused: the caller learns it was not kept

    /// <inheritdoc/>
    internal override bool TryTake(T item) => Offer(item) is Offered.Kept or Offered.Shed or Offered.Refused;

    /// <summary>Kept in memory: <see langword="true"/> only for a record the store holds now.</summary>
    internal override ValueTask<bool> TryWriteDurablyAsync(T item, CancellationToken cancellationToken) => ValueTask.FromResult(Offer(item) == Offered.Kept);

    private enum Offered
    {
        Kept,
        Shed,
        Refused,
        Full,
        Closed,
    }

    private Offered Offer(T item)
    {
        if (_completed) return Offered.Closed;                                          // closed: not a drop, a refusal
        if (GetRefusalReason(item) is { } refusal)
        {
            Drop(refusal);
            return Offered.Refused;
        }
        if (_channel.Writer.TryWrite(Entry(item))) return Offered.Kept;                   // under DropOldest or DropNewest an older record was shed, counted
        if (_completed) return Offered.Closed;
        if (_policy.Overflow != BoundedChannelFullMode.DropWrite) return Offered.Full;
        Drop(ZabbixStoreDropReason.DropWrite);
        return Offered.Shed;
    }

    /// <inheritdoc/>
    public override ValueTask<bool> WaitToWriteAsync(CancellationToken cancellationToken = default)
        => _policy.Overflow == BoundedChannelFullMode.DropWrite && !_completed ? ValueTask.FromResult(true) : _channel.Writer.WaitToWriteAsync(cancellationToken);

    /// <inheritdoc/>
    public override ValueTask WriteAsync(T item, CancellationToken cancellationToken = default)
    {
        if (!_completed && GetRefusalReason(item) is { } refusal)                              // closed first: the channel's own ChannelClosedException
        {
            Drop(refusal);
            return ValueTask.CompletedTask;
        }
        if (_policy.Overflow == BoundedChannelFullMode.DropWrite && !_completed)
        {
            ZabbixStoreEntry<T> entry = Entry(item);
            if (_channel.Writer.TryWrite(entry)) return ValueTask.CompletedTask;
            if (!_completed)
            {
                Drop(ZabbixStoreDropReason.DropWrite);                                  // full: the incoming record is shed, as the channel's DropWrite would
                return ValueTask.CompletedTask;
            }
        }
        return _channel.Writer.WriteAsync(Entry(item), cancellationToken);                // the channel's own pooled wait, no layer over it
    }

    // Not yet numbered: TryLease gives the sequence as it takes the entry out of the channel, in the channel's order.
    private ZabbixStoreEntry<T> Entry(T item) => new(0, Services.Time.GetUtcNow(), item);

    /// <inheritdoc/>
    public override bool TryComplete(Exception? error = null)
    {
        _completed = true;
        return _channel.Writer.TryComplete(error);
    }

    /// <inheritdoc/>
    public override ValueTask<bool> WaitToLeaseAsync(CancellationToken cancellationToken = default)
    {
        if (_disposed) return ValueTask.FromResult(false);                              // disposed: what it held is gone
        lock (_gate) if (_released.Count > 0) return ValueTask.FromResult(true);
        return _channel.Reader.WaitToReadAsync(cancellationToken);
    }

    /// <inheritdoc/>
    public override bool TryLease(out ZabbixStoreEntry<T> entry)
    {
        lock (_gate)
        {
            if (_disposed)
            {
                entry = default;
                return false;
            }
            while (_released.TryDequeue(out entry))
            {
                if (Expired(entry)) continue;
                _leased.Add(entry);
                return true;
            }
            // Read, number and hold as one step, so _leased stays ascending and a Release or Acknowledge never sees an
            // entry out of the channel without its sequence. The lease is the channel's one reader (SingleReader); an
            // overflow sheds inside the channel before an entry is numbered, so a drop never leaves a gap in the sequence.
            while (_channel.Reader.TryRead(out entry))
            {
                if (Expired(entry)) continue;
                entry = entry with { Sequence = ++_sequence };
                _leased.Add(entry);
                return true;
            }
        }
        entry = default;
        return false;
    }

    /// <inheritdoc/>
    public override bool TryPeek(out ZabbixStoreEntry<T> entry)
    {
        lock (_gate)
        {
            if (_disposed)
            {
                entry = default;
                return false;
            }
            if (_released.TryPeek(out entry)) return true;
            while (_channel.Reader.TryPeek(out entry))
            {
                if (!Expired(entry))
                {
                    entry = entry with { Sequence = _sequence + 1 };                     // the sequence TryLease will give it
                    return true;
                }
                _channel.Reader.TryRead(out _);                                         // the one reader: it is the one peeked
            }
        }
        return false;
    }

    /// <inheritdoc/>
    public override void Acknowledge(long sequence)
    {
        int count = 0;
        lock (_gate)
        {
            while (count < _leased.Count && _leased[count].Sequence <= sequence) count++;
            if (count > 0) _leased.RemoveRange(0, count);
        }
        Acknowledged(count);
    }

    /// <inheritdoc/>
    public override void Release(long sequence)
    {
        lock (_gate)
        {
            // _leased is ascending by sequence, so what is leased above the cursor is its tail. It goes back merged with
            // what an earlier release still holds, by sequence: records handed out again from an earlier release are in
            // the tail, and the rest of that release may be younger or older than they are (released to 2, one leased
            // again, then released to 0), so appending would hand them out, and hold them in _leased, out of order.
            int cut = _leased.Count;
            while (cut > 0 && _leased[cut - 1].Sequence > sequence) cut--;
            if (cut == _leased.Count) return;
            if (_released.Count == 0)
            {
                for (int i = cut; i < _leased.Count; i++) _released.Enqueue(_leased[i]);
            }
            else
            {
                ZabbixStoreEntry<T>[] earlier = _released.ToArray();
                _released.Clear();
                int a = 0, b = cut;
                while (a < earlier.Length || b < _leased.Count)
                    _released.Enqueue(b == _leased.Count || (a < earlier.Length && earlier[a].Sequence < _leased[b].Sequence) ? earlier[a++] : _leased[b++]);
            }
            _leased.RemoveRange(cut, _leased.Count - cut);
        }
    }

    /// <summary>Completes the writer face: a pending lease wait or stream read ends, and what the store held is gone with it.</summary>
    /// <returns>When done.</returns>
    public override async ValueTask DisposeAsync()
    {
        _completed = true;
        lock (_gate) _disposed = true;                                                  // after this no lease or peek hands anything out
        _ = _channel.Writer.TryComplete();                                                 // false: completed before disposal
        await base.DisposeAsync().ConfigureAwait(false);
    }

    private bool Expired(in ZabbixStoreEntry<T> entry)
    {
        if (GetExpiryReason(entry) is not { } reason) return false;
        Drop(reason);
        return true;
    }

    /// <summary>The records, in arrival order, as they arrive; ends when the store is complete and empty.</summary>
    /// <param name="cancellationToken">Stops the stream.</param>
    /// <returns>The records.</returns>
    public override IAsyncEnumerator<T> GetAsyncEnumerator(CancellationToken cancellationToken = default) => ReadAsync(cancellationToken);

    // What a lease reader released comes first: it is older than anything still in the channel.
    private async IAsyncEnumerator<T> ReadAsync(CancellationToken cancellationToken)
    {
        while (true)
        {
            while (TryTakeReleased(out T? released)) yield return released;
            if (!await _channel.Reader.WaitToReadAsync(cancellationToken).ConfigureAwait(false)) break;
            while (!_disposed && _channel.Reader.TryRead(out ZabbixStoreEntry<T> entry))
            {
                if (Expired(entry)) continue;
                yield return entry.Record;
            }
        }
        while (TryTakeReleased(out T? released)) yield return released;
    }

    private bool TryTakeReleased([MaybeNullWhen(false)] out T record)
    {
        lock (_gate)
        {
            while (!_disposed && _released.TryDequeue(out ZabbixStoreEntry<T> entry))
            {
                if (Expired(entry)) continue;
                record = entry.Record;
                return true;
            }
        }
        record = default;
        return false;
    }
}
