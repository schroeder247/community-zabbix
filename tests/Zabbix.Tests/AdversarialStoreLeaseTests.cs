// This Source Code Form is subject to the terms of the Mozilla Public
// License, v. 2.0. Copyright (c) 2026 schroeder247. SPDX-License-Identifier: MPL-2.0
using System.Threading.Channels;
using Community.Zabbix.Storage;

namespace Community.Zabbix.Tests;

/// <summary>
/// Adversarial tests of the store's acknowledged face: lease, release and acknowledge interleaved in the orders a
/// consumer that abandons pages produces, the item cap at its exact boundary, and a store disposed under a lease. Each
/// case runs on the memory store and on the durable base (<see cref="AdversarialRowStore{T}"/>).
/// </summary>
public sealed class AdversarialStoreLeaseTests
{
    public static TheoryData<string> Kinds => new("memory", "durable");

    private static ZabbixStore<int> Store(string kind, ZabbixStorePolicy<int>? policy = null)
        => kind == "memory" ? new ZabbixMemoryStore<int>(policy) : new AdversarialRowStore<int>(policy);

    private static List<ZabbixStoreEntry<int>> LeaseAll(ZabbixStore<int> store)
    {
        var leased = new List<ZabbixStoreEntry<int>>();
        while (store.TryLease(out ZabbixStoreEntry<int> entry)) leased.Add(entry);
        return leased;
    }

    [Theory]
    [MemberData(nameof(Kinds))]
    public async Task A_release_below_an_earlier_release_hands_records_out_again_in_sequence_order(string kind)
    {
        await using ZabbixStore<int> store = Store(kind);
        for (int i = 1; i <= 4; i++) await store.WriteAsync(i, TestContext.Current.CancellationToken);
        List<ZabbixStoreEntry<int>> first = LeaseAll(store);
        store.Release(first[1].Sequence);                                                  // 3 and 4 go back
        Assert.True(store.TryLease(out ZabbixStoreEntry<int> third));                       // 3 is out again

        store.Release(0);                                                                   // now everything goes back

        List<ZabbixStoreEntry<int>> again = LeaseAll(store);
        Assert.Equal(3, third.Record);
        Assert.Equal(first.Select(entry => entry.Sequence), again.Select(entry => entry.Sequence));
    }

    [Theory]
    [MemberData(nameof(Kinds))]
    public async Task After_two_overlapping_releases_one_acknowledgement_of_the_last_sequence_empties_the_store(string kind)
    {
        await using ZabbixStore<int> store = Store(kind);
        for (int i = 1; i <= 4; i++) await store.WriteAsync(i, TestContext.Current.CancellationToken);
        List<ZabbixStoreEntry<int>> first = LeaseAll(store);
        store.Release(first[1].Sequence);
        store.TryLease(out _);
        store.Release(0);
        List<ZabbixStoreEntry<int>> again = LeaseAll(store);

        store.Acknowledge(again[^1].Sequence);
        store.Release(0);                                                                   // nothing is left leased to give back

        Assert.Equal(0, store.Count);
        Assert.False(store.TryLease(out _));
    }

    [Theory]
    [MemberData(nameof(Kinds))]
    public async Task Releasing_the_same_page_twice_hands_each_record_out_once(string kind)
    {
        await using ZabbixStore<int> store = Store(kind);
        for (int i = 1; i <= 3; i++) await store.WriteAsync(i, TestContext.Current.CancellationToken);
        LeaseAll(store);

        store.Release(0);
        store.Release(0);

        Assert.Equal([1, 2, 3], LeaseAll(store).Select(entry => entry.Record));
    }

    [Theory]
    [MemberData(nameof(Kinds))]
    public async Task A_release_at_or_above_the_cursor_gives_nothing_back(string kind)
    {
        await using ZabbixStore<int> store = Store(kind);
        for (int i = 1; i <= 3; i++) await store.WriteAsync(i, TestContext.Current.CancellationToken);
        List<ZabbixStoreEntry<int>> leased = LeaseAll(store);

        store.Release(leased[^1].Sequence);
        store.Release(long.MaxValue);

        Assert.False(store.TryLease(out _));
    }

    [Theory]
    [MemberData(nameof(Kinds))]
    public async Task A_negative_release_gives_back_everything_leased_and_nothing_twice(string kind)
    {
        await using ZabbixStore<int> store = Store(kind);
        for (int i = 1; i <= 3; i++) await store.WriteAsync(i, TestContext.Current.CancellationToken);
        LeaseAll(store);

        store.Release(long.MinValue);

        Assert.Equal([1, 2, 3], LeaseAll(store).Select(entry => entry.Record));
    }

    [Theory]
    [InlineData("memory", BoundedChannelFullMode.DropWrite, new[] { 1, 2, 3 })]
    [InlineData("memory", BoundedChannelFullMode.DropOldest, new[] { 2, 3, 4 })]
    [InlineData("durable", BoundedChannelFullMode.DropWrite, new[] { 1, 2, 3 })]
    [InlineData("durable", BoundedChannelFullMode.DropOldest, new[] { 2, 3, 4 })]
    public async Task The_item_cap_holds_exactly_max_items_and_counts_exactly_one_drop_for_one_record_over_it(string kind, BoundedChannelFullMode overflow, int[] kept)
    {
        await using ZabbixStore<int> store = Store(kind, new ZabbixStorePolicy<int> { MaxItems = 3, Overflow = overflow });
        for (int i = 1; i <= 3; i++) await store.WriteAsync(i, TestContext.Current.CancellationToken);
        Assert.Equal((3, 0L), (store.Count, store.DroppedCount));                                 // at the cap: nothing shed yet

        await store.WriteAsync(4, TestContext.Current.CancellationToken);

        Assert.Equal((3, 1L), (store.Count, store.DroppedCount));
        Assert.Equal(kept, LeaseAll(store).Select(entry => entry.Record));
    }

    [Theory]
    [MemberData(nameof(Kinds))]
    public async Task A_cap_of_one_keeps_one_record_under_drop_oldest(string kind)
    {
        await using ZabbixStore<int> store = Store(kind, new ZabbixStorePolicy<int> { MaxItems = 1 });

        for (int i = 1; i <= 5; i++) await store.WriteAsync(i, TestContext.Current.CancellationToken);

        Assert.Equal((1, 4L), (store.Count, store.DroppedCount));
        Assert.Equal([5], LeaseAll(store).Select(entry => entry.Record));
    }

    [Fact]
    public async Task A_memory_store_s_cap_counts_what_is_not_yet_leased_so_a_leased_page_is_held_on_top_of_it()
    {
        await using var store = new ZabbixMemoryStore<int>(new ZabbixStorePolicy<int> { MaxItems = 2, Overflow = BoundedChannelFullMode.DropWrite });
        await store.WriteAsync(1, TestContext.Current.CancellationToken);
        await store.WriteAsync(2, TestContext.Current.CancellationToken);
        LeaseAll(store);                                                                    // in flight, not acknowledged

        await store.WriteAsync(3, TestContext.Current.CancellationToken);
        await store.WriteAsync(4, TestContext.Current.CancellationToken);

        Assert.Equal((2, 0L), (store.Count, store.DroppedCount));                                 // documented: the bound is the channel's
    }

    [Fact]
    public async Task A_durable_store_s_cap_counts_leased_records_too()
    {
        await using var store = new AdversarialRowStore<int>(new ZabbixStorePolicy<int> { MaxItems = 2, Overflow = BoundedChannelFullMode.DropWrite });
        await store.WriteAsync(1, TestContext.Current.CancellationToken);
        await store.WriteAsync(2, TestContext.Current.CancellationToken);
        LeaseAll(store);

        await store.WriteAsync(3, TestContext.Current.CancellationToken);

        Assert.Equal((2, 1L), (store.Count, store.DroppedCount));
    }

    [Theory]
    [MemberData(nameof(Kinds))]
    public async Task A_store_disposed_under_a_lease_takes_late_acknowledgements_and_releases_without_throwing_and_leases_nothing(string kind)
    {
        ZabbixStore<int> store = Store(kind);
        for (int i = 1; i <= 4; i++) await store.WriteAsync(i, TestContext.Current.CancellationToken);
        Assert.True(store.TryLease(out ZabbixStoreEntry<int> leased));

        await store.DisposeAsync();

        Exception? late = Record.Exception(() =>
        {
            store.Acknowledge(leased.Sequence);
            store.Release(0);
        });
        Assert.Null(late);
        Assert.False(store.TryLease(out _));
        Assert.False(store.TryPeek(out _));
        Assert.False(await store.WaitToLeaseAsync(TestContext.Current.CancellationToken));
    }

    [Theory]
    [MemberData(nameof(Kinds))]
    public async Task Disposing_a_store_twice_is_harmless(string kind)
    {
        ZabbixStore<int> store = Store(kind);
        await store.WriteAsync(1, TestContext.Current.CancellationToken);

        await store.DisposeAsync();
        Exception? twice = await Record.ExceptionAsync(() => store.DisposeAsync().AsTask());

        Assert.Null(twice);
    }

    [Theory]
    [MemberData(nameof(Kinds))]
    public async Task A_peek_names_the_sequence_the_next_lease_gives_even_after_a_release(string kind)
    {
        await using ZabbixStore<int> store = Store(kind);
        for (int i = 1; i <= 3; i++) await store.WriteAsync(i, TestContext.Current.CancellationToken);
        List<ZabbixStoreEntry<int>> leased = LeaseAll(store);
        store.Release(leased[0].Sequence);

        Assert.True(store.TryPeek(out ZabbixStoreEntry<int> peeked));
        Assert.True(store.TryLease(out ZabbixStoreEntry<int> next));

        Assert.Equal(next.Sequence, peeked.Sequence);
        Assert.Equal(leased[1].Sequence, next.Sequence);
    }
}
