// This Source Code Form is subject to the terms of the Mozilla Public
// License, v. 2.0. Copyright (c) 2026 schroeder247. SPDX-License-Identifier: MPL-2.0
using System.Runtime.ExceptionServices;
using System.Threading.Channels;
using Community.Zabbix.Storage;
using Microsoft.Extensions.Logging;
using Polly;
using Polly.Retry;

namespace Community.Zabbix.Hosting;

/// <summary>
/// Reads a source (a store, a watcher, any <see cref="IAsyncEnumerable{T}"/>), cuts it into batches by size or by the
/// flush cadence, and delivers each batch in order, so the source is paced by the wire.
/// </summary>
/// <remarks>
/// <para>A delivery answers a batch with a <see cref="ZabbixSendOutcome"/>. <see cref="ZabbixSendOutcome.Delivered"/> and
/// <see cref="ZabbixSendOutcome.Rejected"/> end a batch; <see cref="ZabbixSendOutcome.Retried"/>,
/// <see cref="ZabbixSendOutcome.Held"/> and a handler that throws keep it and offer it again after an exponential backoff with
/// jitter (a throw is also logged at Error, event 3100). <see cref="ZabbixSendOutcome.Returned"/> and
/// <see cref="ZabbixSendOutcome.Abandoned"/> are what a stopping loop reports, so a delivery that answers with one is the
/// handler's defect, as a throw is. A cancellation the handler throws is the handler's failure (a client's own timeout), not
/// a stop: only the loop's own token stops it.</para>
/// <para>
/// A store is read through its acknowledged face: a batch is leased, and acknowledged once it has its final answer, so a
/// durable store keeps a batch in flight until the destination took it. Any other source is read as a stream.
/// </para>
/// <para>
/// Cancelling the token stops the loop waiting for values, not a delivery. <see cref="ZabbixSendOptions.DrainTimeout"/>
/// then bounds the drain; what is still undelivered is released back to a store, handed back to a source that is a
/// <see cref="ChannelWriter{T}"/> still taking writes, else counted and logged as abandoned (event 3106). Nothing taken is
/// dropped silently.
/// </para>
/// </remarks>
public static class ZabbixSendLoop
{
    /// <summary>Runs until <paramref name="source"/> ends, or until <paramref name="cancellationToken"/> stops it and the drain is over, with the default options.</summary>
    /// <typeparam name="T">The record.</typeparam>
    /// <param name="source">The values.</param>
    /// <param name="deliver">The destination.</param>
    /// <param name="cancellationToken">Stops waiting for values; the drain follows.</param>
    /// <returns>How many values were delivered or rejected: every value that reached a final answer.</returns>
    public static Task<long> RunAsync<T>(IAsyncEnumerable<T> source, Func<IReadOnlyList<T>, CancellationToken, ValueTask<ZabbixSendOutcome>> deliver, CancellationToken cancellationToken = default)
        => RunAsync(source, deliver, new ZabbixSendLoopOptions(), cancellationToken);

    /// <summary>Runs until <paramref name="source"/> ends, or until <paramref name="cancellationToken"/> stops it and the drain is over.</summary>
    /// <typeparam name="T">The record.</typeparam>
    /// <param name="source">The values.</param>
    /// <param name="deliver">The destination.</param>
    /// <param name="options">Batching and retry, the clock, the logger and the progress the run reports to.</param>
    /// <param name="cancellationToken">Stops waiting for values; the drain follows.</param>
    /// <returns>How many values were delivered or rejected: every value that reached a final answer.</returns>
    /// <remarks>A source that faults ends the run with its exception, rethrown once everything it yielded before the fault has been delivered.</remarks>
    public static Task<long> RunAsync<T>(IAsyncEnumerable<T> source, Func<IReadOnlyList<T>, CancellationToken, ValueTask<ZabbixSendOutcome>> deliver, ZabbixSendLoopOptions options, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(source);
        ArgumentNullException.ThrowIfNull(deliver);
        ArgumentNullException.ThrowIfNull(options);
        ZabbixSendOptions send = options.Send;
        var run = new Run<T>(deliver, send, options.Time, options.Logger, options.Progress);
        return RunAsync(new Batcher<T>(source, send.BatchSize, send.FlushInterval, options.Time, run), run, send.DrainTimeout, options.Time, cancellationToken, CancellationToken.None);
    }

    /// <summary>Runs over <paramref name="store"/>'s acknowledged face with the default options.</summary>
    /// <typeparam name="T">The record.</typeparam>
    /// <param name="store">The store.</param>
    /// <param name="deliver">The destination.</param>
    /// <param name="cancellationToken">Stops waiting for values; the drain follows.</param>
    /// <returns>How many values were delivered or rejected: every value that reached a final answer.</returns>
    public static Task<long> RunAsync<T>(ZabbixStore<T> store, Func<IReadOnlyList<T>, CancellationToken, ValueTask<ZabbixSendOutcome>> deliver, CancellationToken cancellationToken = default)
        => RunAsync(store, deliver, new ZabbixSendLoopOptions(), cancellationToken, CancellationToken.None);

    /// <summary>
    /// Runs over <paramref name="store"/>'s acknowledged face until the store is complete and empty, or until
    /// <paramref name="cancellationToken"/> stops it and the drain is over. Each batch is leased and acknowledged once it is
    /// delivered or rejected; a batch the drain window closes on is released back to the store, so a durable store leases
    /// it again, with the same sequences, in the next process.
    /// </summary>
    /// <typeparam name="T">The record.</typeparam>
    /// <param name="store">The store.</param>
    /// <param name="deliver">The destination.</param>
    /// <param name="options">Batching and retry, the clock, the logger and the progress the run reports to.</param>
    /// <param name="cancellationToken">Stops waiting for values; the drain follows.</param>
    /// <returns>How many values were delivered or rejected: every value that reached a final answer.</returns>
    /// <remarks>A lease wait that faults (the store completed with an error, a backend that cannot load) ends the run with its exception, once what was leased before it has been delivered.</remarks>
    public static Task<long> RunAsync<T>(ZabbixStore<T> store, Func<IReadOnlyList<T>, CancellationToken, ValueTask<ZabbixSendOutcome>> deliver, ZabbixSendLoopOptions options, CancellationToken cancellationToken = default)
        => RunAsync(store, deliver, options, cancellationToken, CancellationToken.None);

    /// <summary>The store form a host stops with: <paramref name="abandoning"/> closes the drain window at once (a disposal, or a host that gave up waiting).</summary>
    internal static Task<long> RunAsync<T>(ZabbixStore<T> store, Func<IReadOnlyList<T>, CancellationToken, ValueTask<ZabbixSendOutcome>> deliver, ZabbixSendLoopOptions options, CancellationToken cancellationToken, CancellationToken abandoning)
    {
        ArgumentNullException.ThrowIfNull(store);
        ArgumentNullException.ThrowIfNull(deliver);
        ArgumentNullException.ThrowIfNull(options);
        ZabbixSendOptions send = options.Send;
        var run = new Run<T>(deliver, send, options.Time, options.Logger, options.Progress);
        return RunAsync(new LeaseBatcher<T>(store, send.BatchSize, send.FlushInterval, options.Time, run), run, send.DrainTimeout, options.Time, cancellationToken, abandoning);
    }

    private static async Task<long> RunAsync<T>(IBatches<T> batches, Run<T> run, TimeSpan window, TimeProvider time, CancellationToken cancellationToken, CancellationToken abandoning)
    {
        // Deliveries run on their own token: the stop does not interrupt one, it starts the drain window; abandoning closes it.
        using var delivering = new CancellationTokenSource(Timeout.InfiniteTimeSpan, time);
        using CancellationTokenRegistration stop = cancellationToken.Register(() => delivering.CancelAfter(window));
        using CancellationTokenRegistration abandon = abandoning.Register(delivering.Cancel);

        long taken = 0;
        List<T>? inFlight = null;
        await using System.Runtime.CompilerServices.ConfiguredAsyncDisposable disposing = batches.ConfigureAwait(false);
        try
        {
            while ((inFlight = await batches.NextAsync(cancellationToken).ConfigureAwait(false)) is not null)
            {
                taken += await run.DeliverAsync(inFlight, delivering.Token).ConfigureAwait(false);
                batches.Settled();
                inFlight = null;
            }
            batches.ThrowIfFaulted();
            return taken;
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested || delivering.IsCancellationRequested)
        {
            // Stopped (or abandoned before the stop): the drain below.
        }

        try
        {
            if (inFlight is not null)
            {
                taken += await run.DeliverAsync(inFlight, delivering.Token).ConfigureAwait(false);
                batches.Settled();
                inFlight = null;
            }
            while ((inFlight = await batches.NextReadyAsync(delivering.Token).ConfigureAwait(false)) is not null)
            {
                taken += await run.DeliverAsync(inFlight, delivering.Token).ConfigureAwait(false);
                batches.Settled();
                inFlight = null;
            }
        }
        catch (OperationCanceledException) when (delivering.IsCancellationRequested)
        {
            // The window closed.
        }
        if (inFlight is { Count: > 0 }) batches.Unsettled(inFlight);
        batches.ThrowIfFaulted();
        return taken;
    }

    /// <summary>What the loop reads batches from: a stream, or a store's acknowledged face.</summary>
    internal interface IBatches<T> : IAsyncDisposable
    {
        /// <summary>The next batch: full, or partial when the flush cadence ticks, or the rest at the end; <see langword="null"/> when nothing is left.</summary>
        ValueTask<List<T>?> NextAsync(CancellationToken cancellationToken);

        /// <summary>After a stop: the next batch of what is ready without waiting; <see langword="null"/> when nothing is.</summary>
        ValueTask<List<T>?> NextReadyAsync(CancellationToken window);

        /// <summary>The batch handed out last has its final answer.</summary>
        void Settled();

        /// <summary>The drain window closed on <paramref name="batch"/>, the batch handed out last.</summary>
        void Unsettled(List<T> batch);

        /// <summary>Rethrows the source's fault, once what it yielded before the fault has been handed out.</summary>
        void ThrowIfFaulted();
    }

    /// <summary>How many times a batch is tried when its handler throws something other than an exchange failure.</summary>
    internal const int MaxDefectAttempts = 3;

    /// <summary>One run's retry pipeline and reporting: what the loop does with a batch.</summary>
    internal sealed class Run<T>
    {
        private readonly Func<IReadOnlyList<T>, CancellationToken, ValueTask<ZabbixSendOutcome>> _deliver;
        private readonly TimeProvider _time;
        private readonly ILogger _logger;
        private readonly IProgress<ZabbixSendProgress>? _progress;
        private readonly ResiliencePipeline<ZabbixSendOutcome> _retry;

        // DeliverAsync runs one batch at a time (RunAsync never starts the next before this one has a final answer), so a
        // plain field — not a boxed int in the ResilienceContext's Properties bag, set and read once per batch either way
        // — carries the count between DeliverAsync and OnRetryAsync without allocating.
        private int _batchSize;
        private bool _heldLogged;

        [System.Diagnostics.CodeAnalysis.UnconditionalSuppressMessage(
            "AOT",
            "IL3050:RequiresDynamicCode",
            Justification = "AddRetry keeps every member of its result type for its options' validation, which for an enum takes in the base class's Enum.GetValues(Type). Nothing calls it: the pipeline only compares the result's values.")]
        public Run(Func<IReadOnlyList<T>, CancellationToken, ValueTask<ZabbixSendOutcome>> deliver, ZabbixSendOptions options, TimeProvider time, ILogger logger, IProgress<ZabbixSendProgress>? progress)
        {
            _deliver = deliver;
            _time = time;
            _logger = logger;
            _progress = progress;
            _retry = new ResiliencePipelineBuilder<ZabbixSendOutcome> { TimeProvider = time }
                .AddRetry(new RetryStrategyOptions<ZabbixSendOutcome>
                {
                    // Retry and Held keep the batch; so does a failure of the exchange or a server's refusal, for as long
                    // as it lasts, as upstream's sender retries. Any other throw is the handler's own defect (a value it
                    // cannot serialise, a bug): retrying it forever would send nothing else, so it is retried a few times
                    // and the batch is then rejected (DeliverAsync). Only the delivery token's own cancellation ends the
                    // attempts; a cancellation the handler raised itself (an HttpClient timeout) is a failed exchange.
                    ShouldHandle = static arguments => ValueTask.FromResult(arguments.Outcome.Exception switch
                    {
                        null => arguments.Outcome.Result is ZabbixSendOutcome.Retried or ZabbixSendOutcome.Held,
                        OperationCanceledException when arguments.Context.CancellationToken.IsCancellationRequested => false,
                        OperationCanceledException => true,
                        { } failure when IsTransient(failure) => true,
                        _ => arguments.AttemptNumber + 1 < MaxDefectAttempts,
                    }),
                    MaxRetryAttempts = int.MaxValue,
                    Delay = options.RetryDelay,
                    MaxDelay = options.MaxRetryDelay,
                    BackoffType = DelayBackoffType.Exponential,
                    UseJitter = true,
                    OnRetry = OnRetryAsync,
                })
                .Build();
        }

        // A delivery answers with one of the four verdicts; Returned and Abandoned are what a stopping loop reports, never a
        // destination's answer, so a handler that gives one has a defect, as one that throws does.
        private static ZabbixSendOutcome Verdict(ZabbixSendOutcome outcome)
            => outcome is ZabbixSendOutcome.Delivered or ZabbixSendOutcome.Rejected or ZabbixSendOutcome.Retried or ZabbixSendOutcome.Held
                ? outcome
                : throw new InvalidOperationException($"A delivery answers Delivered, Rejected, Retried or Held; {outcome} is what a stopping send loop reports.");

        /// <summary>A failure that is the destination's, not the handler's: worth retrying for as long as it lasts.</summary>
        internal static bool IsTransient(Exception failure)
            => Protocol.ZabbixFrames.IsExchangeFailure(failure) || failure is ZabbixTrapperRefusedException or ZabbixServerRefusedException or System.Text.Json.JsonException;

        /// <returns>The batch's size, once it has a final answer.</returns>
        /// <exception cref="OperationCanceledException">The delivery token was cancelled: the batch has no answer.</exception>
        public async Task<int> DeliverAsync(List<T> batch, CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();                        // the window closed: no new attempt
            ResilienceContext context = ResilienceContextPool.Shared.Get(cancellationToken);
            _batchSize = batch.Count;
            _heldLogged = false;
            try
            {
                ZabbixSendOutcome outcome;
                try
                {
                    outcome = await _retry.ExecuteAsync(
                        static async (context, state) => Verdict(await state.Deliver(state.Batch, context.CancellationToken).ConfigureAwait(false)),
                        context, (Deliver: _deliver, Batch: (IReadOnlyList<T>)batch)).ConfigureAwait(false);
                }
                catch (Exception defect) when (!(defect is OperationCanceledException && cancellationToken.IsCancellationRequested) && !IsTransient(defect))
                {
                    AgentLog.DeliveryDefect(_logger, batch.Count, MaxDefectAttempts, defect);
                    outcome = ZabbixSendOutcome.Rejected;
                }
                if (outcome == ZabbixSendOutcome.Rejected)
                {
                    AgentLog.DeliveryRejected(_logger, batch.Count);
                    Report(ZabbixSendOutcome.Rejected, batch.Count);
                }
                else
                {
                    Report(ZabbixSendOutcome.Delivered, batch.Count);
                }
                return batch.Count;
            }
            finally
            {
                ResilienceContextPool.Shared.Return(context);
            }
        }

        /// <summary>The window closed on <paramref name="count"/> values a store took back: they stay in it, undelivered.</summary>
        public void Returned(int count)
        {
            AgentLog.DeliveryReturned(_logger, count);
            Report(ZabbixSendOutcome.Returned, count);
        }

        /// <summary>A late fault of a source's reader, released after the stop: logged, since nobody awaits it.</summary>
        public void ReleaseFaulted(Exception failure) => AgentLog.SourceReleaseFaulted(_logger, failure);

        /// <summary>The window closed on <paramref name="batch"/>: back to the source if it takes writes, else lost and counted.</summary>
        public void GiveBack(IAsyncEnumerable<T> source, List<T> batch)
        {
            int returned = 0;
            if (source is ChannelWriter<T> writer)
            {
                foreach (T item in batch)
                {
                    if (!writer.TryWrite(item)) break;
                    returned++;
                }
            }
            if (returned > 0) Returned(returned);
            if (batch.Count > returned) Abandoned(batch.Count - returned);
        }

        /// <summary>The window closed on <paramref name="count"/> values nothing will deliver: lost, and counted.</summary>
        public void Abandoned(int count)
        {
            AgentLog.DeliveryAbandoned(_logger, count);
            Report(ZabbixSendOutcome.Abandoned, count);
        }

        private ValueTask OnRetryAsync(OnRetryArguments<ZabbixSendOutcome> arguments)
        {
            int count = _batchSize;
            if (arguments.Outcome.Exception is { } failure)
            {
                AgentLog.DeliveryFaulted(_logger, count, arguments.AttemptNumber + 1, failure);
                Report(ZabbixSendOutcome.Retried, count);
            }
            else if (arguments.Outcome.Result == ZabbixSendOutcome.Held)
            {
                if (!_heldLogged)
                {
                    AgentLog.DeliveryHeld(_logger, count);                          // once per batch: a pause, not a failure
                    _heldLogged = true;
                }
                Report(ZabbixSendOutcome.Held, count);
            }
            else
            {
                AgentLog.DeliveryRetrying(_logger, count, arguments.AttemptNumber + 1, arguments.RetryDelay.TotalSeconds);
                Report(ZabbixSendOutcome.Retried, count);
            }
            return default;
        }

        private void Report(ZabbixSendOutcome outcome, int count) => _progress?.Report(new ZabbixSendProgress(outcome, count, _time.GetUtcNow()));
    }

    /// <summary>
    /// Batches by count or by cadence over one owned enumerator: the pending <c>MoveNextAsync</c> and the pending tick are
    /// both kept across calls, so nothing is re-issued. At stop the pending call is cancelled and awaited for the drain
    /// window, and a value it yields in that window is taken; the enumerator is disposed only once that call has
    /// completed, never under it (when it outlives the window, the disposal is a continuation of the call). A source that
    /// ignores the cancellation keeps its read past the window, and a value it yields then is not taken by this loop: it
    /// stays the source's. A source fault ends the stream after what came before it.
    /// </summary>
    /// <typeparam name="T">The record.</typeparam>
    internal sealed class Batcher<T> : IBatches<T>
    {
        // The enumerator is owned, not hidden behind await foreach: an async iterator (a channel's ReadAllAsync, any
        // yield source) throws NotSupportedException from DisposeAsync while a MoveNextAsync is pending, and the loop
        // races MoveNextAsync against the flush tick. Chunk is count-only and System.Linq.AsyncEnumerable has no
        // time-bounded buffer, so nothing in the BCL batches by cadence.
        // Not pooled: a handler is free to keep the IReadOnlyList<T> a delivery was given — reusing the backing List<T> once "delivered" would silently
        // rewrite it under a caller still holding it. One `new List<T>(size)` per flush is the batch's own, forever.
        private readonly CancellationTokenSource _reading = new();
        private readonly IAsyncEnumerable<T> _source;
        private readonly Run<T>? _run;
        private readonly IAsyncEnumerator<T> _items;
        private readonly PeriodicTimer _timer;
        private readonly int _size;
        private List<T> _batch;
        private Task<bool>? _move;
        private Task<bool>? _tick;
        private bool _ended;
        private ExceptionDispatchInfo? _fault;

        public Batcher(IAsyncEnumerable<T> source, int size, TimeSpan interval, TimeProvider time, Run<T>? run = null)
        {
            _source = source;
            _run = run;
            _items = source.GetAsyncEnumerator(_reading.Token);
            _timer = new PeriodicTimer(interval, time);
            _size = size;
            _batch = new List<T>(size);
        }

        /// <inheritdoc/>
        public void Settled()
        {
            // A stream's value left the source as it was read: nothing to tell it.
        }

        /// <inheritdoc/>
        public void Unsettled(List<T> batch) => _run?.GiveBack(_source, batch);

        /// <summary>Whether a <c>MoveNextAsync</c> is pending: the loop is waiting for the source (an observable for tests).</summary>
        public bool IsWaiting => _move is { IsCompleted: false };

        /// <summary>
        /// The next batch: full, or partial when the flush cadence ticks, or the rest when the source ends;
        /// <see langword="null"/> when nothing is left. Cancelling stops the wait and keeps the pending read for the drain.
        /// </summary>
        public async ValueTask<List<T>?> NextAsync(CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            while (!_ended)
            {
                if (_move is null)
                {
                    // The fast path: an item already there costs no task and no race with the clock.
                    ValueTask<bool> moving = _items.MoveNextAsync();
                    if (!moving.IsCompleted)
                    {
                        _move = moving.AsTask();
                        continue;
                    }
                    if (moving.IsCompletedSuccessfully) Take(await moving.ConfigureAwait(false));   // completed: no task, no suspension per value
                    else await AcceptAsync(moving.AsTask()).ConfigureAwait(false);
                }
                else
                {
                    _tick ??= _timer.WaitForNextTickAsync(CancellationToken.None).AsTask();
                    Task done = await Task.WhenAny(_move, _tick).WaitAsync(cancellationToken).ConfigureAwait(false);
                    if (done == _tick)
                    {
                        Task<bool> tick = _tick;
                        _tick = null;
                        if (_batch.Count > 0 || !await tick.ConfigureAwait(false)) return Flush();   // a disposed timer ends the wait
                        continue;
                    }
                    Task<bool> move = _move;
                    _move = null;
                    await AcceptAsync(move).ConfigureAwait(false);
                }
                if (_batch.Count >= _size) return Flush();
            }
            return Flush();
        }

        /// <summary>
        /// After a stop: the next batch of what is already taken or ready without waiting, up to the batch size;
        /// <see langword="null"/> when nothing is. A pending read is cancelled and its outcome observed (for at most
        /// <paramref name="window"/>). A read that had already been answered when it was cancelled yields its value (a
        /// channel's wake-up is on its way when the stop comes), and what the source then hands out without waiting is
        /// taken too (the store's last values); the first read after that which would wait ends the reading.
        /// </summary>
        public async ValueTask<List<T>?> NextReadyAsync(CancellationToken window)
        {
            while (!_ended && _batch.Count < _size)
            {
                if (_move is null)
                {
                    ValueTask<bool> moving = _items.MoveNextAsync();
                    if (moving.IsCompletedSuccessfully)
                    {
                        Take(await moving.ConfigureAwait(false));
                        continue;
                    }
                    if (moving.IsCompleted)
                    {
                        await AcceptAsync(moving.AsTask()).ConfigureAwait(false);
                        continue;
                    }
                    _move = moving.AsTask();
                }
                if (_reading.IsCancellationRequested && !_move.IsCompleted)
                {
                    _ended = true;                                                  // the reading was cancelled and this read would wait: it stays the source's
                    break;
                }
                if (!_move.IsCompleted) await _reading.CancelAsync().ConfigureAwait(false);   // nothing is ready: stop reading
                // The read's own outcome (a value, the end, the cancellation the loop asked for, a fault) is AcceptAsync's.
                await ((Task)_move).WaitAsync(window).ConfigureAwait(ConfigureAwaitOptions.SuppressThrowing);
                if (!_move.IsCompleted)
                {
                    _ended = true;                                                  // the window closed: the source ignores cancellation and keeps its read
                    break;
                }
                Task<bool> move = _move;
                _move = null;
                await AcceptAsync(move).ConfigureAwait(false);
            }
            return Flush();
        }

        /// <summary>Rethrows the source's fault, once what it yielded before the fault has been handed out.</summary>
        public void ThrowIfFaulted()
        {
            if (_batch.Count == 0) _fault?.Throw();
        }

        public async ValueTask DisposeAsync()
        {
            _timer.Dispose();
            if (_move is { IsCompleted: false } pending)
            {
                // A read the source never finished: cancel it, and release the enumerator and the token source once it
                // does, never under it. The stop already gave the read its drain window (NextReadyAsync), so a second
                // bound here would only lengthen the stop; a source that ignores cancellation finishes in its own time.
                await _reading.CancelAsync().ConfigureAwait(false);
                Released = ReleaseAfterAsync(pending);
                return;
            }
            await ReleaseAsync().ConfigureAwait(false);
        }

        /// <summary>When the enumerator and the token source were released (an observable for tests).</summary>
        public Task Released { get; private set; } = Task.CompletedTask;

        private async Task ReleaseAfterAsync(Task<bool> pending)
        {
            await ((Task)pending).ConfigureAwait(ConfigureAwaitOptions.SuppressThrowing);   // its outcome is no longer this loop's
            try
            {
                await ReleaseAsync().ConfigureAwait(false);
            }
            catch (Exception failure) when (failure is not OutOfMemoryException)
            {
                _run?.ReleaseFaulted(failure);                                       // nobody awaits this release: logged here
            }
        }

        private async ValueTask ReleaseAsync()
        {
            try
            {
                await _items.DisposeAsync().ConfigureAwait(false);
            }
            finally
            {
                _reading.Dispose();
            }
        }

        /// <summary>A <c>MoveNextAsync</c> that completed: a value, or the end.</summary>
        private void Take(bool moved)
        {
            if (moved) _batch.Add(_items.Current);
            else _ended = true;
        }

        /// <summary>Takes the outcome of one <c>MoveNextAsync</c>: a value, the end, or a fault that ends the stream.</summary>
        private async Task AcceptAsync(Task<bool> move)
        {
            try
            {
                Take(await move.ConfigureAwait(false));
            }
            catch (OperationCanceledException) when (_reading.IsCancellationRequested)
            {
                _ended = true;                                                      // the read the loop cancelled
            }
            catch (Exception failure) when (!(failure is OperationCanceledException && _reading.IsCancellationRequested))
            {
                _fault = ExceptionDispatchInfo.Capture(failure);                    // rethrown after what came before it
                _ended = true;
            }
        }

        private List<T>? Flush()
        {
            if (_batch.Count == 0) return null;
            List<T> batch = _batch;
            _batch = new List<T>(_size);
            return batch;
        }
    }
    /// <summary>
    /// Batches a store's acknowledged face by count or by cadence: records are leased, the batch handed out is acknowledged
    /// up to its last sequence once it has its final answer, and released back to the store when the drain window closes on
    /// it. A pending lease wait removes nothing, so cancelling one loses nothing. After the stop, what is ready is leased
    /// without waiting; a completed store is waited on for the rest (a durable store's last commits) within the window.
    /// </summary>
    /// <typeparam name="T">The record.</typeparam>
    private sealed class LeaseBatcher<T> : IBatches<T>
    {
        private readonly ZabbixStore<T> _store;
        private readonly Run<T> _run;
        private readonly CancellationTokenSource _reading = new();
        private readonly PeriodicTimer _timer;
        private readonly int _size;
        private List<T> _batch;
        private long _first;
        private long _last;
        private long _outFirst;
        private long _outLast;
        private Task<bool>? _wait;
        private Task<bool>? _tick;
        private bool _ended;
        private ExceptionDispatchInfo? _fault;

        public LeaseBatcher(ZabbixStore<T> store, int size, TimeSpan interval, TimeProvider time, Run<T> run)
        {
            _store = store;
            _run = run;
            _timer = new PeriodicTimer(interval, time);
            _size = size;
            _batch = new List<T>(size);
        }

        public async ValueTask<List<T>?> NextAsync(CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            while (!_ended)
            {
                if (_wait is null)
                {
                    LeaseReady();
                    if (_batch.Count >= _size) return Flush();
                    ValueTask<bool> waiting = _store.WaitToLeaseAsync(_reading.Token);
                    if (waiting.IsCompletedSuccessfully)
                    {
                        if (!await waiting.ConfigureAwait(false)) _ended = true;    // complete, and every record leased
                        continue;
                    }
                    _wait = waiting.AsTask();
                    if (_wait.IsCompleted) continue;                                // faulted at once: taken below
                }
                if (!_wait.IsCompleted)
                {
                    _tick ??= _timer.WaitForNextTickAsync(CancellationToken.None).AsTask();
                    Task done = await Task.WhenAny(_wait, _tick).WaitAsync(cancellationToken).ConfigureAwait(false);
                    if (done == _tick)
                    {
                        Task<bool> tick = _tick;
                        _tick = null;
                        if (_batch.Count > 0 || !await tick.ConfigureAwait(false)) return Flush();   // a disposed timer ends the wait
                        continue;
                    }
                }
                Task<bool> wait = _wait;
                _wait = null;
                await AcceptAsync(wait).ConfigureAwait(false);
            }
            return Flush();
        }

        public async ValueTask<List<T>?> NextReadyAsync(CancellationToken window)
        {
            if (_wait is { IsCompleted: false })
            {
                await _reading.CancelAsync().ConfigureAwait(false);                 // a lease wait holds nothing: let it go
                _wait = null;
            }
            while (!_ended && _batch.Count < _size)
            {
                LeaseReady();
                if (_batch.Count >= _size || !_store.IsCompleted) break;           // an open store keeps what is not ready
                try
                {
                    if (!await _store.WaitToLeaseAsync(window).ConfigureAwait(false)) _ended = true;
                }
                catch (OperationCanceledException) when (window.IsCancellationRequested)
                {
                    break;                                                          // the window closed: the rest stays in the store
                }
                catch (Exception failure) when (!(failure is OperationCanceledException && window.IsCancellationRequested))
                {
                    _fault = ExceptionDispatchInfo.Capture(failure);
                    _ended = true;
                }
            }
            return Flush();
        }

        public void Settled() => _store.Acknowledge(_outLast);

        public void Unsettled(List<T> batch)
        {
            // Released either way, so a later reader of the store leases it again. A durable store keeps it for the next
            // process, and an open store for its next reader; a completed in-memory store is read by nobody again and goes
            // with its disposal, so there it is counted lost.
            _store.Release(_outFirst - 1);
            if (_store.IsDurable || !_store.IsCompleted) _run.Returned(batch.Count);
            else _run.Abandoned(batch.Count);
        }

        public void ThrowIfFaulted()
        {
            if (_batch.Count == 0) _fault?.Throw();
        }

        public async ValueTask DisposeAsync()
        {
            _timer.Dispose();
            await _reading.CancelAsync().ConfigureAwait(false);
            if (_wait is { } pending) await ((Task)pending).ConfigureAwait(ConfigureAwaitOptions.SuppressThrowing);   // a store's wait ends on its token
            if (_batch.Count > 0) _store.Release(_first - 1);                       // leased, never handed out: the store has them again
            _reading.Dispose();
        }

        private void LeaseReady()
        {
            while (_batch.Count < _size && _store.TryLease(out ZabbixStoreEntry<T> entry))
            {
                if (_batch.Count == 0) _first = entry.Sequence;
                _last = entry.Sequence;
                _batch.Add(entry.Record);
            }
        }

        private async Task AcceptAsync(Task<bool> wait)
        {
            try
            {
                if (!await wait.ConfigureAwait(false)) _ended = true;
            }
            catch (OperationCanceledException) when (_reading.IsCancellationRequested)
            {
                _ended = true;                                                      // the wait the loop cancelled
            }
            catch (Exception failure) when (!(failure is OperationCanceledException && _reading.IsCancellationRequested))
            {
                _fault = ExceptionDispatchInfo.Capture(failure);                    // rethrown after what was leased before it
                _ended = true;
            }
        }

        private List<T>? Flush()
        {
            if (_batch.Count == 0) return null;
            List<T> batch = _batch;
            _outFirst = _first;
            _outLast = _last;
            _batch = new List<T>(_size);
            return batch;
        }
    }
}
