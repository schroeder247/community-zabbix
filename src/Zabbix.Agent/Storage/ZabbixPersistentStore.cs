// This Source Code Form is subject to the terms of the Mozilla Public
// License, v. 2.0. Copyright (c) 2026 schroeder247. SPDX-License-Identifier: MPL-2.0
using System.Diagnostics;
using System.Runtime.ExceptionServices;
using System.Threading.Channels;

namespace Community.Zabbix.Storage;

/// <summary>
/// Provides the base for a durable store. A subclass supplies four asynchronous operations — open the backend, append a
/// batch (and the sequences it got), load the oldest records after a sequence, remove a range of sequences — and this
/// class is the writer face, the policy enforcement, the lease cursor and both read faces.
/// </summary>
/// <remarks>
/// <para>No backend operation runs on a caller's thread or under a lock. One loop, owned by the store, runs them one at a
/// time: it commits what was written (every record written while a commit is under way goes in the next one, in write
/// order), removes what was acknowledged, and loads ahead of the lease cursor when the consumer has run out. The faces
/// work on memory only: a record is leased from what has been committed and loaded, an acknowledgement takes records out
/// of the count at once and out of the backend on the loop's next turn.</para>
/// <para>What "written" means depends on the face. <see cref="WriteAsync"/> returns when the record is committed (or
/// dropped by the policy), and throws the backend's fault when the commit failed, so its completion means "durable".
/// <see cref="TryWrite"/>, synchronous by the <see cref="ChannelWriter{T}"/> contract, returns once the policy has
/// accepted the record: it is committed shortly after, and a record whose commit fails is dropped, logged (event 2000) and
/// counted (<c>faulted</c>). A commit that fails is retried record by record, so one record the backend cannot hold fails
/// alone.</para>
/// <para>The lease cursor lives in memory: a restarted process leases every record still in the backend again, oldest
/// first, with the sequence it was stored with. The sequence is the backend's own and only grows, which lets a peer
/// de-duplicate a re-sent record. <see cref="Release"/> does the same rewind within one process. An acknowledged record
/// whose removal has not reached the backend when the process stops is leased again by the next one.</para>
/// <para><c>DropOldest</c> sheds the oldest record not yet leased, or the incoming one when every held record is leased;
/// <see cref="BoundedChannelFullMode.DropNewest"/> is refused at construction. Under <c>Wait</c> a writer waits on a signal
/// raised each time records leave the store, so there is no polling. Until the backend has been opened (at the first
/// use), <see cref="Count"/> counts only what this process wrote.</para>
/// </remarks>
/// <typeparam name="T">The record.</typeparam>
public abstract class ZabbixPersistentStore<T> : ZabbixStore<T>
{
    /// <summary>How many records one load reads ahead of the lease cursor.</summary>
    public const int ReadAhead = 256;

    // Committed records kept in memory past the lease cursor before the rest is left to a later load.
    private const int MemoryTail = 4096;

    // The most records one commit takes; the rest go in the next.
    private const int MaxBatch = 1024;

    private readonly ZabbixStorePolicy<T> _policy;
    private readonly Lock _gate = new();
    // The loop's work signal, the lease waiter's and a waiting writer's: one slot each, extra writes dropped, because
    // "there is something to do", "there is something to lease" and "there is room" need saying once.
    private readonly Channel<bool> _work = Channel.CreateBounded<bool>(new BoundedChannelOptions(1) { FullMode = BoundedChannelFullMode.DropWrite, SingleReader = true });
    private readonly Channel<bool> _wake = Channel.CreateBounded<bool>(new BoundedChannelOptions(1) { FullMode = BoundedChannelFullMode.DropWrite, SingleReader = true });
    private readonly Channel<bool> _room = Channel.CreateBounded<bool>(new BoundedChannelOptions(1) { FullMode = BoundedChannelFullMode.DropWrite });
    private Task? _loop;                                                // started at the first use (Kick), never from the constructor

    // ----- under _gate ----------------------------------------------------------------------------------------------
    private readonly List<Pending> _pending = [];                       // accepted, not yet committed, in write order, from _pendingFrom
    private int _pendingFrom;                                           // the entries of _pending before it were shed (DropOldest)
    private readonly List<ZabbixStoreEntry<T>> _ahead = [];             // committed, in memory, not leased; ascending from _head
    private readonly List<ZabbixStoreEntry<T>> _leased = [];            // leased, not acknowledged; ascending, all below _ahead
    private readonly List<long> _doomed = [];                           // dropped from memory, still to remove from the backend
    private readonly List<TaskCompletionSource> _turns = [];            // FlushAsync callers, told when the next whole turn ends
    private readonly List<ZabbixStoreDropReason> _shed = [];                           // reasons of records dropped under the gate, counted after it
    private TaskCompletionSource _opened = NewSignal();
    private int _head;
    private int _held;                                                  // the count: committed and held, plus pending
    private int _committing;                                            // taken from _pending by the commit under way
    private int _shedFromBackend;                                       // oldest unloaded records DropOldest has shed
    private long _loadedThrough;                                        // every record at or below it was brought into memory
    private long _removeThrough;                                        // acknowledged through this sequence
    private long _removedThrough;                                       // the backend has removed through this sequence
    private bool _ready;
    private bool _tailInMemory;                                         // no committed record above the memory's tail is only in the backend
    private bool _loadWanted;
    private Exception? _fault;                                          // a load's fault, for the next lease wait
    private Exception? _error;
    private volatile bool _completed;
    private volatile bool _disposed;

    /// <summary>Initialises the machinery.</summary>
    /// <param name="policy">The policy; the defaults when <see langword="null"/>.</param>
    /// <param name="services">The store's name, logging, meters and clock; the defaults when <see langword="null"/>.</param>
    /// <exception cref="ArgumentOutOfRangeException">The policy asks for <see cref="BoundedChannelFullMode.DropNewest"/>
    /// or a mode the channel does not define, or <c>MaxItems</c> below 1.</exception>
    protected ZabbixPersistentStore(ZabbixStorePolicy<T>? policy = null, ZabbixStoreServices? services = null)
        : base(services)
    {
        _policy = policy ?? new ZabbixStorePolicy<T>();
        if (_policy.Overflow is not (BoundedChannelFullMode.DropOldest or BoundedChannelFullMode.DropWrite or BoundedChannelFullMode.Wait))
            throw new ArgumentOutOfRangeException(nameof(policy), _policy.Overflow, "A persistent store sheds the oldest (DropOldest), refuses the incoming (DropWrite) or waits (Wait).");
        if (_policy.MaxItems is < 1) throw new ArgumentOutOfRangeException(nameof(policy), "MaxItems must be at least 1 when set");
    }

    /// <inheritdoc/>
    public override ZabbixStorePolicy<T> Policy => _policy;

    /// <summary>Records held: committed and not acknowledged (leased or not), and written and not yet committed. Never touches the backend.</summary>
    public override int Count => Volatile.Read(ref _held);

    /// <inheritdoc/>
    public override bool IsCompleted => _completed;

    /// <summary>Opens the backend (creates what it needs) and answers how many records it already holds. Called once, by the store's loop; a failure is retried at the next use.</summary>
    /// <param name="cancellationToken">A cancellation token.</param>
    /// <returns>How many records the backend holds.</returns>
    protected abstract ValueTask<int> OpenAsync(CancellationToken cancellationToken);

    /// <summary>
    /// Appends <paramref name="records"/> in one commit, all or nothing, and answers the sequence each got, in order; every
    /// sequence must exceed every sequence the backend ever gave.
    /// </summary>
    /// <param name="records">The records and when each was stored (their <see cref="ZabbixStoreEntry{T}.Sequence"/> is 0).</param>
    /// <param name="cancellationToken">A cancellation token.</param>
    /// <returns>The sequences, one per record.</returns>
    protected abstract ValueTask<IReadOnlyList<long>> AppendAsync(IReadOnlyList<ZabbixStoreEntry<T>> records, CancellationToken cancellationToken);

    /// <summary>Loads, without removing, up to <paramref name="count"/> held records with the lowest sequences above <paramref name="sequence"/>, ascending.</summary>
    /// <param name="sequence">The sequence to load after; 0 for the oldest.</param>
    /// <param name="count">The most to load.</param>
    /// <param name="cancellationToken">A cancellation token.</param>
    /// <returns>The records.</returns>
    protected abstract ValueTask<IReadOnlyList<ZabbixStoreEntry<T>>> LoadAfterAsync(long sequence, int count, CancellationToken cancellationToken);

    /// <summary>Removes the held records whose sequence is between <paramref name="first"/> and <paramref name="last"/>, inclusive.</summary>
    /// <param name="first">The lowest sequence removed.</param>
    /// <param name="last">The highest sequence removed.</param>
    /// <param name="cancellationToken">A cancellation token.</param>
    /// <returns>How many were removed.</returns>
    protected abstract ValueTask<int> RemoveAsync(long first, long last, CancellationToken cancellationToken);

    /// <summary>Releases the store (a connection, a file); a subclass that holds one overrides this.</summary>
    /// <returns>When released.</returns>
    protected virtual ValueTask DisposeStoreAsync() => ValueTask.CompletedTask;

    // ----- the writer face ---------------------------------------------------------------------------------------

    /// <summary>
    /// Accepts <paramref name="item"/> under the policy; it is committed by the store's loop shortly after. A record whose
    /// commit fails is dropped, logged (event 2000) and counted <c>faulted</c>. Use <see cref="WriteAsync"/> to wait for the commit.
    /// </summary>
    /// <param name="item">The record.</param>
    /// <returns>Whether it was taken (or taken and dropped by the overflow policy, as a bounded channel's drop modes are).</returns>
    public override bool TryWrite(T item) => Accept(item, done: null) is Accepted.Taken or Accepted.Dropped;

    /// <inheritdoc/>
    internal override bool IsDurable => true;

    /// <inheritdoc/>
    internal override bool TryTake(T item)
    {
        if (!_completed && GetRefusalReason(item) is { } refusal)
        {
            Drop(refusal);
            return true;                                                                // taken and dropped: not offered again
        }
        return Accept(item, done: null, refuseChecked: true) is Accepted.Taken or Accepted.Dropped;
    }

    /// <summary>
    /// Writes <paramref name="item"/> and waits for its commit: when this completes the record is in the backend, or was
    /// dropped by the policy (counted). Under <see cref="BoundedChannelFullMode.Wait"/> it first waits for room.
    /// Cancellation stops the waits; a record already handed to the loop is still committed.
    /// </summary>
    /// <param name="item">The record.</param>
    /// <param name="cancellationToken">Stops the waits.</param>
    /// <returns>When the record is committed or dropped.</returns>
    /// <exception cref="ChannelClosedException">The writer face was completed.</exception>
    public override async ValueTask WriteAsync(T item, CancellationToken cancellationToken = default)
    {
        while (true)
        {
            if (_completed) throw new ChannelClosedException();
            if (GetRefusalReason(item) is { } refusal)
            {
                Drop(refusal);
                return;
            }
            await OpenedAsync(cancellationToken).ConfigureAwait(false);
            var done = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
            switch (Accept(item, done, refuseChecked: true))
            {
                case Accepted.Taken:
                    await done.Task.WaitAsync(cancellationToken).ConfigureAwait(false);
                    return;
                case Accepted.Dropped:
                    return;
                case Accepted.Closed:
                    throw new ChannelClosedException();
                case Accepted.Full:
                    if (!await WaitForRoomAsync(cancellationToken).ConfigureAwait(false)) throw new ChannelClosedException();
                    break;
                default:
                    throw new UnreachableException();
            }
        }
    }

    /// <summary>
    /// Writes without waiting for room and answers when the record is committed: <see langword="true"/> when it is in the
    /// backend, <see langword="false"/> when the policy did not keep it (full under <c>Wait</c> or <c>DropWrite</c>,
    /// refused, completed). A backend fault is thrown.
    /// </summary>
    internal override async ValueTask<bool> TryWriteDurablyAsync(T item, CancellationToken cancellationToken)
    {
        if (_completed) return false;
        if (GetRefusalReason(item) is { } refusal)
        {
            Drop(refusal);
            return false;
        }
        await OpenedAsync(cancellationToken).ConfigureAwait(false);
        var done = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        return Accept(item, done, refuseChecked: true) == Accepted.Taken && await done.Task.WaitAsync(cancellationToken).ConfigureAwait(false);
    }

    private enum Accepted
    {
        Taken,
        Dropped,
        Full,
        Closed,
    }

    private Accepted Accept(T item, TaskCompletionSource<bool>? done, bool refuseChecked = false)
    {
        if (_completed) return Accepted.Closed;
        if (!refuseChecked && GetRefusalReason(item) is { } refusal)
        {
            Drop(refusal);
            return Accepted.Full;                                                       // the caller learns it was not kept
        }
        bool dropped = false;
        lock (_gate)
        {
            if (_disposed || _completed) return Accepted.Closed;
            if (IsFullLocked())
            {
                switch (_policy.Overflow)
                {
                    case BoundedChannelFullMode.DropWrite:
                        _shed.Add(ZabbixStoreDropReason.DropWrite);
                        done?.TrySetResult(false);
                        dropped = true;
                        break;
                    case BoundedChannelFullMode.DropOldest:
                        if (!ShedOldestLocked())
                        {
                            _shed.Add(ZabbixStoreDropReason.DropOldest);                // every held record is in flight: the incoming goes
                            done?.TrySetResult(false);
                            dropped = true;
                        }
                        break;
                    default:
                        return Accepted.Full;                                           // Wait: WriteAsync waits for room
                }
            }
            if (!dropped)
            {
                _pending.Add(new Pending(item, Services.Time.GetUtcNow(), done));
                _held++;
            }
        }
        ReportShed();
        if (dropped) return Accepted.Dropped;
        Kick();
        return Accepted.Taken;
    }

    /// <inheritdoc/>
    public override async ValueTask<bool> WaitToWriteAsync(CancellationToken cancellationToken = default)
    {
        if (_completed) return false;
        if (_policy.Overflow != BoundedChannelFullMode.Wait) return true;
        await OpenedAsync(cancellationToken).ConfigureAwait(false);
        return await WaitForRoomAsync(cancellationToken).ConfigureAwait(false);
    }

    // One signal wakes one waiting writer. A writer that finds room passes the signal on, so room made for several (a
    // cumulative acknowledgement) resumes every writer it has room for, not only the first; one that finds none keeps
    // waiting without passing it on, so nothing spins. A disposed store completes the signal: the wait ends, refused.
    private async ValueTask<bool> WaitForRoomAsync(CancellationToken cancellationToken)
    {
        while (!_completed && IsFull())
        {
            if (!await _room.Reader.WaitToReadAsync(cancellationToken).ConfigureAwait(false)) break;
            _room.Reader.TryRead(out _);
        }
        if (_completed) return false;
        Signal(_room);
        return true;
    }

    // The backend's count is known once it is open; a write that must judge "full" against it waits for that.
    private async ValueTask OpenedAsync(CancellationToken cancellationToken)
    {
        Task opened;
        lock (_gate)
        {
            if (_ready) return;
            opened = _opened.Task;
        }
        Kick();
        await opened.WaitAsync(cancellationToken).ConfigureAwait(false);
    }

    private bool IsFull()
    {
        lock (_gate) return IsFullLocked();
    }

    private bool IsFullLocked() => _policy.MaxItems is int max && _held >= max;

    // DropOldest: the oldest record not yet leased leaves — from memory, from the backend (shed when it is loaded), or a
    // write not yet committed. False when every held record is leased.
    private bool ShedOldestLocked()
    {
        if (_head < _ahead.Count)
        {
            _doomed.Add(_ahead[_head].Sequence);
            _head++;
        }
        else if (BackendOnlyLocked() > 0)
        {
            _shedFromBackend++;
        }
        else if (PendingLocked > 0)
        {
            // The oldest write leaves by moving the start, not by shifting the list: a shed is O(1), and the list is
            // compacted once half of it is shed.
            _pending[_pendingFrom].Done?.TrySetResult(false);
            _pending[_pendingFrom++] = default;
            if (_pendingFrom == _pending.Count || _pendingFrom > _pending.Count / 2)
            {
                _pending.RemoveRange(0, _pendingFrom);
                _pendingFrom = 0;
            }
        }
        else
        {
            return false;
        }
        _held--;
        _shed.Add(ZabbixStoreDropReason.DropOldest);
        return true;
    }

    private int PendingLocked => _pending.Count - _pendingFrom;

    // Records committed and held that are neither in memory nor already shed: only the backend has them.
    private int BackendOnlyLocked() => _held - PendingLocked - _committing - (_ahead.Count - _head) - _leased.Count - _shedFromBackend;

    /// <inheritdoc/>
    public override bool TryComplete(Exception? error = null)
    {
        lock (_gate)
        {
            if (_completed) return false;
            _error = error;
            _completed = true;
        }
        Signal(_wake);
        Signal(_room);
        Kick();
        return true;
    }

    // ----- the acknowledged face ---------------------------------------------------------------------------------

    /// <inheritdoc/>
    public override bool TryLease(out ZabbixStoreEntry<T> entry)
    {
        bool leased;
        lock (_gate)
        {
            leased = TryNextLocked(out entry);
            if (leased)
            {
                _head++;
                _leased.Add(entry);
            }
        }
        ReportShed();
        return leased;
    }

    /// <inheritdoc/>
    public override bool TryPeek(out ZabbixStoreEntry<T> entry)
    {
        bool found;
        lock (_gate) found = TryNextLocked(out entry);
        ReportShed();
        return found;
    }

    /// <inheritdoc/>
    public override async ValueTask<bool> WaitToLeaseAsync(CancellationToken cancellationToken = default)
    {
        while (true)
        {
            Exception? fault;
            bool ready = false, ended = false;
            lock (_gate)
            {
                if (_disposed) return false;                                            // disposed under the wait: it ends, as a completed store's does
                fault = _fault;
                _fault = null;
                if (fault is null)
                {
                    if (TryNextLocked(out _)) ready = true;
                    else if (_completed && PendingLocked == 0 && _committing == 0 && _tailInMemory && _shedFromBackend == 0)
                    {
                        if (_error is null) ended = true;
                        else fault = _error;
                    }
                }
            }
            ReportShed();
            if (ready) return true;
            if (ended) return false;
            if (fault is not null) ExceptionDispatchInfo.Throw(fault);
            if (await _wake.Reader.WaitToReadAsync(cancellationToken).ConfigureAwait(false)) _wake.Reader.TryRead(out _);
        }
    }

    /// <inheritdoc/>
    public override void Acknowledge(long sequence)
    {
        int count = 0;
        lock (_gate)
        {
            if (_disposed) return;                                                      // kept: the next process leases it again
            while (count < _leased.Count && _leased[count].Sequence <= sequence) count++;
            if (count == 0) return;
            _removeThrough = Math.Max(_removeThrough, _leased[count - 1].Sequence);
            _leased.RemoveRange(0, count);
            _held -= count;
        }
        Acknowledged(count);
        Signal(_room);
        Kick();
    }

    /// <inheritdoc/>
    public override void Release(long sequence)
    {
        lock (_gate)
        {
            int cut = _leased.Count;
            while (cut > 0 && _leased[cut - 1].Sequence > sequence) cut--;
            if (cut == _leased.Count) return;                                           // nothing leased above it
            // Every leased record is below every one in _ahead, so what goes back goes in front of it, in order.
            int back = _leased.Count - cut;
            if (_head >= back)
            {
                _head -= back;
                for (int i = 0; i < back; i++) _ahead[_head + i] = _leased[cut + i];
            }
            else
            {
                _ahead.RemoveRange(0, _head);
                _ahead.InsertRange(0, _leased.GetRange(cut, back));
                _head = 0;
            }
            _leased.RemoveRange(cut, back);
        }
        Signal(_wake);                                                    // a waiter in WaitToLeaseAsync sees it again
    }

    // Under the gate: the next record in memory the policy lets out; expired and discarded ones are dropped (counted) and
    // left for the loop to remove from the backend. When memory has nothing and the backend may, the loop is asked to load.
    private bool TryNextLocked(out ZabbixStoreEntry<T> entry)
    {
        if (!_disposed)
        {
            while (_head < _ahead.Count)
            {
                entry = _ahead[_head];
                if (GetExpiryReason(entry) is not { } reason) return true;
                _head++;
                _held--;
                _doomed.Add(entry.Sequence);
                _shed.Add(reason);
                Signal(_room);
            }
            if (_head > 0)
            {
                _ahead.Clear();
                _head = 0;
            }
            if (!_tailInMemory || !_ready)
            {
                _loadWanted = true;
                Kick();
            }
        }
        entry = default;
        return false;
    }

    // ----- the loop: the one place the backend is called ------------------------------------------------------------

    // The loop starts at the first signal, not in the constructor: a derived constructor has finished before the loop calls
    // its OpenAsync. It yields at once, so no backend operation runs on the thread that kicked it.
    private void Kick()
    {
        if (Volatile.Read(ref _loop) is null)
        {
            lock (_gate) _loop ??= LoopAsync();                                         // returns at its first yield: cheap under the gate
        }
        Signal(_work);
    }

    // Counts the records dropped under the gate, after it: a drop logs and runs the meter's listeners.
    private void ReportShed()
    {
        ZabbixStoreDropReason[] reasons;
        lock (_gate)
        {
            if (_shed.Count == 0) return;
            reasons = [.. _shed];
            _shed.Clear();
        }
        foreach (ZabbixStoreDropReason reason in reasons) Drop(reason);
    }

    // A coalescing signal (capacity one, DropWrite): a full channel is already signalled and still answers true; false
    // means completed, the store disposed, when there is nobody left to wake.
    private static void Signal(Channel<bool> channel) => _ = channel.Writer.TryWrite(true);

    private async Task LoopAsync()
    {
        await Task.Yield();                                                             // off the kicking thread before any backend call
        while (await _work.Reader.WaitToReadAsync().ConfigureAwait(false))
        {
            _work.Reader.TryRead(out _);
            lock (_gate)
            {
                // A store disposed with nothing to commit or remove does not open its backend for the last turn.
                if (_disposed && PendingLocked == 0 && _removeThrough <= _removedThrough && _doomed.Count == 0)
                {
                    foreach (TaskCompletionSource turn in _turns) turn.TrySetResult();
                    _turns.Clear();
                    continue;
                }
            }
            TaskCompletionSource[] turns;
            lock (_gate)
            {
                turns = [.. _turns];
                _turns.Clear();
            }
            bool more = false;
            if (await OpenStepAsync().ConfigureAwait(false))
            {
                more = await CommitStepAsync().ConfigureAwait(false);
                await RemoveStepAsync().ConfigureAwait(false);
                if (!_disposed) await LoadStepAsync().ConfigureAwait(false);
                Signal(_wake);
            }
            if (more)
            {
                lock (_gate) _turns.AddRange(turns);                                     // not done yet: told after the next turn
                Kick();
            }
            else
            {
                foreach (TaskCompletionSource turn in turns) turn.TrySetResult();
            }
        }
        lock (_gate)
        {
            foreach (TaskCompletionSource turn in _turns) turn.TrySetResult();
            _turns.Clear();
        }
        // Closed: what was written and never committed cannot be any more.
        Pending[] left;
        lock (_gate)
        {
            left = [.. _pending.Skip(_pendingFrom)];
            _pending.Clear();
            _pendingFrom = 0;
            _held -= left.Length;
        }
        foreach (Pending pending in left) pending.Done?.TrySetException(new ObjectDisposedException(GetType().Name));
    }

    private async ValueTask<bool> OpenStepAsync()
    {
        lock (_gate) if (_ready) return true;
        int held;
        try
        {
            held = await OpenAsync(CancellationToken.None).ConfigureAwait(false);
        }
        catch (Exception failure) when (failure is not OutOfMemoryException)
        {
            StoreLog.StoragePumpFaulted(Logger, Name, failure);
            Pending[] failed;
            TaskCompletionSource opened;
            lock (_gate)
            {
                failed = [.. _pending.Skip(_pendingFrom)];
                _pending.Clear();
                _pendingFrom = 0;
                _held -= failed.Length;
                _fault = failure;
                opened = _opened;
                _opened = NewSignal();                                                  // the next use tries again
            }
            Fail(failed, failure);
            opened.TrySetException(failure);
            Signal(_wake);
            return false;
        }
        TaskCompletionSource ready;
        lock (_gate)
        {
            _ready = true;
            _held += held;
            _tailInMemory = held == 0;
            ready = _opened;
            // Writes accepted before the backend's count was known may have filled it past the cap.
            if (_policy.MaxItems is int max)
            {
                while (_held > max && _policy.Overflow == BoundedChannelFullMode.DropOldest && ShedOldestLocked()) { }
                while (_held > max && _policy.Overflow == BoundedChannelFullMode.DropWrite && PendingLocked > 0)
                {
                    _pending[^1].Done?.TrySetResult(false);
                    _pending.RemoveAt(_pending.Count - 1);
                    _held--;
                    _shed.Add(ZabbixStoreDropReason.DropWrite);
                }
            }
        }
        ReportShed();
        ready.TrySetResult();
        return true;
    }

    // Commits what was written since the last commit, oldest first. Answers whether more is waiting.
    private async ValueTask<bool> CommitStepAsync()
    {
        Pending[] batch;
        lock (_gate)
        {
            if (PendingLocked == 0) return false;
            batch = [.. _pending.Skip(_pendingFrom).Take(MaxBatch)];
            _pending.RemoveRange(0, _pendingFrom + batch.Length);
            _pendingFrom = 0;
            _committing = batch.Length;
        }
        try
        {
            IReadOnlyList<long> sequences;
            try
            {
                sequences = await AppendAsync([.. batch.Select(pending => new ZabbixStoreEntry<T>(0, pending.Stored, pending.Item))], CancellationToken.None).ConfigureAwait(false);
            }
            catch (Exception failure) when (failure is not OutOfMemoryException)
            {
                // All or nothing: find the records the backend cannot hold by committing one at a time.
                StoreLog.StoragePumpFaulted(Logger, Name, failure);
                await CommitOneByOneAsync(batch).ConfigureAwait(false);
                return HasPending();
            }
            if (sequences.Count != batch.Length) throw new InvalidOperationException($"The backend answered {sequences.Count} sequence(s) for {batch.Length} record(s).");
            Committed(batch, sequences);
        }
        finally
        {
            lock (_gate) _committing = 0;
        }
        return HasPending();
    }

    // Two failures in a row read as the backend being down rather than two records it cannot hold: the rest of the batch
    // fails with the second, instead of one round trip (and one connection timeout) per record.
    private async ValueTask CommitOneByOneAsync(Pending[] batch)
    {
        int failedInARow = 0;
        for (int i = 0; i < batch.Length; i++)
        {
            Pending pending = batch[i];
            try
            {
                IReadOnlyList<long> sequence = await AppendAsync([new ZabbixStoreEntry<T>(0, pending.Stored, pending.Item)], CancellationToken.None).ConfigureAwait(false);
                Committed([pending], sequence);
                failedInARow = 0;
            }
            catch (Exception failure) when (failure is not OutOfMemoryException)
            {
                Pending[] failed = ++failedInARow < 2 || batch.Length == 1 ? [pending] : batch[i..];
                lock (_gate) _held -= failed.Length;
                Fail(failed, failure);
                if (failed.Length > 1) return;
            }
        }
    }

    private void Committed(Pending[] batch, IReadOnlyList<long> sequences)
    {
        lock (_gate)
        {
            for (int i = 0; i < batch.Length; i++)
            {
                long sequence = sequences[i];
                if (_tailInMemory && _ahead.Count - _head < MemoryTail)
                {
                    _ahead.Add(new ZabbixStoreEntry<T>(sequence, batch[i].Stored, batch[i].Item));
                    _loadedThrough = Math.Max(_loadedThrough, sequence);
                }
                else
                {
                    _tailInMemory = false;                                              // the rest is loaded when the cursor gets there
                }
            }
        }
        foreach (Pending pending in batch) pending.Done?.TrySetResult(true);
        Signal(_wake);
    }

    // A record that was accepted and could not be committed: the writer that waits is told; one that did not is counted.
    private void Fail(Pending[] failed, Exception failure)
    {
        foreach (Pending pending in failed)
        {
            if (pending.Done is { } done) done.TrySetException(failure);
            else Drop(ZabbixStoreDropReason.Faulted);
        }
        Signal(_room);
    }

    private bool HasPending()
    {
        lock (_gate) return PendingLocked > 0;
    }

    // Removes what was acknowledged, expired, discarded or shed. A failure is logged and retried at the next turn.
    private async ValueTask RemoveStepAsync()
    {
        long through;
        long[] doomed;
        lock (_gate)
        {
            through = _removeThrough;
            doomed = [.. _doomed.Where(sequence => sequence > _removeThrough)];
            _doomed.Clear();
        }
        try
        {
            if (through > _removedThrough)
            {
                await RemoveAsync(long.MinValue, through, CancellationToken.None).ConfigureAwait(false);
                _removedThrough = through;
            }
            for (int i = 0; i < doomed.Length; i++)
            {
                await RemoveAsync(doomed[i], doomed[i], CancellationToken.None).ConfigureAwait(false);
                doomed[i] = 0;
            }
        }
        catch (Exception failure) when (failure is not OutOfMemoryException)
        {
            StoreLog.StoragePumpFaulted(Logger, Name, failure);
            lock (_gate) _doomed.AddRange(doomed.Where(sequence => sequence != 0));
        }
    }

    // Loads ahead of the cursor when the consumer has run out and the backend may hold more; the oldest records DropOldest
    // shed while only the backend had them are removed as they arrive.
    private async ValueTask LoadStepAsync()
    {
        long after;
        lock (_gate)
        {
            if (_tailInMemory || (!_loadWanted && _shedFromBackend == 0) || (_head < _ahead.Count && _shedFromBackend == 0)) return;
            after = _loadedThrough;
        }
        IReadOnlyList<ZabbixStoreEntry<T>> loaded;
        try
        {
            loaded = await LoadAfterAsync(after, ReadAhead, CancellationToken.None).ConfigureAwait(false);
        }
        catch (Exception failure) when (failure is not OutOfMemoryException)
        {
            StoreLog.StoragePumpFaulted(Logger, Name, failure);
            lock (_gate)
            {
                _fault = failure;
                _loadWanted = false;
            }
            return;
        }
        bool again;
        lock (_gate)
        {
            foreach (ZabbixStoreEntry<T> entry in loaded)
            {
                if (entry.Sequence <= _loadedThrough) continue;
                _loadedThrough = entry.Sequence;
                if (entry.Sequence <= _removeThrough) continue;                         // acknowledged before it was loaded: skipped, not offered again
                if (_shedFromBackend > 0)
                {
                    _shedFromBackend--;
                    _doomed.Add(entry.Sequence);
                    continue;
                }
                if (_head > 0 && _head == _ahead.Count)
                {
                    _ahead.Clear();
                    _head = 0;
                }
                _ahead.Add(entry);
            }
            if (loaded.Count < ReadAhead)
            {
                _tailInMemory = true;
                _shedFromBackend = 0;                                                   // nothing older is left to shed
            }
            _loadWanted = _head >= _ahead.Count && !_tailInMemory;
            again = _loadWanted || (_shedFromBackend > 0 && !_tailInMemory) || _doomed.Count > 0;
        }
        if (again) Kick();
    }

    // ----- the stream --------------------------------------------------------------------------------------------

    /// <summary>
    /// Leases and at once acknowledges stored records, oldest first, for the one consumer: a record leaves the store as it
    /// is read. Waits for more while the writer face is open, and ends when it is complete and drained, throwing the
    /// completion error then, if the writer completed with one. A backend fault is logged (event 2000) and thrown to the consumer.
    /// </summary>
    /// <param name="cancellationToken">Stops the stream.</param>
    /// <returns>The records.</returns>
    public override IAsyncEnumerator<T> GetAsyncEnumerator(CancellationToken cancellationToken = default) => ReadAsync(cancellationToken);

    private async IAsyncEnumerator<T> ReadAsync(CancellationToken cancellationToken)
    {
        while (await WaitToLeaseAsync(cancellationToken).ConfigureAwait(false))
        {
            while (TryLease(out ZabbixStoreEntry<T> entry))
            {
                Acknowledge(entry.Sequence);
                yield return entry.Record;
            }
        }
    }

    /// <summary>
    /// Completes the writer face, lets the loop commit what was written and remove what was acknowledged, and releases the
    /// backend. A pending <see cref="WaitToLeaseAsync"/> or stream read ends (as at completion) and a writer waiting for
    /// room is refused; a leased record that was not acknowledged stays in the backend, so the next process leases it again
    /// with the same sequence.
    /// </summary>
    /// <returns>When released.</returns>
    public override async ValueTask DisposeAsync()
    {
        lock (_gate)
        {
            if (_disposed) return;
            _completed = true;
            _disposed = true;
        }
        _ = _wake.Writer.TryComplete();                                                    // false: completed by an earlier disposal
        _ = _room.Writer.TryComplete();
        Kick();                                                                         // a last turn for what is pending
        _ = _work.Writer.TryComplete();
        if (Volatile.Read(ref _loop) is { } loop) await loop.ConfigureAwait(ConfigureAwaitOptions.SuppressThrowing);
        await DisposeStoreAsync().ConfigureAwait(false);
        await base.DisposeAsync().ConfigureAwait(false);
        GC.SuppressFinalize(this);
    }

    /// <summary>
    /// Waits until the store's loop has run one whole turn after this call: what was written before it is committed (or
    /// failed), what was acknowledged before it is removed from the backend (or failed and kept for a retry).
    /// </summary>
    /// <returns>When the turn has ended.</returns>
    internal Task FlushAsync()
    {
        TaskCompletionSource turn = NewSignal();
        lock (_gate)
        {
            if (_disposed && Volatile.Read(ref _loop) is null or { IsCompleted: true }) return Task.CompletedTask;
            _turns.Add(turn);
        }
        Kick();
        return turn.Task;
    }

    private static TaskCompletionSource NewSignal() => new(TaskCreationOptions.RunContinuationsAsynchronously);

    private readonly record struct Pending(T Item, DateTimeOffset Stored, TaskCompletionSource<bool>? Done);
}
