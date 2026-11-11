// This Source Code Form is subject to the terms of the Mozilla Public
// License, v. 2.0. Copyright (c) 2026 schroeder247. SPDX-License-Identifier: MPL-2.0
using System.Diagnostics.Metrics;
using System.Threading.Channels;
using Community.Zabbix.Diagnostics;
using Community.Zabbix.Storage;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Diagnostics.Metrics.Testing;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Testing;

namespace Community.Zabbix.Tests;

/// <summary>
/// The store's two faces attacked where <c>StorageTests</c> does not go — writes after
/// completion, acknowledgements twice, out of order and beyond the lease, a disposed store under a waiting consumer, a
/// backend that fails on append. Every case runs on the memory store and on the durable base (<see cref="AdversarialRowStore{T}"/>,
/// the <see cref="ZabbixPersistentStore{T}"/> machinery with no database in the way). The concurrency cases live in
/// <see cref="AdversarialStoreStressTests"/>.
/// </summary>
public sealed class AdversarialStoreTests
{
    private static readonly TimeSpan Failsafe = TimeSpan.FromSeconds(10);

    public static TheoryData<string> Kinds => new("memory", "durable");

    private static ZabbixStore<int> Store(string kind, ZabbixStorePolicy<int>? policy = null, ZabbixStoreServices? services = null)
        => kind == "memory" ? new ZabbixMemoryStore<int>(policy, services) : new AdversarialRowStore<int>(policy, services);

    [Theory]
    [MemberData(nameof(Kinds))]
    public async Task A_write_after_complete_is_refused_and_WriteAsync_throws_channel_closed(string kind)
    {
        await using ZabbixStore<int> store = Store(kind);
        store.TryWrite(1);
        store.Complete();

        bool taken = store.TryWrite(2);
        ChannelClosedException closed = await Assert.ThrowsAsync<ChannelClosedException>(() => store.WriteAsync(3, TestContext.Current.CancellationToken).AsTask());

        Assert.False(taken);
        Assert.NotNull(closed);
        Assert.Equal([1], await store.ToListAsync(TestContext.Current.CancellationToken));
    }

    [Theory]
    [MemberData(nameof(Kinds))]
    public async Task Completing_twice_answers_false_the_second_time_and_keeps_the_first_error(string kind)
    {
        await using ZabbixStore<int> store = Store(kind);
        var first = new InvalidOperationException("first");

        bool once = store.TryComplete(first);
        bool twice = store.TryComplete(new InvalidOperationException("second"));

        Assert.True(once);
        Assert.False(twice);
        InvalidOperationException thrown = await Assert.ThrowsAsync<InvalidOperationException>(async () => await store.WaitToLeaseAsync(TestContext.Current.CancellationToken));
        Assert.Same(first, thrown);
    }

    [Theory]
    [MemberData(nameof(Kinds))]
    public async Task Acknowledging_twice_or_out_of_order_counts_each_record_once(string kind)
    {
        using ServiceProvider provider = new ServiceCollection().AddMetrics().BuildServiceProvider();
        IMeterFactory meters = provider.GetRequiredService<IMeterFactory>();
        using var acknowledged = new MetricCollector<long>(meters, ZabbixTelemetryNames.Store, "zabbix.store.acknowledged");
        await using ZabbixStore<int> store = Store(kind, services: new ZabbixStoreServices { Name = "adv", Meters = meters });
        for (int i = 1; i <= 3; i++) await store.WriteAsync(i, TestContext.Current.CancellationToken);
        var leased = new List<ZabbixStoreEntry<int>>();
        while (store.TryLease(out ZabbixStoreEntry<int> entry)) leased.Add(entry);

        store.Acknowledge(leased[2].Sequence);
        store.Acknowledge(leased[1].Sequence);                                             // older than what is already gone
        store.Acknowledge(leased[2].Sequence);                                             // the same again

        Assert.Equal(3, acknowledged.GetMeasurementSnapshot().Sum(m => m.Value));
        Assert.Equal(0, store.Count);
    }

    [Theory]
    [MemberData(nameof(Kinds))]
    public async Task Acknowledging_a_sequence_never_handed_out_takes_only_what_was_leased(string kind)
    {
        await using ZabbixStore<int> store = Store(kind);
        for (int i = 1; i <= 3; i++) await store.WriteAsync(i, TestContext.Current.CancellationToken);
        Assert.True(store.TryLease(out ZabbixStoreEntry<int> first));

        store.Acknowledge(long.MaxValue);
        store.Acknowledge(long.MinValue);
        store.Acknowledge(0);

        var rest = new List<int>();
        while (store.TryLease(out ZabbixStoreEntry<int> entry)) rest.Add(entry.Record);
        Assert.Equal(1, first.Record);
        Assert.Equal([2, 3], rest);                                                         // the unleased two were not taken by the huge ack
    }

    [Theory]
    [MemberData(nameof(Kinds))]
    public async Task An_acknowledgement_after_a_release_does_not_remove_the_released_records(string kind)
    {
        await using ZabbixStore<int> store = Store(kind);
        for (int i = 1; i <= 3; i++) await store.WriteAsync(i, TestContext.Current.CancellationToken);
        var leased = new List<ZabbixStoreEntry<int>>();
        while (store.TryLease(out ZabbixStoreEntry<int> entry)) leased.Add(entry);

        store.Release(0);
        store.Acknowledge(leased[^1].Sequence);                                           // a late ack for the batch that was given back

        var again = new List<long>();
        while (store.TryLease(out ZabbixStoreEntry<int> entry)) again.Add(entry.Sequence);
        Assert.Equal(leased.Select(entry => entry.Sequence), again);
    }

    [Theory]
    [MemberData(nameof(Kinds))]
    public async Task A_one_value_record_and_a_default_record_round_trip_unchanged(string kind)
    {
        await using ZabbixStore<int> store = Store(kind);

        await store.WriteAsync(0, TestContext.Current.CancellationToken);
        await store.WriteAsync(int.MaxValue, TestContext.Current.CancellationToken);
        store.Complete();

        Assert.Equal([0, int.MaxValue], await store.ToListAsync(TestContext.Current.CancellationToken));
    }

    [Theory]
    [InlineData("memory")]
    [InlineData("durable")]
    public async Task Disposing_a_store_ends_a_pending_lease_wait_with_false_or_object_disposed(string kind)
    {
        ZabbixStore<int> store = Store(kind);
        Task<bool> waiting = store.WaitToLeaseAsync(TestContext.Current.CancellationToken).AsTask();

        await store.DisposeAsync();

        Exception? thrown = await Record.ExceptionAsync(() => waiting.WaitAsync(Failsafe, TestContext.Current.CancellationToken));
        Assert.True(thrown is null or ObjectDisposedException, $"the wait ended with {thrown?.GetType().Name ?? "no exception"}");
        Assert.True(thrown is not null || !await waiting);
    }

    [Theory]
    [InlineData("memory")]
    [InlineData("durable")]
    public async Task Disposing_a_store_ends_a_pending_stream_reader(string kind)
    {
        ZabbixStore<int> store = Store(kind);
        IAsyncEnumerator<int> reader = store.GetAsyncEnumerator(TestContext.Current.CancellationToken);
        Task<bool> moving = reader.MoveNextAsync().AsTask();

        await store.DisposeAsync();

        Exception? thrown = await Record.ExceptionAsync(() => moving.WaitAsync(Failsafe, TestContext.Current.CancellationToken));
        Assert.True(thrown is null or ObjectDisposedException, $"the read ended with {thrown?.GetType().Name ?? "no exception"}");
    }

    [Fact]
    public async Task A_durable_backend_that_fails_on_append_is_logged_as_the_store_s_fault()
    {
        // The write that waits for its commit is told; the commit runs on the store's loop, not on the writer's thread.
        var logs = new FakeLogCollector();
        using ILoggerFactory loggers = LoggerFactory.Create(logging => logging.AddProvider(new FakeLoggerProvider(logs)).SetMinimumLevel(LogLevel.Trace));
        await using var store = new AdversarialRowStore<int>(services: new ZabbixStoreServices { Name = "adv", LoggerFactory = loggers }) { FailAppends = true };

        Exception? thrown = await Record.ExceptionAsync(() => store.WriteAsync(1, TestContext.Current.CancellationToken).AsTask());

        Assert.IsType<IOException>(thrown);
        Assert.Contains(logs.GetSnapshot(), record => record.Id.Id == 2000);            // StoragePumpFaulted, as a failing read is
        Assert.Equal(0, store.Count);
    }

    [Fact]
    public async Task A_record_taken_without_waiting_whose_commit_fails_is_dropped_and_counted_faulted()
    {
        await using var store = new AdversarialRowStore<int>() { FailAppends = true };

        bool taken = store.TryWrite(1);
        store.Complete();
        List<int> drained = await store.ToListAsync(TestContext.Current.CancellationToken);

        Assert.True(taken);                                                              // accepted by the policy
        Assert.Empty(drained);
        Assert.Equal((0, 1L), (store.Count, store.DroppedCount));
    }

    [Fact]
    public async Task A_record_the_backend_cannot_hold_fails_alone_and_the_records_beside_it_are_kept()
    {
        // A commit that fails is retried record by record (ZabbixPersistentStore's remarks): one value the backend cannot
        // hold (a column too narrow, a constraint) costs that value, never the batch committed with it.
        await using var store = new AdversarialRowStore<int>() { Unholdable = value => value == 13 };

        Assert.True(store.TryWrite(1));
        Assert.True(store.TryWrite(13));                                                  // accepted by the policy; its commit fails
        Assert.True(store.TryWrite(2));
        Exception? waited = await Record.ExceptionAsync(() => store.WriteAsync(13, TestContext.Current.CancellationToken).AsTask());
        await store.WriteAsync(3, TestContext.Current.CancellationToken);
        store.Complete();

        Assert.IsType<InvalidDataException>(waited);                                      // the writer that waits is told
        Assert.Equal([1, 2, 3], await store.ToListAsync(TestContext.Current.CancellationToken));
        Assert.Equal(1L, store.DroppedCount);                                              // the one that did not wait is counted
    }

    [Fact]
    public async Task A_durable_backend_that_recovers_takes_the_next_write_and_keeps_what_it_held()
    {
        await using var store = new AdversarialRowStore<int>();
        await store.WriteAsync(1, TestContext.Current.CancellationToken);
        store.FailAppends = true;
        await Assert.ThrowsAsync<IOException>(() => store.WriteAsync(2, TestContext.Current.CancellationToken).AsTask());
        store.FailAppends = false;

        await store.WriteAsync(3, TestContext.Current.CancellationToken);

        store.Complete();
        Assert.Equal([1, 3], await store.ToListAsync(TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task A_refused_record_written_after_completion_is_still_answered_closed()
    {
        await using var store = new ZabbixMemoryStore<int>(new ZabbixStorePolicy<int> { Admit = value => value > 0 });
        store.Complete();

        Exception? thrown = await Record.ExceptionAsync(() => store.WriteAsync(-1, TestContext.Current.CancellationToken).AsTask());

        Assert.IsType<ChannelClosedException>(thrown);
    }

    /// <summary>A backend whose appends wait at a gate, and that records the size of every commit.</summary>
    private sealed class GatedRowStore : ListBackedStore<int>
    {
        public TaskCompletionSource Gate { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public System.Collections.Concurrent.ConcurrentQueue<int> Commits { get; } = new();

        protected override async ValueTask<IReadOnlyList<long>> AppendAsync(IReadOnlyList<ZabbixStoreEntry<int>> records, CancellationToken cancellationToken)
        {
            Commits.Enqueue(records.Count);
            await Gate.Task;
            return await base.AppendAsync(records, cancellationToken);
        }
    }

    [Fact]
    public async Task A_durable_store_whose_backend_hangs_never_blocks_a_writer_or_a_reader_and_commits_what_queued_meanwhile_as_one()
    {
        await using var store = new GatedRowStore();
        Assert.True(store.TryWrite(1));
        await WaitUntilAsync(() => !store.Commits.IsEmpty);                                // the loop is inside the backend now

        bool[] taken = [store.TryWrite(2), store.TryWrite(3), store.TryWrite(4)];
        bool leased = store.TryLease(out _);
        int held = store.Count;
        store.Acknowledge(long.MaxValue);
        store.Gate.SetResult();
        await store.FlushAsync();
        await store.FlushAsync();

        Assert.All(taken, Assert.True);
        Assert.False(leased);                                                              // nothing is committed yet: nothing to lease
        Assert.Equal(4, held);
        Assert.Equal([1, 3], store.Commits);
        var all = new List<int>();
        while (store.TryLease(out ZabbixStoreEntry<int> entry)) all.Add(entry.Record);
        Assert.Equal([1, 2, 3, 4], all);
    }

    private static async Task WaitUntilAsync(Func<bool> condition)
    {
        using var failsafe = new CancellationTokenSource(TimeSpan.FromSeconds(10), TimeProvider.System);
        while (!condition())
        {
            failsafe.Token.ThrowIfCancellationRequested();
            await Task.Yield();
        }
    }

    [Fact]
    public async Task A_durable_store_logs_and_counts_a_drop_outside_its_lock()
    {
        // A drop runs every logger and every MeterListener callback; under the store's lock, one that touches the store
        // from another thread waits on the lock the dropping thread holds.
        var store = new System.Runtime.CompilerServices.StrongBox<ZabbixStore<int>?>();
        using var probing = new ProbingLoggerProvider(() => Task.Run(() => store.Value!.TryPeek(out _)).Wait(TimeSpan.FromSeconds(2)));
        using var loggers = LoggerFactory.Create(builder => builder.AddProvider(probing));
        await using (store.Value = new AdversarialRowStore<int>(new ZabbixStorePolicy<int> { MaxItems = 1, Overflow = BoundedChannelFullMode.DropWrite }, new ZabbixStoreServices { LoggerFactory = loggers }))
        {
            Assert.True(store.Value.TryWrite(1));
            Assert.True(store.Value.TryWrite(2));                                       // full: shed, counted and logged
        }

        Assert.NotEmpty(probing.Results);
        Assert.All(probing.Results, Assert.True);
    }

    [Fact]
    public async Task Shedding_the_oldest_of_many_uncommitted_writes_costs_the_same_for_each_write()
    {
        var committing = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        const int Cap = 30_000;
        await using var store = new StalledRowStore(committing.Task, new ZabbixStorePolicy<int> { MaxItems = Cap, Overflow = BoundedChannelFullMode.DropOldest });
        var watch = System.Diagnostics.Stopwatch.StartNew();

        for (int i = 0; i < 3 * Cap; i++) store.TryWrite(i);                           // the commit is stalled: every write past the cap sheds the oldest pending one

        watch.Stop();
        Assert.Equal(Cap, store.Count);
        Assert.True(watch.Elapsed < TimeSpan.FromSeconds(5), $"{watch.Elapsed.TotalSeconds:F1} s for {3 * Cap:N0} writes");
        committing.SetResult();
    }

    /// <summary>A durable backend whose commits wait until the test lets them through.</summary>
    private sealed class StalledRowStore(Task released, ZabbixStorePolicy<int> policy) : ListBackedStore<int>(policy)
    {
        protected override async ValueTask<IReadOnlyList<long>> AppendAsync(IReadOnlyList<ZabbixStoreEntry<int>> records, CancellationToken cancellationToken)
        {
            await released;
            return await base.AppendAsync(records, cancellationToken);
        }
    }

    /// <summary>A logger that runs a probe on every drop it is told of (event 2100) and keeps what the probe answered.</summary>
    private sealed class ProbingLoggerProvider(Func<bool> probe) : ILoggerProvider, ILogger
    {
        public List<bool> Results { get; } = [];

        public ILogger CreateLogger(string categoryName) => this;

        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;

        public bool IsEnabled(LogLevel logLevel) => true;

        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter)
        {
            if (eventId.Id != 2100) return;
            bool answered = probe();
            lock (Results) Results.Add(answered);
        }

        public void Dispose()
        {
        }
    }
}
