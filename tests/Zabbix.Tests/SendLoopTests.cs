// This Source Code Form is subject to the terms of the Mozilla Public
// License, v. 2.0. Copyright (c) 2026 schroeder247. SPDX-License-Identifier: MPL-2.0
using System.Diagnostics.Metrics;
using Community.Zabbix.Hosting;
using Community.Zabbix.Storage;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Diagnostics.Metrics.Testing;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Testing;
using Microsoft.Extensions.Time.Testing;

namespace Community.Zabbix.Tests;

/// <summary>
/// The send loop: batches by size or by the flush cadence, keeps a batch until it has a final answer (retry, hold, or a
/// handler that throws), drops a rejected one, and at stop drains what it took and what is ready within the window, then
/// hands the rest back to the source or counts it lost. Nothing here waits for real time: every step waits for an
/// observable (the source's waits, a delivery's entry) or moves a fake clock.
/// </summary>
public sealed class SendLoopTests
{
    private static readonly TimeSpan Guard = TimeSpan.FromSeconds(10);

    private static ValueTask<ZabbixSendOutcome> Answer(ZabbixSendOutcome delivery) => new(delivery);

    [Theory]
    [InlineData(ZabbixSendOutcome.Returned)]
    [InlineData(ZabbixSendOutcome.Abandoned)]
    public async Task A_delivery_that_answers_what_only_a_stopping_loop_reports_is_a_defect_and_its_batch_is_rejected(ZabbixSendOutcome answer)
    {
        var logs = new FakeLogCollector();
        await using var store = new ZabbixMemoryStore<int>();
        store.TryWrite(1);
        store.Complete();

        long taken = await ZabbixSendLoop.RunAsync<int>(store, (_, _) => Answer(answer), new ZabbixSendLoopOptions { Send = new ZabbixSendOptions { RetryDelay = TimeSpan.Zero }, Time = new FakeTimeProvider(), Logger = new FakeLogger(logs) }, TestContext.Current.CancellationToken).WaitAsync(Guard, TestContext.Current.CancellationToken);

        FakeLogRecord defect = Assert.Single(logs.GetSnapshot(), record => record.Id.Id == 3103);
        Assert.Equal(1, taken);
        Assert.Contains(answer.ToString(), Assert.IsType<InvalidOperationException>(defect.Exception).Message, StringComparison.Ordinal);
        Assert.Contains(logs.GetSnapshot(), record => record.Id.Id == 3105);                 // rejected, so the loop goes on
        Assert.Equal(0, store.Count);
    }

    [Fact]
    public async Task A_full_batch_is_delivered_at_once_and_the_stream_s_end_flushes_the_rest()
    {
        var batches = new List<IReadOnlyList<int>>();
        await using var store = new ZabbixMemoryStore<int>();
        Task<long> loop = ZabbixSendLoop.RunAsync<int>(store, (batch, _) => { batches.Add(batch); return Answer(ZabbixSendOutcome.Delivered); }, new ZabbixSendLoopOptions { Send = new ZabbixSendOptions { BatchSize = 3 }, Time = new FakeTimeProvider() }, cancellationToken: TestContext.Current.CancellationToken);

        for (int i = 1; i <= 5; i++) store.TryWrite(i);
        store.Complete();

        Assert.Equal(5, await loop.WaitAsync(Guard, TestContext.Current.CancellationToken));
        Assert.Equal([[1, 2, 3], [4, 5]], batches);
    }

    [Fact]
    public async Task A_partial_batch_is_delivered_when_the_flush_cadence_ticks()
    {
        var time = new FakeTimeProvider();
        var source = new ProbeSource<int>();
        var delivered = new TaskCompletionSource<IReadOnlyList<int>>(TaskCreationOptions.RunContinuationsAsynchronously);
        Task<long> loop = ZabbixSendLoop.RunAsync<int>(source, (batch, _) => { delivered.TrySetResult(batch); return Answer(ZabbixSendOutcome.Delivered); }, new ZabbixSendLoopOptions { Send = new ZabbixSendOptions { BatchSize = 100, FlushInterval = TimeSpan.FromSeconds(5) }, Time = time }, cancellationToken: TestContext.Current.CancellationToken);
        await source.Waits.ReadAsync(TestContext.Current.CancellationToken).AsTask().WaitAsync(Guard, TestContext.Current.CancellationToken);                   // idle on the empty source
        source.Write(1);
        await source.Waits.ReadAsync(TestContext.Current.CancellationToken).AsTask().WaitAsync(Guard, TestContext.Current.CancellationToken);                   // took it, waiting for more
        Assert.False(delivered.Task.IsCompleted);                                     // a partial batch waits for the cadence

        time.Advance(TimeSpan.FromSeconds(5));

        Assert.Equal([1], await delivered.Task.WaitAsync(Guard, TestContext.Current.CancellationToken));
        source.Complete();
        await loop.WaitAsync(Guard, TestContext.Current.CancellationToken);
    }

    [Fact]
    public async Task A_batch_is_kept_and_retried_until_the_destination_takes_it()
    {
        var logs = new FakeLogCollector();
        int attempts = 0;
        var delivered = new List<IReadOnlyList<int>>();
        await using var store = new ZabbixMemoryStore<int>();
        Task<long> loop = ZabbixSendLoop.RunAsync<int>(store, (batch, _) =>
            {
                if (Interlocked.Increment(ref attempts) < 3) return Answer(ZabbixSendOutcome.Retried);
                delivered.Add(batch);
                return Answer(ZabbixSendOutcome.Delivered);
            }, new ZabbixSendLoopOptions { Send = new ZabbixSendOptions { BatchSize = 2, RetryDelay = TimeSpan.Zero }, Time = new FakeTimeProvider(), Logger = new FakeLogger(logs) }, cancellationToken: TestContext.Current.CancellationToken);
        store.TryWrite(1);
        store.TryWrite(2);
        store.Complete();

        Assert.Equal(2, await loop.WaitAsync(Guard, TestContext.Current.CancellationToken));
        Assert.Equal([[1, 2]], delivered);
        Assert.Equal(3, attempts);
        Assert.Equal(2, logs.GetSnapshot().Count(record => record.Id.Id == 3104 && record.Level == LogLevel.Warning));
    }

    [Fact]
    public async Task A_delivery_that_throws_is_logged_as_a_fault_and_retried_so_no_value_is_lost()
    {
        // 250 values and a destination that throws twice: the loop must log each throw and retry, not die on the first
        // with nothing logged and the 250 values gone.
        var logs = new FakeLogCollector();
        int attempts = 0;
        var delivered = new List<int>();
        await using var store = new ZabbixMemoryStore<int>();
        for (int i = 0; i < 250; i++) store.TryWrite(i);
        store.Complete();

        long taken = await ZabbixSendLoop.RunAsync<int>(store, (batch, _) =>
            {
                if (Interlocked.Increment(ref attempts) <= 2) throw new InvalidOperationException("the destination broke");
                delivered.AddRange(batch);
                return Answer(ZabbixSendOutcome.Delivered);
            }, new ZabbixSendLoopOptions { Send = new ZabbixSendOptions { BatchSize = 100, RetryDelay = TimeSpan.Zero }, Time = new FakeTimeProvider(), Logger = new FakeLogger(logs) }, cancellationToken: TestContext.Current.CancellationToken).WaitAsync(Guard, TestContext.Current.CancellationToken);

        Assert.Equal(250, taken);
        Assert.Equal(Enumerable.Range(0, 250), delivered);
        Assert.Equal(2, logs.GetSnapshot().Count(record => record.Id.Id == 3100 && record.Level == LogLevel.Error && record.Exception is InvalidOperationException));
    }

    [Fact]
    public async Task A_held_batch_is_kept_and_offered_again_and_the_hold_is_logged_once()
    {
        var logs = new FakeLogCollector();
        var reports = new List<ZabbixSendProgress>();
        int attempts = 0;
        await using var store = new ZabbixMemoryStore<int>();
        store.TryWrite(1);
        store.Complete();

        long taken = await ZabbixSendLoop.RunAsync<int>(store, (_, _) => Answer(Interlocked.Increment(ref attempts) <= 3 ? ZabbixSendOutcome.Held : ZabbixSendOutcome.Delivered), new ZabbixSendLoopOptions { Send = new ZabbixSendOptions { RetryDelay = TimeSpan.Zero }, Time = new FakeTimeProvider(), Logger = new FakeLogger(logs), Progress = new Progress(reports) }, TestContext.Current.CancellationToken).WaitAsync(Guard, TestContext.Current.CancellationToken);

        Assert.Equal(1, taken);
        Assert.Single(logs.GetSnapshot(), record => record.Id.Id == 3101);
        Assert.DoesNotContain(logs.GetSnapshot(), record => record.Level >= LogLevel.Warning);   // a pause is not a failure
        Assert.Equal([ZabbixSendOutcome.Held, ZabbixSendOutcome.Held, ZabbixSendOutcome.Held, ZabbixSendOutcome.Delivered], reports.Select(report => report.Outcome));
    }

    [Fact]
    public async Task A_rejected_batch_is_dropped_and_counted()
    {
        var logs = new FakeLogCollector();
        await using var store = new ZabbixMemoryStore<int>();
        Task<long> loop = ZabbixSendLoop.RunAsync<int>(store, (_, _) => Answer(ZabbixSendOutcome.Rejected), new ZabbixSendLoopOptions { Send = new ZabbixSendOptions { BatchSize = 2 }, Time = new FakeTimeProvider(), Logger = new FakeLogger(logs) }, cancellationToken: TestContext.Current.CancellationToken);
        store.TryWrite(1);
        store.TryWrite(2);
        store.Complete();

        Assert.Equal(2, await loop.WaitAsync(Guard, TestContext.Current.CancellationToken));
        Assert.Equal(0, store.Count);
        Assert.Contains(logs.GetSnapshot(), record => record.Id.Id == 3105 && record.Level == LogLevel.Warning);
    }

    [Fact]
    public async Task Stopping_while_the_source_has_nothing_ready_neither_throws_nor_loses_the_partial_batch()
    {
        // The loop is waiting on the source's MoveNextAsync with two values batched. The stop must not dispose the
        // iterator under that pending call (NotSupportedException) and lose the two values.
        var source = new ProbeSource<int>();
        var delivered = new List<int>();
        using var stop = new CancellationTokenSource();
        Task<long> loop = ZabbixSendLoop.RunAsync<int>(source, (batch, _) => { delivered.AddRange(batch); return Answer(ZabbixSendOutcome.Delivered); }, new ZabbixSendLoopOptions { Send = new ZabbixSendOptions { BatchSize = 100 }, Time = new FakeTimeProvider() }, cancellationToken: stop.Token);
        source.Write(1);
        source.Write(2);
        await source.Waits.ReadAsync(TestContext.Current.CancellationToken).AsTask().WaitAsync(Guard, TestContext.Current.CancellationToken);                   // idle before the writes
        await source.Waits.ReadAsync(TestContext.Current.CancellationToken).AsTask().WaitAsync(Guard, TestContext.Current.CancellationToken);                   // took 1 and 2; the third read is pending

        await stop.CancelAsync();

        Assert.Equal(2, await loop.WaitAsync(Guard, TestContext.Current.CancellationToken));
        Assert.Equal([1, 2], delivered);
    }

    [Fact]
    public async Task A_stop_lets_the_delivery_in_flight_finish_within_the_window_and_drains_what_is_ready()
    {
        var time = new FakeTimeProvider();
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var delivered = new List<int>();
        using var stop = new CancellationTokenSource();
        await using var store = new ZabbixMemoryStore<int>();
        Task<long> loop = ZabbixSendLoop.RunAsync<int>(store, async (batch, token) =>
            {
                entered.TrySetResult();
                await release.Task.WaitAsync(token);
                delivered.AddRange(batch);
                return ZabbixSendOutcome.Delivered;
            }, new ZabbixSendLoopOptions { Send = new ZabbixSendOptions { BatchSize = 1, DrainTimeout = TimeSpan.FromSeconds(30) }, Time = time }, cancellationToken: stop.Token);
        store.TryWrite(1);
        await entered.Task.WaitAsync(Guard, TestContext.Current.CancellationToken);
        store.TryWrite(2);
        store.TryWrite(3);

        await stop.CancelAsync();                                                     // the delivery in flight is not interrupted
        release.SetResult();

        Assert.Equal(3, await loop.WaitAsync(Guard, TestContext.Current.CancellationToken));
        Assert.Equal([1, 2, 3], delivered);                                           // and what the store had ready followed it
    }

    [Fact]
    public async Task When_the_window_closes_the_batch_goes_back_to_a_store_that_still_takes_writes()
    {
        var time = new FakeTimeProvider();
        var logs = new FakeLogCollector();
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        int attempts = 0;
        using var stop = new CancellationTokenSource();
        await using var store = new ZabbixMemoryStore<int>();
        Task<long> loop = ZabbixSendLoop.RunAsync<int>(store, async (_, token) =>
            {
                Interlocked.Increment(ref attempts);
                entered.TrySetResult();
                await Task.Delay(Timeout.InfiniteTimeSpan, token);                    // the destination hangs
                return ZabbixSendOutcome.Delivered;
            }, new ZabbixSendLoopOptions { Send = new ZabbixSendOptions { BatchSize = 1, DrainTimeout = TimeSpan.FromSeconds(30) }, Time = time, Logger = new FakeLogger(logs) }, cancellationToken: stop.Token);
        store.TryWrite(7);
        await entered.Task.WaitAsync(Guard, TestContext.Current.CancellationToken);

        await stop.CancelAsync();                                                     // the window starts now, on the fake clock
        time.Advance(TimeSpan.FromSeconds(30));

        Assert.Equal(0, await loop.WaitAsync(Guard, TestContext.Current.CancellationToken));
        Assert.Equal(1, attempts);                                                    // one attempt, cancelled only by the window
        Assert.Equal([7], await store.Take(1).ToListAsync(cancellationToken: TestContext.Current.CancellationToken).AsTask().WaitAsync(Guard, TestContext.Current.CancellationToken));   // handed back, not lost
        Assert.Contains(logs.GetSnapshot(), record => record.Id.Id == 3102);
        Assert.DoesNotContain(logs.GetSnapshot(), record => record.Id.Id == 3106);
    }

    [Fact]
    public async Task When_the_window_closes_and_the_source_cannot_take_values_back_they_are_counted_lost()
    {
        var time = new FakeTimeProvider();
        var logs = new FakeLogCollector();
        var reports = new List<ZabbixSendProgress>();
        var source = new ProbeSource<int>();
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        using var stop = new CancellationTokenSource();
        Task<long> loop = ZabbixSendLoop.RunAsync<int>(source, async (_, token) =>
            {
                entered.TrySetResult();
                await Task.Delay(Timeout.InfiniteTimeSpan, token);
                return ZabbixSendOutcome.Delivered;
            }, new ZabbixSendLoopOptions { Send = new ZabbixSendOptions { BatchSize = 1, DrainTimeout = TimeSpan.FromSeconds(30) }, Time = time, Logger = new FakeLogger(logs), Progress = new Progress(reports) }, stop.Token);
        source.Write(7);
        await entered.Task.WaitAsync(Guard, TestContext.Current.CancellationToken);

        await stop.CancelAsync();
        time.Advance(TimeSpan.FromSeconds(30));

        Assert.Equal(0, await loop.WaitAsync(Guard, TestContext.Current.CancellationToken));
        Assert.Contains(logs.GetSnapshot(), record => record.Id.Id == 3106 && record.Level == LogLevel.Warning);
        Assert.Contains(reports, report => report is { Outcome: ZabbixSendOutcome.Abandoned, Count: 1 });
    }

    [Fact]
    public async Task A_source_that_faults_ends_the_loop_with_its_exception_after_what_it_yielded_was_delivered()
    {
        static async IAsyncEnumerable<int> Faulty()
        {
            yield return 1;
            await Task.Yield();
            throw new InvalidDataException("the store broke");
        }
        var delivered = new List<int>();

        await Assert.ThrowsAsync<InvalidDataException>(() => ZabbixSendLoop.RunAsync(Faulty(), (batch, _) => { delivered.AddRange(batch); return Answer(ZabbixSendOutcome.Delivered); }, new ZabbixSendLoopOptions { Send = new ZabbixSendOptions { BatchSize = 10 }, Time = new FakeTimeProvider() }, cancellationToken: TestContext.Current.CancellationToken).WaitAsync(Guard, TestContext.Current.CancellationToken));
        Assert.Equal([1], delivered);
    }

    [Fact]
    public async Task The_batcher_never_re_issues_a_pending_move_next_across_flush_ticks()
    {
        var time = new FakeTimeProvider();
        var source = new ProbeSource<int>();
        await using var batcher = new ZabbixSendLoop.Batcher<int>(source, 2, TimeSpan.FromSeconds(1), time);
        var got = new List<int>();

        Task<List<int>?> next = batcher.NextAsync(TestContext.Current.CancellationToken).AsTask();
        await source.Waits.ReadAsync(TestContext.Current.CancellationToken).AsTask().WaitAsync(Guard, TestContext.Current.CancellationToken);                   // waiting on the source: one read pending

        for (int i = 0; i < 3; i++)
        {
            source.Write(i);
            await source.Waits.ReadAsync(TestContext.Current.CancellationToken).AsTask().WaitAsync(Guard, TestContext.Current.CancellationToken);               // took it; the next read is pending
            time.Advance(TimeSpan.FromSeconds(1));                                    // the tick flushes the partial batch
            got.AddRange(await next.WaitAsync(Guard, TestContext.Current.CancellationToken) ?? []);
            if (i < 2) next = batcher.NextAsync(TestContext.Current.CancellationToken).AsTask();                    // keeps the pending read, issues none
        }

        Assert.Equal([0, 1, 2], got);
        Assert.Equal(4, source.Moves);                                                // one per item, and the one still pending
    }

    [Fact]
    public async Task Every_report_is_counted_on_the_agent_meter_by_outcome()
    {
        // The host's meter factory scopes the collector to this test's meter (a name alone binds the first "Zabbix.Agent"
        // instrument any parallel test published).
        using ServiceProvider provider = new ServiceCollection().AddMetrics().BuildServiceProvider();
        IMeterFactory meters = provider.GetRequiredService<IMeterFactory>();
        using var values = new MetricCollector<long>(meters, ZabbixAgentTelemetry.MeterName, ZabbixAgentTelemetry.ValuesInstrument);
        using var telemetry = new ZabbixAgentTelemetry(meters);
        var status = new ZabbixComponentStatus("send-loop-probe", telemetry);
        int attempts = 0;
        await using var store = new ZabbixMemoryStore<int>();
        store.TryWrite(1);
        store.TryWrite(2);
        store.Complete();

        await ZabbixSendLoop.RunAsync<int>(store, (_, _) => Answer(Interlocked.Increment(ref attempts) == 1 ? ZabbixSendOutcome.Retried : ZabbixSendOutcome.Delivered), new ZabbixSendLoopOptions { Send = new ZabbixSendOptions { RetryDelay = TimeSpan.Zero }, Time = new FakeTimeProvider(), Progress = status }, cancellationToken: TestContext.Current.CancellationToken).WaitAsync(Guard, TestContext.Current.CancellationToken);

        IReadOnlyList<CollectedMeasurement<long>> measured = values.GetMeasurementSnapshot();
        Assert.Contains(measured, m => m.Value == 2 && m.MatchesTags(new KeyValuePair<string, object?>(ZabbixAgentTelemetry.OutcomeTag, "retried"), new KeyValuePair<string, object?>(ZabbixAgentTelemetry.AgentTag, "send-loop-probe")));
        Assert.Contains(measured, m => m.Value == 2 && m.MatchesTags(new KeyValuePair<string, object?>(ZabbixAgentTelemetry.OutcomeTag, "delivered"), new KeyValuePair<string, object?>(ZabbixAgentTelemetry.AgentTag, "send-loop-probe")));
        Assert.Equal(2, status.Delivered);
    }

    [Fact]
    public async Task A_read_the_source_never_finished_is_released_once_it_finishes_not_under_it()
    {
        var read = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        await using var source = new StubbornSource(read.Task);
        var batches = new ZabbixSendLoop.Batcher<int>(source, 10, TimeSpan.FromSeconds(5), new FakeTimeProvider());
        using var stop = new CancellationTokenSource();
        ValueTask<List<int>?> next = batches.NextAsync(stop.Token);
        Assert.True(batches.IsWaiting);
        await stop.CancelAsync();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(async () => await next);

        await batches.DisposeAsync();

        Assert.False(batches.Released.IsCompleted);                                        // the read is still the source's
        Assert.False(source.Disposed);
        read.SetResult(false);
        await batches.Released.WaitAsync(TimeSpan.FromSeconds(30), TestContext.Current.CancellationToken);
        Assert.True(source.Disposed);
    }

    [Fact]
    public async Task A_stop_while_a_read_is_pending_still_takes_every_value_the_source_has_ready()
    {
        await using var source = new ReadySource();
        await using var batches = new ZabbixSendLoop.Batcher<int>(source, 10, TimeSpan.FromSeconds(5), new FakeTimeProvider());
        using var stop = new CancellationTokenSource();
        ValueTask<List<int>?> next = batches.NextAsync(stop.Token);
        Assert.True(batches.IsWaiting);
        source.Buffer(1, 2, 3);                                                             // a store's last values: the read's wake-up is on its way
        await stop.CancelAsync();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(async () => await next);

        List<int>? drained = await batches.NextReadyAsync(TestContext.Current.CancellationToken);

        Assert.Equal([1, 2, 3], drained);
        Assert.Null(await batches.NextReadyAsync(TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task A_durable_store_keeps_the_batch_in_flight_until_it_is_delivered_so_a_restart_leases_it_again()
    {
        var time = new FakeTimeProvider();
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        using var stop = new CancellationTokenSource();
        SortedList<long, (DateTimeOffset Stored, int Record)> rows = [];
        long highWater;
        await using (var store = new ListBackedStore<int>(rows: rows))
        {
            for (int i = 1; i <= 10; i++) await store.WriteAsync(i, TestContext.Current.CancellationToken);   // committed
            Task<long> loop = ZabbixSendLoop.RunAsync<int>(store, async (_, token) =>
                {
                    entered.TrySetResult();
                    await Task.Delay(Timeout.InfiniteTimeSpan, token);                // the destination is down
                    return ZabbixSendOutcome.Delivered;
                }, new ZabbixSendLoopOptions { Send = new ZabbixSendOptions { BatchSize = 5, DrainTimeout = TimeSpan.FromSeconds(1) }, Time = time }, cancellationToken: stop.Token);
            await entered.Task.WaitAsync(Guard, TestContext.Current.CancellationToken);
            await store.FlushAsync().WaitAsync(Guard, TestContext.Current.CancellationToken);   // any removal the reads asked for has reached the backend

            Assert.Equal(10, rows.Count);                                             // nothing is gone before the destination took it

            await stop.CancelAsync();
            time.Advance(TimeSpan.FromSeconds(1));
            Assert.Equal(0, await loop.WaitAsync(Guard, TestContext.Current.CancellationToken));
            highWater = store.HighWater;
        }

        await using var restarted = new ListBackedStore<int>(rows: rows) { HighWater = highWater };
        List<int> again = await restarted.Take(10).ToListAsync(cancellationToken: TestContext.Current.CancellationToken).AsTask().WaitAsync(Guard, TestContext.Current.CancellationToken);
        Assert.Equal(Enumerable.Range(1, 10), again);                                 // the next process has every row
    }

    [Fact]
    public async Task A_cancellation_the_handler_raises_itself_is_a_failed_attempt_retried_not_a_stop()
    {
        int attempts = 0;
        await using var store = new ZabbixMemoryStore<int>();
        store.TryWrite(1);
        store.TryWrite(2);
        store.Complete();

        long taken = await ZabbixSendLoop.RunAsync<int>(store, (_, _) => Interlocked.Increment(ref attempts) == 1
                ? throw new TaskCanceledException("The request was canceled due to the configured HttpClient.Timeout of 100 seconds elapsing.")
                : Answer(ZabbixSendOutcome.Delivered), new ZabbixSendLoopOptions { Send = new ZabbixSendOptions { RetryDelay = TimeSpan.Zero }, Time = new FakeTimeProvider() }, cancellationToken: TestContext.Current.CancellationToken).WaitAsync(Guard, TestContext.Current.CancellationToken);

        Assert.Equal(2, taken);
        Assert.Equal(2, attempts);
    }

    [Fact]
    public async Task A_source_reader_that_faults_when_it_is_released_after_the_stop_is_logged()
    {
        var logs = new FakeLogCollector();
        var read = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        await using var source = new StubbornSource(read.Task) { FailDisposal = true };
        var run = new ZabbixSendLoop.Run<int>((_, _) => Answer(ZabbixSendOutcome.Delivered), new ZabbixSendOptions(), new FakeTimeProvider(), new FakeLogger(logs), null);
        var batches = new ZabbixSendLoop.Batcher<int>(source, 10, TimeSpan.FromSeconds(5), new FakeTimeProvider(), run);
        using var stop = new CancellationTokenSource();
        ValueTask<List<int>?> next = batches.NextAsync(stop.Token);
        await stop.CancelAsync();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(async () => await next);
        await batches.DisposeAsync();

        read.SetResult(false);
        await batches.Released.WaitAsync(Guard, TestContext.Current.CancellationToken);

        FakeLogRecord logged = Assert.Single(logs.GetSnapshot(), record => record.Id.Id == 3107);
        Assert.IsType<InvalidOperationException>(logged.Exception);
    }

    /// <summary>
    /// A source read as a channel is: a read that finds a value takes it at once; one that waits is answered by a wake-up
    /// that is already on its way when the reading is cancelled, so it still yields the value; and once the reading is
    /// cancelled, a read that finds nothing is cancelled.
    /// </summary>
    private sealed class ReadySource : IAsyncEnumerable<int>, IAsyncEnumerator<int>
    {
        private readonly Queue<int> _ready = new();
        private readonly TaskCompletionSource _woken = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private CancellationToken _reading;

        public int Current { get; private set; }

        public IAsyncEnumerator<int> GetAsyncEnumerator(CancellationToken cancellationToken = default)
        {
            _reading = cancellationToken;
            cancellationToken.Register(() => _woken.TrySetResult());
            return this;
        }

        public void Buffer(params int[] values)
        {
            foreach (int value in values) _ready.Enqueue(value);
        }

        public ValueTask<bool> MoveNextAsync()
        {
            if (_ready.TryDequeue(out int value))
            {
                Current = value;
                return new(true);
            }
            return _reading.IsCancellationRequested ? ValueTask.FromCanceled<bool>(_reading) : new(WokenAsync());
        }

        public ValueTask DisposeAsync() => ValueTask.CompletedTask;

        private async Task<bool> WokenAsync()
        {
            await _woken.Task;
            if (!_ready.TryDequeue(out int value)) throw new OperationCanceledException(_reading);
            Current = value;
            return true;
        }
    }

    /// <summary>A source whose one read ignores cancellation and ends when the test says so.</summary>
    private sealed class StubbornSource(Task<bool> read) : IAsyncEnumerable<int>, IAsyncEnumerator<int>
    {
        public bool Disposed { get; private set; }

        /// <summary>Whether disposal throws, as an enumerator whose cleanup fails does.</summary>
        public bool FailDisposal { get; set; }

        public int Current => 0;

        public IAsyncEnumerator<int> GetAsyncEnumerator(CancellationToken cancellationToken = default) => this;

        public ValueTask<bool> MoveNextAsync() => new(read);

        public ValueTask DisposeAsync()
        {
            if (FailDisposal && !Disposed)
            {
                Disposed = true;
                return ValueTask.FromException(new InvalidOperationException("the reader's cleanup failed"));
            }
            Disposed = true;
            return ValueTask.CompletedTask;
        }
    }

    /// <summary>A synchronous progress sink (<see cref="Progress{T}"/> posts to the thread pool, which a test cannot order).</summary>
    private sealed class Progress(List<ZabbixSendProgress> reports) : IProgress<ZabbixSendProgress>
    {
        public void Report(ZabbixSendProgress value)
        {
            lock (reports) reports.Add(value);
        }
    }
}
