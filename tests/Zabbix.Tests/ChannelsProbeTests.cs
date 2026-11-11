// This Source Code Form is subject to the terms of the Mozilla Public
// License, v. 2.0. Copyright (c) 2026 schroeder247. SPDX-License-Identifier: MPL-2.0
using System.Diagnostics;
using System.Threading.Channels;
using Community.Zabbix.Hosting;
using Community.Zabbix.Storage;
using Microsoft.Extensions.Time.Testing;

namespace Community.Zabbix.Tests;

/// <summary>The probe measures process-wide allocation, so it runs alone.</summary>
[CollectionDefinition(nameof(ChannelsProbeTests), DisableParallelization = true)]
public sealed class ChannelsProbeAlone;

/// <summary>
/// Channels against TPL Dataflow, measured three runs each on one machine (Debug):
/// store, 1,000,000 items — Dataflow <c>BufferStorageBlock → ActionBlock</c> 716–836 ms / 81–99 MB allocated;
/// <c>ZabbixMemoryStore → await foreach</c> 252–313 ms / 8.0 MB. Send, 20,000 batches of 50 — Dataflow
/// <c>BatchBlock → ActionBlock</c> 125–174 ms / 13.6 MB; <c>ZabbixSendLoop</c> 177–381 ms / 9.0 MB. The store is ahead on every
/// property; the loop costs 0.2–0.4 µs more per value and allocates a third less. What stays locked here is the
/// allocation ceiling, the deciding property that does not depend on the box.
/// </summary>
[Collection(nameof(ChannelsProbeTests))]
public sealed class ChannelsProbeTests
{
    private const int N = 1_000_000;

    [Fact]
    public async Task A_million_values_through_the_store_allocate_under_sixteen_megabytes()
    {
        long before = GC.GetTotalAllocatedBytes(precise: true);
        var watch = Stopwatch.StartNew();
        // The buffer is bounded and paced (Wait), so what is measured is the allocation per value through the store, not
        // how far a lagging reader lets the buffer grow: that growth is the scheduler's, and each held entry carries its
        // sequence and stored time (32 bytes for an int), so an unbounded buffer crossed the ceiling on a loaded machine.
        await using var store = new ZabbixMemoryStore<int>(new ZabbixStorePolicy<int> { MaxItems = 4096, Overflow = BoundedChannelFullMode.Wait });
        long sum = 0;
        Task reader = Task.Run(async () => { await foreach (int x in store) sum += x; }, TestContext.Current.CancellationToken);
        for (int i = 0; i < N; i++) await store.WriteAsync(i, TestContext.Current.CancellationToken);
        store.Complete();
        await reader;
        watch.Stop();
        long bytes = GC.GetTotalAllocatedBytes(precise: true) - before;

        Assert.Equal((long)N * (N - 1) / 2, sum);
        Assert.True(bytes < 16 * 1024 * 1024, $"{bytes / 1024.0 / 1024.0:F1} MB allocated for {N:N0} values in {watch.ElapsedMilliseconds} ms");
    }

    /// <summary>
    /// The ceiling is derived, not picked: 20,000 batches of 50 through <see cref="ZabbixSendLoop"/> —
    /// <c>List&lt;int&gt;(50)</c> once per flush (a list object plus its backing array a caller may keep past the call,
    /// so it is never pooled) is 20,000 × (24 B list + 24 B array header + 50 × 4 B) ≈ 4.7 MiB, and the rest is Debug's
    /// un-tiered JIT for the loop's own async state machines and Polly's pipeline (measured ≈ 13 MiB in Debug, isolated;
    /// ≈ 7 MiB in Release). <see cref="GC.GetTotalAllocatedBytes(bool)"/> counts the whole process, not this test's
    /// threads: <see cref="ChannelsProbeAlone"/> keeps the two probes here from overlapping each other, but another
    /// collection's tail work can still land in the window (measured up to +14 MiB in a full run on a loaded machine,
    /// depending on which collection ran before this one). The ceiling is therefore 2.5× the isolated cost: a loop
    /// that doubled its own allocation still fails, and a busy neighbour does not.
    /// </summary>
    [Fact]
    public async Task A_million_values_through_the_send_loop_allocate_under_thirty_two_megabytes()
    {
        // The process-wide counter picks up whatever another collection allocates in the window, so the loop runs three
        // times and the smallest reading is the loop's own cost: contamination only ever adds.
        long best = long.MaxValue;
        long elapsed = 0;
        for (int run = 0; run < 3; run++)
        {
            (long bytes, long ms) = await SendLoopAllocationAsync();
            best = Math.Min(best, bytes);
            elapsed = ms;
        }

        Assert.True(best < 32 * 1024 * 1024, $"{best / 1024.0 / 1024.0:F1} MB allocated for {N:N0} values in {elapsed} ms (best of 3)");
    }

    private static async Task<(long Bytes, long Milliseconds)> SendLoopAllocationAsync()
    {
        const int size = 50;
        long before = GC.GetTotalAllocatedBytes(precise: true);
        var watch = Stopwatch.StartNew();
        long delivered = 0;
        await using var store = new ZabbixMemoryStore<int>(new ZabbixStorePolicy<int> { MaxItems = size * 4, Overflow = BoundedChannelFullMode.Wait });
        Task loop = ZabbixSendLoop.RunAsync<int>(store, (batch, _) => { delivered += batch.Count; return new ValueTask<ZabbixSendOutcome>(ZabbixSendOutcome.Delivered); }, new ZabbixSendLoopOptions { Send = new ZabbixSendOptions { BatchSize = size }, Time = new FakeTimeProvider() }, cancellationToken: TestContext.Current.CancellationToken);
        for (int i = 0; i < N; i++) await store.WriteAsync(i, TestContext.Current.CancellationToken);
        store.Complete();
        await loop;
        watch.Stop();
        long bytes = GC.GetTotalAllocatedBytes(precise: true) - before;

        Assert.Equal(N, delivered);
        return (bytes, watch.ElapsedMilliseconds);
    }
}
