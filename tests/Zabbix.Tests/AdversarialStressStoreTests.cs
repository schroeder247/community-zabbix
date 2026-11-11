// This Source Code Form is subject to the terms of the Mozilla Public
// License, v. 2.0. Copyright (c) 2026 schroeder247. SPDX-License-Identifier: MPL-2.0
using System.Threading.Channels;
using Community.Zabbix.Storage;

namespace Community.Zabbix.Tests;

/// <summary>
/// Many producers against one consumer on each store, counted and checksummed — nothing lost,
/// nothing duplicated, no writer left waiting. The bound is a failsafe on a hang, never a pace.
/// </summary>
[Collection("Adversarial hosts stress")]
public sealed class AdversarialStoreStressTests
{
    private static readonly TimeSpan Failsafe = TimeSpan.FromSeconds(20);
    private const int Writers = 8;
    private const int PerWriter = 1_000;

    public static TheoryData<string, int> WaitStores => new()
    {
        { "memory", 1 },
        { "memory", 64 },
        { "durable", 64 },
        { "durable", 1 },
    };

    private static ZabbixStore<int> Store(string kind, ZabbixStorePolicy<int> policy)
        => kind == "memory" ? new ZabbixMemoryStore<int>(policy) : new AdversarialRowStore<int>(policy);

    private static async Task<List<int>> ProduceAndDrainAsync(ZabbixStore<int> store, Func<ZabbixStore<int>, CancellationToken, Task<List<int>>> consume)
    {
        CancellationToken cancellationToken = TestContext.Current.CancellationToken;
        Task<List<int>> consuming = Task.Run(() => consume(store, cancellationToken), cancellationToken);
        Task[] producers = [.. Enumerable.Range(0, Writers).Select(writer => Task.Run(async () =>
        {
            for (int i = 0; i < PerWriter; i++) await store.WriteAsync(writer * 1_000_000 + i, cancellationToken);
        }, cancellationToken))];
        await Task.WhenAll(producers).WaitAsync(Failsafe, cancellationToken);
        store.Complete();
        return await consuming.WaitAsync(Failsafe, cancellationToken);
    }

    private static void AssertEveryRecordOnce(List<int> got)
    {
        long expected = 0;
        for (int writer = 0; writer < Writers; writer++)
            for (int i = 0; i < PerWriter; i++) expected += writer * 1_000_000 + i;
        Assert.Equal(Writers * PerWriter, got.Count);
        Assert.Equal(Writers * PerWriter, got.Distinct().Count());
        Assert.Equal(expected, got.Sum(value => (long)value));
    }

    [Theory]
    [MemberData(nameof(WaitStores))]
    public async Task Many_writers_under_wait_and_one_stream_reader_lose_and_duplicate_nothing(string kind, int capacity)
    {
        await using ZabbixStore<int> store = Store(kind, new ZabbixStorePolicy<int> { MaxItems = capacity, Overflow = BoundedChannelFullMode.Wait });

        List<int> got = await ProduceAndDrainAsync(store, static async (store, token) => await store.ToListAsync(token));

        AssertEveryRecordOnce(got);
    }

    [Fact]
    public async Task Sequences_leased_from_a_memory_store_fed_by_concurrent_writers_only_increase()
    {
        await using var store = new ZabbixMemoryStore<int>(new ZabbixStorePolicy<int> { MaxItems = 64, Overflow = BoundedChannelFullMode.Wait });

        List<int> got = await ProduceAndDrainAsync(store, static async (store, token) =>
        {
            var inversions = new List<int>();
            long last = 0;
            while (await store.WaitToLeaseAsync(token))
            {
                while (store.TryLease(out ZabbixStoreEntry<int> entry))
                {
                    if (entry.Sequence <= last) inversions.Add(entry.Record);                // a peer de-duplicating on the id drops this one
                    last = Math.Max(last, entry.Sequence);
                    store.Acknowledge(entry.Sequence);
                }
            }
            return inversions;
        });

        Assert.Empty(got);
    }

    [Theory]
    [InlineData("memory", 1)]
    [InlineData("memory", 64)]
    [InlineData("durable", 64)]
    [InlineData("durable", 1)]
    public async Task Many_writers_and_one_lease_reader_that_releases_every_third_page_lose_and_duplicate_nothing(string kind, int capacity)
    {
        await using ZabbixStore<int> store = Store(kind, new ZabbixStorePolicy<int> { MaxItems = capacity, Overflow = BoundedChannelFullMode.Wait });

        List<int> got = await ProduceAndDrainAsync(store, static async (store, token) =>
        {
            var acknowledged = new List<int>();
            var page = new List<ZabbixStoreEntry<int>>();
            int turn = 0;
            while (await store.WaitToLeaseAsync(token))
            {
                while (page.Count < 16 && store.TryLease(out ZabbixStoreEntry<int> entry)) page.Add(entry);
                if (page.Count == 0) continue;
                if (++turn % 3 == 0)
                {
                    store.Release(page[0].Sequence - 1);                                  // the destination failed: the page goes back
                }
                else
                {
                    store.Acknowledge(page[^1].Sequence);
                    acknowledged.AddRange(page.Select(entry => entry.Record));
                }
                page.Clear();
            }
            return acknowledged;
        });

        AssertEveryRecordOnce(got);
    }
}
