// This Source Code Form is subject to the terms of the Mozilla Public
// License, v. 2.0. Copyright (c) 2026 schroeder247. SPDX-License-Identifier: MPL-2.0
using System.Threading.Channels;
using Community.Zabbix.Storage;
using Community.Zabbix.Storage.EntityFrameworkCore;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;

namespace Community.Zabbix.Tests;

/// <summary>
/// One contract, every backing: enumerate, do not sample. The acknowledged face, the writer face under
/// <c>Wait</c>, and disposal behave the same whether the store is a channel in memory, a durable store over a sorted list,
/// or EF Core on SQLite: a consumer drains either without knowing which (<see cref="ZabbixStore{T}"/>'s promise).
/// </summary>
public sealed class StoreContractTests
{
    public static TheoryData<string> Backings => new("memory", "durable", "ef");

    public static TheoryData<string> DurableBackings => new("durable", "ef");

    /// <summary>A store of one backing, and what it takes to clean up after it.</summary>
    private sealed class Backing : IAsyncDisposable
    {
        private readonly string? _file;

        private Backing(string kind, ZabbixStorePolicy<ZabbixValue>? policy)
        {
            if (kind == "ef") _file = Path.Combine(Path.GetTempPath(), $"zbx-contract-{Guid.NewGuid():N}.db");
            Store = Create(kind, _file, policy);
        }

        public ZabbixStore<ZabbixValue> Store { get; }

        public static Backing Open(string kind, ZabbixStorePolicy<ZabbixValue>? policy = null) => new(kind, policy);

        private static ZabbixStore<ZabbixValue> Create(string kind, string? file, ZabbixStorePolicy<ZabbixValue>? policy)
        {
            var services = new ZabbixStoreServices { Name = "contract_" + Guid.NewGuid().ToString("N")[..8] };
            return kind switch
            {
                "memory" => new ZabbixMemoryStore<ZabbixValue>(policy, services),
                "durable" => new ListStore<ZabbixValue>(policy, services),
                _ => new ZabbixEntityFrameworkStore<ZabbixValue>(options => options.UseSqlite($"Data Source={file};Pooling=False"), policy, services),
            };
        }

        public async ValueTask DisposeAsync()
        {
            await Store.DisposeAsync();
            if (_file is null) return;
            SqliteConnection.ClearAllPools();
            File.Delete(_file);
        }
    }

    /// <summary>The smallest honest durable backend: a sorted list, sequences that only grow.</summary>
    private sealed class ListStore<T>(ZabbixStorePolicy<T>? policy, ZabbixStoreServices? services) : ListBackedStore<T>(policy, services);

    private static ZabbixValue Value(int i) => new("web01", $"cpu[{i}]", i.ToString(System.Globalization.CultureInfo.InvariantCulture), DateTimeOffset.UnixEpoch.AddSeconds(i));

    [Theory]
    [MemberData(nameof(Backings))]
    public async Task A_released_page_is_leased_again_in_order_before_anything_newer(string kind)
    {
        // ZabbixEntityFrameworkStore reads ahead of the lease cursor; a release below what it read ahead must not skip the released records,
        // or the next cumulative acknowledgement removes them from the table without their ever being sent.
        await using Backing backing = Backing.Open(kind);
        ZabbixStore<ZabbixValue> store = backing.Store;
        for (int i = 0; i < 3; i++) await store.WriteAsync(Value(i), TestContext.Current.CancellationToken);
        Assert.True(store.TryLease(out ZabbixStoreEntry<ZabbixValue> a));
        Assert.True(store.TryLease(out ZabbixStoreEntry<ZabbixValue> b));

        store.Release(0);

        List<ZabbixStoreEntry<ZabbixValue>> again = [];
        while (store.TryLease(out ZabbixStoreEntry<ZabbixValue> entry)) again.Add(entry);
        Assert.Equal(["0", "1", "2"], again.Select(entry => entry.Record.Value));
        Assert.Equal([a.Sequence, b.Sequence], again.Take(2).Select(entry => entry.Sequence));
    }

    [Theory]
    [MemberData(nameof(DurableBackings))]
    public async Task An_acknowledgement_after_a_release_removes_only_what_was_handed_out_again(string kind)
    {
        await using Backing backing = Backing.Open(kind);
        ZabbixStore<ZabbixValue> store = backing.Store;
        for (int i = 0; i < 3; i++) await store.WriteAsync(Value(i), TestContext.Current.CancellationToken);
        Assert.True(store.TryLease(out _));
        Assert.True(store.TryLease(out _));
        store.Release(0);

        Assert.True(store.TryLease(out ZabbixStoreEntry<ZabbixValue> first));
        store.Acknowledge(first.Sequence);

        Assert.Equal(2, store.Count);                                                       // "1" and "2" are still held
        Assert.True(store.TryLease(out ZabbixStoreEntry<ZabbixValue> second));
        Assert.Equal("1", second.Record.Value);
    }

    [Theory]
    [MemberData(nameof(Backings))]
    public async Task Every_writer_waiting_for_room_resumes_when_the_consumer_makes_room(string kind)
    {
        // Under Wait a full store paces its writers; room made for three must resume all three, not the first one only.
        await using Backing backing = Backing.Open(kind, new ZabbixStorePolicy<ZabbixValue> { MaxItems = 3, Overflow = BoundedChannelFullMode.Wait });
        ZabbixStore<ZabbixValue> store = backing.Store;
        for (int i = 0; i < 3; i++) await store.WriteAsync(Value(i), TestContext.Current.CancellationToken);
        Task[] writers = [.. Enumerable.Range(3, 3).Select(i => store.WriteAsync(Value(i), TestContext.Current.CancellationToken).AsTask())];
        Assert.All(writers, writer => Assert.False(writer.IsCompleted));

        List<ZabbixStoreEntry<ZabbixValue>> page = [];
        while (page.Count < 3 && store.TryLease(out ZabbixStoreEntry<ZabbixValue> entry)) page.Add(entry);
        store.Acknowledge(page[^1].Sequence);

        await Task.WhenAll(writers).WaitAsync(TimeSpan.FromSeconds(30), TestContext.Current.CancellationToken);
        Assert.Equal(3, store.Count);
    }

    [Theory]
    [MemberData(nameof(Backings))]
    public async Task Disposing_a_store_ends_a_pending_lease_wait_instead_of_hanging_or_throwing(string kind)
    {
        Backing backing = Backing.Open(kind);
        ValueTask<bool> waiting = backing.Store.WaitToLeaseAsync(TestContext.Current.CancellationToken);
        Assert.False(waiting.IsCompleted);

        await backing.DisposeAsync();

        Assert.False(await waiting.AsTask().WaitAsync(TimeSpan.FromSeconds(30), TestContext.Current.CancellationToken));
    }

    [Theory]
    [MemberData(nameof(Backings))]
    public async Task Disposing_a_store_ends_a_reader_of_its_stream(string kind)
    {
        Backing backing = Backing.Open(kind);
        await backing.Store.WriteAsync(Value(1), TestContext.Current.CancellationToken);
        var read = new List<string>();
        Task reading = Task.Run(async () => { await foreach (ZabbixValue value in backing.Store) read.Add(value.Value); }, TestContext.Current.CancellationToken);
        while (backing.Store.Count > 0) await Task.Yield();                                  // the reader took the one record

        await backing.DisposeAsync();

        await reading.WaitAsync(TimeSpan.FromSeconds(30), TestContext.Current.CancellationToken);
        Assert.Equal(["1"], read);
    }

    [Theory]
    [MemberData(nameof(Backings))]
    public async Task A_disposed_store_refuses_writes_as_a_completed_writer_does(string kind)
    {
        Backing backing = Backing.Open(kind);

        await backing.DisposeAsync();

        Assert.False(backing.Store.TryWrite(Value(1)));
        Assert.True(backing.Store.IsCompleted);
    }

    [Fact]
    public async Task A_released_record_is_still_counted_as_held()
    {
        await using var store = new ZabbixMemoryStore<int>();
        store.TryWrite(1);
        store.TryWrite(2);
        Assert.True(store.TryLease(out _));
        Assert.Equal(1, store.Count);                                                        // a memory store's lease leaves the channel

        store.Release(0);

        Assert.Equal(2, store.Count);                                                        // released is held again: the pending gauge sees it
    }
}
