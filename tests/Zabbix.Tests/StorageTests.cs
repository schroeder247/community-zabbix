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

/// <summary>The store: a writer face, a stream and an acknowledged face. In memory it is a channel with the policy as its options; durable it is four members over the same faces.</summary>
public sealed class StorageTests
{
    private static (FakeLogCollector Logs, ILoggerFactory Loggers) Logging()
    {
        var logs = new FakeLogCollector();
        return (logs, LoggerFactory.Create(logging => logging.AddProvider(new FakeLoggerProvider(logs)).SetMinimumLevel(LogLevel.Trace)));
    }

    [Fact]
    public async Task Below_the_threshold_it_grows_and_is_fifo()
    {
        await using var store = new ZabbixMemoryStore<int>();
        for (int i = 0; i < 1000; i++) Assert.True(store.TryWrite(i));

        Assert.Equal(1000, store.Count);
        store.Complete();
        Assert.Equal(Enumerable.Range(0, 1000), await store.ToListAsync(cancellationToken: TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task DropOldest_at_the_threshold_keeps_the_newest_and_counts_the_drop()
    {
        (FakeLogCollector logs, ILoggerFactory loggers) = Logging();
        using (loggers)
        {
            await using var store = new ZabbixMemoryStore<int>(new ZabbixStorePolicy<int> { MaxItems = 3 }, new ZabbixStoreServices { LoggerFactory = loggers });
            for (int i = 0; i < 20; i++) Assert.True(store.TryWrite(i));

            Assert.Equal(3, store.Count);
            Assert.Equal(17, store.DroppedCount);
            store.Complete();
            Assert.Equal([17, 18, 19], await store.ToListAsync(cancellationToken: TestContext.Current.CancellationToken));
            // A steady loss is a warning at the 1st and 10th drop, never a line per record.
            Assert.Equal([1L, 10L], logs.GetSnapshot().Where(record => record.Id.Id == 2100).Select(record => long.Parse(record.StructuredState!.Single(kv => kv.Key == "Dropped").Value!, System.Globalization.CultureInfo.InvariantCulture)));
            Assert.All(logs.GetSnapshot().Where(record => record.Id.Id == 2100), record => Assert.Equal(LogLevel.Warning, record.Level));
        }
    }

    [Fact]
    public async Task A_shed_record_is_never_numbered_so_the_leases_run_without_a_gap()
    {
        await using var store = new ZabbixMemoryStore<int>(new ZabbixStorePolicy<int> { MaxItems = 3 });
        for (int i = 0; i < 20; i++) Assert.True(store.TryWrite(i));

        List<ZabbixStoreEntry<int>> leased = [];
        while (store.TryLease(out ZabbixStoreEntry<int> entry)) leased.Add(entry);

        Assert.Equal([17, 18, 19], leased.Select(entry => entry.Record));
        Assert.Equal([leased[0].Sequence, leased[0].Sequence + 1, leased[0].Sequence + 2], leased.Select(entry => entry.Sequence));
    }

    [Fact]
    public async Task DropWrite_at_the_threshold_takes_the_newcomer_and_drops_it()
    {
        await using var store = new ZabbixMemoryStore<int>(new ZabbixStorePolicy<int> { MaxItems = 2, Overflow = BoundedChannelFullMode.DropWrite });
        Assert.True(store.TryWrite(1));
        Assert.True(store.TryWrite(2));
        Assert.True(store.TryWrite(3));                                                 // a channel takes it and drops it, counted

        Assert.Equal(2, store.Count);
        Assert.Equal(1, store.DroppedCount);
        store.Complete();
        Assert.Equal([1, 2], await store.ToListAsync(cancellationToken: TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task Wait_at_the_threshold_paces_the_producer_and_loses_nothing()
    {
        await using var store = new ZabbixMemoryStore<int>(new ZabbixStorePolicy<int> { MaxItems = 2, Overflow = BoundedChannelFullMode.Wait });
        Assert.True(store.TryWrite(1));
        Assert.True(store.TryWrite(2));
        Assert.False(store.TryWrite(3));                                                // full: the producer waits, nothing is dropped
        ValueTask third = store.WriteAsync(3, TestContext.Current.CancellationToken);
        Assert.False(third.IsCompleted);

        await foreach (int first in store) { Assert.Equal(1, first); break; }
        await third;
        Assert.Equal(2, store.Count);
        Assert.Equal(0, store.DroppedCount);
    }

    [Fact]
    public async Task Admit_refuses_at_the_door_and_counts_it()
    {
        await using ZabbixStore<int> store = new ZabbixMemoryStore<int>(new ZabbixStorePolicy<int> { Admit = x => x > 0 });
        Assert.False(store.TryWrite(-1));
        Assert.False(store.TryWrite(0));
        Assert.True(store.TryWrite(5));
        Assert.Equal(1, store.Count);
        Assert.Equal(2, store.DroppedCount);
    }

    [Fact]
    public async Task Discard_refuses_at_the_door_and_evicts_on_the_way_out()
    {
        var stale = new HashSet<int>();
        await using ZabbixStore<int> store = new ZabbixMemoryStore<int>(new ZabbixStorePolicy<int> { Discard = stale.Contains });
        for (int i = 0; i < 6; i++) Assert.True(store.TryWrite(i));
        stale.Add(2);
        stale.Add(4);
        store.Complete();

        Assert.Equal([0, 1, 3, 5], await store.ToListAsync(cancellationToken: TestContext.Current.CancellationToken));
        Assert.False(store.TryWrite(2));
        Assert.Equal(2, store.DroppedCount);                                                 // the two evicted on the way out; the refusal after Complete is not a drop
    }

    [Fact]
    public async Task Drops_are_a_counter_tagged_with_the_store_and_the_reason()
    {
        using ServiceProvider provider = new ServiceCollection().AddMetrics().BuildServiceProvider();
        IMeterFactory meters = provider.GetRequiredService<IMeterFactory>();
        using var dropped = new MetricCollector<long>(meters, ZabbixTelemetryNames.Store, "zabbix.store.dropped");
        await using var store = new ZabbixMemoryStore<int>(
            new ZabbixStorePolicy<int> { MaxItems = 1, Admit = x => x >= 0 },
            new ZabbixStoreServices { Name = "edge-01", Meters = meters });

        store.TryWrite(1);
        store.TryWrite(2);                                                              // sheds 1
        store.TryWrite(-1);                                                             // refused

        Assert.Collection(dropped.GetMeasurementSnapshot(),
            m => { Assert.Equal(1, m.Value); Assert.Equal("edge-01", m.Tags["zabbix.store.name"]); Assert.Equal(ZabbixStoreDropReason.DropOldest, m.Tags["zabbix.store.reason"]); },
            m => { Assert.Equal(1, m.Value); Assert.Equal(ZabbixStoreDropReason.Refused, m.Tags["zabbix.store.reason"]); });
    }

    [Fact]
    public async Task The_store_carries_the_policy_it_was_given()
    {
        await using ZabbixStore<int> store = new ZabbixMemoryStore<int>(new ZabbixStorePolicy<int> { MaxItems = 500, Overflow = BoundedChannelFullMode.DropWrite });

        Assert.Equal(500, store.Policy.MaxItems);
        Assert.Equal(BoundedChannelFullMode.DropWrite, store.Policy.Overflow);
        Assert.IsType<ZabbixMemoryStore<int>>(store);
    }

    [Fact]
    public async Task A_store_is_a_writer_face_and_a_stream_so_it_composes_with_the_platform_s_own_primitives()
    {
        Channel<int> ingress = Channel.CreateBounded<int>(4);
        await using var store = new ZabbixMemoryStore<int>();
        Task preprocess = Task.Run(async () =>                                          // a transform stage: read, map, write
        {
            await foreach (int x in ingress.Reader.ReadAllAsync()) await store.WriteAsync(x * 10);
            store.Complete();
        }, TestContext.Current.CancellationToken);

        for (int i = 1; i <= 4; i++) await ingress.Writer.WriteAsync(i, TestContext.Current.CancellationToken);
        ingress.Writer.Complete();
        await preprocess;

        Assert.Equal([10, 20, 30, 40], await store.ToListAsync(cancellationToken: TestContext.Current.CancellationToken));
    }

    /// <summary>
    /// A backend that is a sorted list: the "four members" formula, proven without an external store. Its rows can be
    /// handed to a second instance, which is what a restart over the same table is.
    /// </summary>
    private sealed class QueueStore<T>(ZabbixStorePolicy<T>? policy = null, ZabbixStoreServices? services = null, SortedList<long, (DateTimeOffset Stored, T Record)>? rows = null)
        : ListBackedStore<T>(policy, services, rows)
    {
        /// <summary>Every load fails while set.</summary>
        public bool Broken
        {
            get => FailLoads;
            set => FailLoads = value;
        }
    }

    [Fact]
    public async Task A_durable_store_persists_then_streams_out_in_order_and_ends_when_complete()
    {
        await using var store = new QueueStore<int>();
        for (int i = 0; i < 5; i++) await store.WriteAsync(i, TestContext.Current.CancellationToken);

        var got = new List<int>();
        Task reading = Task.Run(async () => { await foreach (int x in store) got.Add(x); }, TestContext.Current.CancellationToken);
        await store.WriteAsync(5, TestContext.Current.CancellationToken);                                                        // late or not, the stream takes it
        store.Complete();
        await reading.WaitAsync(TestContext.Current.CancellationToken);

        Assert.Equal([0, 1, 2, 3, 4, 5], got);
    }

    [Fact]
    public async Task A_durable_store_under_wait_resumes_the_writer_when_the_reader_takes_a_record_with_no_clock()
    {
        await using var store = new QueueStore<int>(new ZabbixStorePolicy<int> { MaxItems = 2, Overflow = BoundedChannelFullMode.Wait });
        await store.WriteAsync(1, TestContext.Current.CancellationToken);
        await store.WriteAsync(2, TestContext.Current.CancellationToken);
        ValueTask third = store.WriteAsync(3, TestContext.Current.CancellationToken);
        Assert.False(third.IsCompleted);                                                  // full: waiting for room, not polling for it

        await foreach (int first in store) { Assert.Equal(1, first); break; }

        await third.AsTask().WaitAsync(TestContext.Current.CancellationToken);           // the read acknowledged it and raised the room signal
        Assert.Equal(2, store.Count);
    }

    [Fact]
    public async Task A_refused_record_does_not_spin_the_writer()
    {
        await using var store = new QueueStore<int>(new ZabbixStorePolicy<int> { Admit = x => x > 0 });

        ValueTask write = store.WriteAsync(-1, TestContext.Current.CancellationToken);                                           // refused: counted, and the call returns

        Assert.True(write.IsCompletedSuccessfully);
        Assert.Equal(0, store.Count);
        Assert.Equal(1, store.DroppedCount);
        await write;
    }

    [Fact]
    public async Task A_completion_error_reaches_the_reader_after_the_store_drains()
    {
        await using var store = new QueueStore<int>();
        await store.WriteAsync(1, TestContext.Current.CancellationToken);
        await store.WriteAsync(2, TestContext.Current.CancellationToken);
        store.Complete(new InvalidDataException("source broke"));

        var got = new List<int>();
        InvalidDataException error = await Assert.ThrowsAsync<InvalidDataException>(async () => { await foreach (int x in store) got.Add(x); });

        Assert.Equal([1, 2], got);                                                        // what was stored is delivered first
        Assert.Equal("source broke", error.Message);
    }

    [Fact]
    public void A_durable_store_refuses_drop_newest_at_construction()
    {
        ArgumentOutOfRangeException error = Assert.Throws<ArgumentOutOfRangeException>(() => new QueueStore<int>(new ZabbixStorePolicy<int> { Overflow = BoundedChannelFullMode.DropNewest }));
        Assert.Equal("policy", error.ParamName);
    }

    [Fact]
    public async Task A_backend_fault_is_logged_and_reaches_the_reader()
    {
        (FakeLogCollector logs, ILoggerFactory loggers) = Logging();
        using (loggers)
        {
            // A record left by an earlier process: the store must load it, and the load fails.
            var rows = new SortedList<long, (DateTimeOffset Stored, int Record)> { [1] = (DateTimeOffset.UnixEpoch, 1) };
            await using var store = new QueueStore<int>(services: new ZabbixStoreServices { LoggerFactory = loggers }, rows: rows) { Broken = true, HighWater = 1 };

            await Assert.ThrowsAsync<InvalidOperationException>(async () => await store.ToListAsync(cancellationToken: TestContext.Current.CancellationToken));
            Assert.Contains(logs.GetSnapshot(), record => record.Id.Id == 2000 && record.Level == LogLevel.Error);
        }
    }

    // ----- the acknowledged face ------------------------------------------------------------------------------

    [Fact]
    public async Task A_durable_store_keeps_a_leased_record_until_it_is_acknowledged()
    {
        await using var store = new QueueStore<string>();
        await store.WriteAsync("a", TestContext.Current.CancellationToken);
        await store.WriteAsync("b", TestContext.Current.CancellationToken);
        await store.WriteAsync("c", TestContext.Current.CancellationToken);

        Assert.True(store.TryLease(out ZabbixStoreEntry<string> first));
        Assert.True(store.TryLease(out ZabbixStoreEntry<string> second));
        Assert.Equal(3, store.Count);                                                    // leased is still held
        store.Acknowledge(second.Sequence);
        await store.FlushAsync();                                                         // the backend's removal is the loop's

        Assert.Equal(["a", "b"], new[] { first.Record, second.Record });
        Assert.True(first.Sequence < second.Sequence);
        Assert.Equal(["c"], store.Rows.Values.Select(row => row.Record));
    }

    [Fact]
    public async Task A_batch_in_flight_when_the_process_stops_is_leased_again_with_the_same_sequences()
    {
        var before = new QueueStore<string>();
        await before.WriteAsync("a", TestContext.Current.CancellationToken);
        await before.WriteAsync("b", TestContext.Current.CancellationToken);
        Assert.True(before.TryLease(out ZabbixStoreEntry<string> a));
        Assert.True(before.TryLease(out ZabbixStoreEntry<string> b));                     // in flight: no acknowledgement

        await using var after = new QueueStore<string>(rows: before.Rows) { HighWater = before.HighWater };   // the restart, over the same table
        Assert.True(await after.WaitToLeaseAsync(TestContext.Current.CancellationToken));                     // opened and loaded
        Assert.True(after.TryLease(out ZabbixStoreEntry<string> again));
        await after.WriteAsync("c", TestContext.Current.CancellationToken);
        Assert.True(after.TryLease(out _));
        Assert.True(after.TryLease(out ZabbixStoreEntry<string> c));

        Assert.Equal((a.Sequence, "a"), (again.Sequence, again.Record));
        Assert.True(c.Sequence > b.Sequence);                                            // the ids continue above every id handed out
    }

    [Fact]
    public async Task Acknowledging_past_the_lease_acknowledges_only_what_was_leased()
    {
        await using var store = new QueueStore<int>();
        await store.WriteAsync(1, TestContext.Current.CancellationToken);
        await store.WriteAsync(2, TestContext.Current.CancellationToken);
        Assert.True(store.TryLease(out ZabbixStoreEntry<int> first));

        store.Acknowledge(long.MaxValue);

        Assert.Equal(1, store.Count);
        Assert.True(store.TryLease(out ZabbixStoreEntry<int> second));
        Assert.Equal(2, second.Record);
        Assert.True(second.Sequence > first.Sequence);
    }

    [Fact]
    public async Task A_peek_shows_the_next_record_without_leasing_it()
    {
        await using var store = new QueueStore<int>();
        await store.WriteAsync(7, TestContext.Current.CancellationToken);

        Assert.True(store.TryPeek(out ZabbixStoreEntry<int> peeked));
        Assert.True(store.TryLease(out ZabbixStoreEntry<int> leased));
        Assert.False(store.TryPeek(out _));

        Assert.Equal(peeked, leased);
    }

    [Fact]
    public async Task DropOldest_on_a_durable_store_sheds_the_oldest_record_not_in_flight()
    {
        await using var store = new QueueStore<int>(new ZabbixStorePolicy<int> { MaxItems = 2 });
        await store.WriteAsync(1, TestContext.Current.CancellationToken);
        await store.WriteAsync(2, TestContext.Current.CancellationToken);
        Assert.True(store.TryLease(out _));                                               // 1 is in flight

        await store.WriteAsync(3, TestContext.Current.CancellationToken);                                                                 // full: 2 is the oldest that can go
        await store.FlushAsync();

        Assert.Equal([1, 3], store.Rows.Values.Select(row => row.Record));
        Assert.Equal(1, store.DroppedCount);
    }

    [Fact]
    public async Task WaitToLease_ends_when_the_store_is_complete_and_leased_and_throws_the_completion_error()
    {
        await using var store = new QueueStore<int>();
        store.TryWrite(1);
        store.Complete(new InvalidDataException("source broke"));

        Assert.True(await store.WaitToLeaseAsync(TestContext.Current.CancellationToken));
        Assert.True(store.TryLease(out _));
        InvalidDataException error = await Assert.ThrowsAsync<InvalidDataException>(async () => await store.WaitToLeaseAsync(TestContext.Current.CancellationToken));
        Assert.Equal("source broke", error.Message);
    }

    [Fact]
    public async Task A_memory_store_leases_in_order_with_growing_sequences()
    {
        await using var store = new ZabbixMemoryStore<string>();
        store.TryWrite("a");
        store.TryWrite("b");

        Assert.True(store.TryPeek(out ZabbixStoreEntry<string> peeked));
        Assert.True(store.TryLease(out ZabbixStoreEntry<string> a));
        Assert.True(store.TryLease(out ZabbixStoreEntry<string> b));
        store.Complete();

        Assert.Equal(peeked, a);
        Assert.Equal(["a", "b"], new[] { a.Record, b.Record });
        Assert.True(b.Sequence > a.Sequence);
        Assert.False(await store.WaitToLeaseAsync(TestContext.Current.CancellationToken));
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task A_record_held_longer_than_MaxAge_expires_on_the_way_out_and_is_counted(bool durable)
    {
        var time = new Microsoft.Extensions.Time.Testing.FakeTimeProvider(DateTimeOffset.UnixEpoch.AddDays(1));
        var policy = new ZabbixStorePolicy<int> { MaxAge = TimeSpan.FromHours(1) };
        var services = new ZabbixStoreServices { Time = time };
        await using ZabbixStore<int> store = durable ? new QueueStore<int>(policy, services) : new ZabbixMemoryStore<int>(policy, services);
        await store.WriteAsync(1, TestContext.Current.CancellationToken);
        time.Advance(TimeSpan.FromMinutes(61));
        await store.WriteAsync(2, TestContext.Current.CancellationToken);

        Assert.True(store.TryLease(out ZabbixStoreEntry<int> entry));

        Assert.Equal(2, entry.Record);
        Assert.Equal(1, store.DroppedCount);
        Assert.Equal(time.GetUtcNow(), entry.Stored);
    }

    [Fact]
    public async Task Releasing_a_leased_page_on_a_durable_store_hands_it_out_again_with_the_same_sequences()
    {
        await using var store = new QueueStore<string>();
        await store.WriteAsync("a", TestContext.Current.CancellationToken);
        await store.WriteAsync("b", TestContext.Current.CancellationToken);
        await store.WriteAsync("c", TestContext.Current.CancellationToken);
        Assert.True(store.TryLease(out ZabbixStoreEntry<string> first));
        Assert.True(store.TryLease(out ZabbixStoreEntry<string> second));                    // a page: "a", "b"

        store.Release(0);                                                                    // give the whole page back

        Assert.True(store.TryLease(out ZabbixStoreEntry<string> again));
        Assert.True(store.TryLease(out ZabbixStoreEntry<string> secondAgain));
        Assert.Equal((first.Sequence, first.Record), (again.Sequence, again.Record));
        Assert.Equal((second.Sequence, second.Record), (secondAgain.Sequence, secondAgain.Record));
        Assert.Equal(3, store.Count);                                                        // nothing left the store
    }

    [Fact]
    public async Task Releasing_past_what_was_leased_on_a_durable_store_does_nothing()
    {
        await using var store = new QueueStore<int>();
        await store.WriteAsync(1, TestContext.Current.CancellationToken);
        Assert.True(store.TryLease(out ZabbixStoreEntry<int> leased));

        store.Release(leased.Sequence);                                                      // at the cursor: nothing above it

        Assert.False(store.TryPeek(out _));                                                  // still leased, not handed out again
    }

    [Fact]
    public async Task Releasing_only_the_newer_half_of_a_durable_lease_keeps_the_older_half_leased()
    {
        await using var store = new QueueStore<string>();
        await store.WriteAsync("a", TestContext.Current.CancellationToken);
        await store.WriteAsync("b", TestContext.Current.CancellationToken);
        Assert.True(store.TryLease(out ZabbixStoreEntry<string> a));
        Assert.True(store.TryLease(out ZabbixStoreEntry<string> b));

        store.Release(a.Sequence);                                                           // only "b" goes back

        Assert.True(store.TryPeek(out ZabbixStoreEntry<string> peeked));
        Assert.Equal((b.Sequence, "b"), (peeked.Sequence, peeked.Record));
    }

    [Fact]
    public async Task Releasing_a_leased_page_on_a_memory_store_hands_it_out_again_with_the_same_sequences()
    {
        await using var store = new ZabbixMemoryStore<string>();
        await store.WriteAsync("a", TestContext.Current.CancellationToken);
        await store.WriteAsync("b", TestContext.Current.CancellationToken);
        await store.WriteAsync("c", TestContext.Current.CancellationToken);
        Assert.True(store.TryLease(out ZabbixStoreEntry<string> first));
        Assert.True(store.TryLease(out ZabbixStoreEntry<string> second));

        store.Release(0);

        Assert.True(store.TryLease(out ZabbixStoreEntry<string> again));
        Assert.True(store.TryLease(out ZabbixStoreEntry<string> secondAgain));
        Assert.Equal((first.Sequence, first.Record), (again.Sequence, again.Record));
        Assert.Equal((second.Sequence, second.Record), (secondAgain.Sequence, secondAgain.Record));
        Assert.True(store.TryLease(out ZabbixStoreEntry<string> third));                     // "c" still comes after, from the channel
        Assert.Equal("c", third.Record);
    }

    [Fact]
    public async Task Releasing_a_memory_store_lease_is_visible_to_WaitToLeaseAsync()
    {
        await using var store = new ZabbixMemoryStore<int>();
        await store.WriteAsync(1, TestContext.Current.CancellationToken);
        Assert.True(store.TryLease(out ZabbixStoreEntry<int> leased));

        store.Release(0);

        Assert.True(await store.WaitToLeaseAsync(TestContext.Current.CancellationToken));
        Assert.True(store.TryLease(out ZabbixStoreEntry<int> again));
        Assert.Equal(leased.Sequence, again.Sequence);
    }

    [Fact]
    public async Task Acknowledged_and_pending_are_instruments_tagged_with_the_store()
    {
        using ServiceProvider provider = new ServiceCollection().AddMetrics().BuildServiceProvider();
        IMeterFactory meters = provider.GetRequiredService<IMeterFactory>();
        using var acknowledged = new MetricCollector<long>(meters, ZabbixTelemetryNames.Store, "zabbix.store.acknowledged");
        using var pending = new MetricCollector<long>(meters, ZabbixTelemetryNames.Store, "zabbix.store.pending");
        await using var store = new QueueStore<int>(services: new ZabbixStoreServices { Name = "proxy-01", Meters = meters });
        await store.WriteAsync(1, TestContext.Current.CancellationToken);
        await store.WriteAsync(2, TestContext.Current.CancellationToken);
        Assert.True(store.TryLease(out _));
        Assert.True(store.TryLease(out ZabbixStoreEntry<int> last));

        pending.RecordObservableInstruments();
        store.Acknowledge(last.Sequence);
        pending.RecordObservableInstruments();

        Assert.Equal(2, acknowledged.GetMeasurementSnapshot().Sum(m => m.Value));
        Assert.All(acknowledged.GetMeasurementSnapshot(), m => Assert.Equal("proxy-01", m.Tags["zabbix.store.name"]));
        Assert.Equal([2L, 0L], pending.GetMeasurementSnapshot().Select(m => m.Value));                // leased is still held; acknowledged is gone
        Assert.All(pending.GetMeasurementSnapshot(), m => Assert.Equal("proxy-01", m.Tags["zabbix.store.name"]));
    }
}
