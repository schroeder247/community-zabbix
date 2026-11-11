// This Source Code Form is subject to the terms of the Mozilla Public
// License, v. 2.0. Copyright (c) 2026 schroeder247. SPDX-License-Identifier: MPL-2.0
using System.Net;
using System.Runtime.CompilerServices;
using System.Threading.Channels;
using Community.Zabbix.Hosting;
using Community.Zabbix.Protocol;
using Community.Zabbix.Storage;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Testing;
using Microsoft.Extensions.Options;

namespace Community.Zabbix.Tests;

/// <summary>An agent is its sources merged into the store; one bad source never stops the others; named agents are hosted one per name; the whole pipeline runs from the container to a trapper.</summary>
public sealed class AgentTests
{
    private static async IAsyncEnumerable<int> Numbers(int from, int count, [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        for (int i = 0; i < count; i++)
        {
            await Task.Yield();
            yield return from + i;
        }
    }

    private static async IAsyncEnumerable<int> Faulty([EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        await Task.Yield();
        yield return 100;
        throw new InvalidOperationException("source died");
    }

    [Fact]
    public async Task An_agent_merges_its_sources_into_the_store_and_completes_it_when_they_end()
    {
        await using var storage = new ZabbixMemoryStore<int>();
        using var agent = new ZabbixSourcePump<int>(storage, [Numbers(1, 2, TestContext.Current.CancellationToken), Numbers(3, 2, TestContext.Current.CancellationToken)]);

        await agent.RunAsync(TestContext.Current.CancellationToken);
        List<int> sent = await storage.ToListAsync(cancellationToken: TestContext.Current.CancellationToken).AsTask().WaitAsync(TimeSpan.FromSeconds(5), TestContext.Current.CancellationToken);   // the stream ends: the store was completed

        Assert.Equal([1, 2, 3, 4], sent.Order());
    }

    [Fact]
    public async Task One_bad_source_does_not_stop_the_others()
    {
        var logs = new FakeLogCollector();
        await using var storage = new ZabbixMemoryStore<int>();
        using var agent = new ZabbixSourcePump<int>(storage, [Faulty(TestContext.Current.CancellationToken), Numbers(1, 3, TestContext.Current.CancellationToken)], new FakeLogger<ZabbixSourcePump<int>>(logs));

        await agent.RunAsync(TestContext.Current.CancellationToken);

        List<int> items = await storage.ToListAsync(cancellationToken: TestContext.Current.CancellationToken);
        Assert.Equal([1, 2, 3, 100], items.Order());
        Assert.Contains(logs.GetSnapshot(), record => record.Id.Id == 3002 && record.Level == LogLevel.Error);
        Assert.Contains(logs.GetSnapshot(), record => record.Id.Id == 3000);
    }

    private static async IAsyncEnumerable<int> TimesOut([EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        await Task.Yield();
        yield return 1;
        throw new TaskCanceledException("The request was canceled due to the configured HttpClient.Timeout.");
    }

    [Fact]
    public async Task A_source_that_cancels_itself_is_a_fault_logged_and_told_not_a_stop()
    {
        var logs = new FakeLogCollector();
        var faults = new List<Exception>();
        await using var storage = new ZabbixMemoryStore<int>();
        using var agent = new ZabbixSourcePump<int>(storage, [TimesOut(TestContext.Current.CancellationToken)], new FakeLogger<ZabbixSourcePump<int>>(logs));

        await agent.RunAsync(faults.Add, null, TestContext.Current.CancellationToken, TestContext.Current.CancellationToken);

        Assert.IsType<TaskCanceledException>(Assert.Single(faults));
        Assert.Contains(logs.GetSnapshot(), record => record.Id.Id == 3002 && record.Level == LogLevel.Error);
    }

    /// <summary>A durable store that counts its commits.</summary>
    private sealed class CountingStore : ListBackedStore<int>
    {
        private int _appends;

        public int Appends => Volatile.Read(ref _appends);

        protected override async ValueTask<IReadOnlyList<long>> AppendAsync(IReadOnlyList<ZabbixStoreEntry<int>> records, CancellationToken cancellationToken)
        {
            Interlocked.Increment(ref _appends);
            await Task.Delay(TimeSpan.FromMilliseconds(1), cancellationToken);           // a commit takes a while, as a database's does
            return await base.AppendAsync(records, cancellationToken);
        }
    }

    [Fact]
    public async Task The_pump_commits_a_durable_store_in_batches_not_one_value_per_commit()
    {
        await using var storage = new CountingStore();
        using var agent = new ZabbixSourcePump<int>(storage, [Numbers(1, 1000, TestContext.Current.CancellationToken)]);

        await agent.RunAsync(TestContext.Current.CancellationToken);
        await storage.FlushAsync().WaitAsync(TimeSpan.FromSeconds(30), TestContext.Current.CancellationToken);

        Assert.Equal(1000, storage.Rows.Count);
        Assert.True(storage.Appends < 100, $"{storage.Appends} commits for 1,000 values");
    }

    [Fact]
    public async Task Values_the_drain_window_closes_on_are_counted_abandoned_and_logged()
    {
        var logs = new FakeLogCollector();
        var abandoned = new List<int>();
        Channel<int> edge = Channel.CreateUnbounded<int>();
        for (int i = 0; i < 5; i++) edge.Writer.TryWrite(i);
        await using var storage = new ZabbixMemoryStore<int>(new ZabbixStorePolicy<int> { MaxItems = 1, Overflow = BoundedChannelFullMode.Wait });
        storage.TryWrite(-1);                                                                     // full: the pump waits with the first value in hand
        using var agent = new ZabbixSourcePump<int>(storage, [new ZabbixCompletingSource<int>(edge.Reader)], new FakeLogger<ZabbixSourcePump<int>>(logs));
        using var abandoning = new CancellationTokenSource();

        Task pumping = agent.RunAsync(null, abandoned.Add, CancellationToken.None, abandoning.Token);
        while (edge.Reader.Count == 5) await Task.Yield();                                       // the pump took one
        await abandoning.CancelAsync();
        await pumping.WaitAsync(TimeSpan.FromSeconds(10), TestContext.Current.CancellationToken);

        Assert.Equal(5, Assert.Single(abandoned));                                                // the value in hand and the four the edge held
        FakeLogRecord logged = Assert.Single(logs.GetSnapshot(), record => record.Id.Id == 3008);
        Assert.Equal(LogLevel.Warning, logged.Level);
    }

    [Fact]
    public async Task One_full_destination_does_not_keep_a_value_from_the_others()
    {
        await using var full = new ZabbixMemoryStore<int>(new ZabbixStorePolicy<int> { MaxItems = 1, Overflow = BoundedChannelFullMode.Wait });
        await using var open = new ZabbixMemoryStore<int>();
        full.TryWrite(0);
        var fanOut = new ZabbixFanOutWriter<int>([full, open]);

        ValueTask writing = fanOut.WriteAsync(1, TestContext.Current.CancellationToken);

        Assert.Equal(1, open.Count);                                                              // the open destination has it at once
        Assert.False(writing.IsCompleted);                                                        // the full one is still waited on
        Assert.True(full.TryLease(out ZabbixStoreEntry<int> first));
        full.Acknowledge(first.Sequence);
        await writing.AsTask().WaitAsync(TimeSpan.FromSeconds(10), TestContext.Current.CancellationToken);
        Assert.Equal(1, full.Count);
    }

    [Fact]
    public async Task An_agent_is_a_hosted_service_that_stops_with_the_host()
    {
        await using var storage = new ZabbixMemoryStore<int>();
        using var agent = new ZabbixSourcePump<int>(storage, [Numbers(1, 3, TestContext.Current.CancellationToken)]);

        await agent.StartAsync(TestContext.Current.CancellationToken);
        await agent.ExecuteTask!.WaitAsync(TimeSpan.FromSeconds(5), TestContext.Current.CancellationToken);
        await agent.StopAsync(TestContext.Current.CancellationToken);

        Assert.Equal(3, storage.Count);
        Assert.False(storage.TryWrite(4));                                      // completed for input; what is held drains downstream
    }

    [Fact]
    public void Two_agents_registered_by_name_are_two_hosted_services_with_their_own_options()
    {
        var services = new ServiceCollection();
        services.AddZabbixPushAgent("edge-01").AddServer(new DnsEndPoint("trapper-a", 10051));
        services.AddZabbixPushAgent("edge-02").AddServer(new DnsEndPoint("trapper-b", 10052));
        services.AddZabbixPushAgent("edge-01").Configure(options => options.MaxItems = 5);    // continues the first
        using ServiceProvider provider = services.BuildValidated();

        IHostedService[] hosted = [.. provider.GetServices<IHostedService>()];
        IOptionsMonitor<ZabbixPushAgentOptions> options = provider.GetRequiredService<IOptionsMonitor<ZabbixPushAgentOptions>>();

        Assert.Equal(2, hosted.Length);
        Assert.Equal("trapper-a:10051", options.Get("edge-01").ServerActive);
        Assert.Equal(5, options.Get("edge-01").MaxItems);
        Assert.Equal("trapper-b:10052", options.Get("edge-02").ServerActive);
    }

    [Fact]
    public async Task A_hosted_agent_collects_stores_and_sends_end_to_end()
    {
        var (port, received) = MockTrapper.Collect(3);
        var services = new ServiceCollection();
        int n = 0;
        services.AddZabbixPushAgent("edge")
            .AddPoll(TimeSpan.FromMilliseconds(20), _ => new ValueTask<IEnumerable<ZabbixValue>>([new ZabbixValue("web01", "cpu", (++n).ToString(System.Globalization.CultureInfo.InvariantCulture), DateTimeOffset.UnixEpoch)]))
            .AddServer(new DnsEndPoint("127.0.0.1", port))
            .Configure(options => { options.Send.BatchSize = 1; options.Send.DrainTimeout = TimeSpan.FromMilliseconds(200); });
        using ServiceProvider provider = services.BuildValidated();
        IHostedService hosted = provider.GetServices<IHostedService>().Single();

        await hosted.StartAsync(TestContext.Current.CancellationToken);
        int count = await received.WaitAsync(TimeSpan.FromSeconds(10), TestContext.Current.CancellationToken);
        await hosted.StopAsync(TestContext.Current.CancellationToken);                                                // the trapper is gone: the drain gives up within DrainTimeout

        Assert.True(count >= 3);
    }

    [Fact]
    public async Task A_store_drains_into_a_wire_client()
    {
        var (port, received) = MockTrapper.Collect(2);
        await using var storage = new ZabbixMemoryStore<ZabbixValue>();
        var wire = new ZabbixWireClient($"127.0.0.1:{port}");
        Task<long> draining = wire.DrainAsync(storage, new ZabbixSendLoopOptions { Send = new ZabbixSendOptions { BatchSize = 2 } }, cancellationToken: TestContext.Current.CancellationToken);

        storage.TryWrite(new ZabbixValue("web01", "k1", "1", DateTimeOffset.UnixEpoch));
        storage.TryWrite(new ZabbixValue("web01", "k2", "2", DateTimeOffset.UnixEpoch));
        storage.Complete();

        Assert.Equal(2, await received.WaitAsync(TimeSpan.FromSeconds(5), TestContext.Current.CancellationToken));
        Assert.Equal(2, await draining.WaitAsync(TimeSpan.FromSeconds(5), TestContext.Current.CancellationToken));
        wire.Dispose();                                                                   // after the drain that uses it
    }

    [Fact]
    public async Task A_drain_without_a_token_runs_until_its_store_is_complete_and_empty()
    {
        var (port, received) = MockTrapper.Collect(2);
        var (otherPort, otherReceived) = MockTrapper.Collect(1);
        await using var storage = new ZabbixMemoryStore<ZabbixValue>();
        var wire = new ZabbixWireClient($"127.0.0.1:{port}");
        var other = new ZabbixWireClient($"127.0.0.1:{otherPort}");
#pragma warning disable xUnit1051 // the overloads without a token are what this test calls
        Task<long> draining = wire.DrainAsync(storage);

        storage.TryWrite(new ZabbixValue("web01", "k1", "1", DateTimeOffset.UnixEpoch));
        storage.TryWrite(new ZabbixValue("web01", "k2", "2", DateTimeOffset.UnixEpoch));
        storage.Complete();
        ZabbixDelivery delivered = await other.DeliverAsync([new ZabbixValue("web01", "k3", "3", DateTimeOffset.UnixEpoch)]);
#pragma warning restore xUnit1051

        Assert.Equal(2, await received.WaitAsync(TimeSpan.FromSeconds(5), TestContext.Current.CancellationToken));
        Assert.Equal(2, await draining.WaitAsync(TimeSpan.FromSeconds(5), TestContext.Current.CancellationToken));
        Assert.Equal((ZabbixDelivery.Delivered, 1), (delivered, await otherReceived.WaitAsync(TimeSpan.FromSeconds(5), TestContext.Current.CancellationToken)));
        wire.Dispose();                                                                   // after the drain that uses it
        other.Dispose();
    }

    /// <summary>A pass that fails for a reason the loop's own contract does not translate is a defect: logged at Error and shown on the status until the next good pass.</summary>
    [Fact]
    public async Task A_loop_pass_that_is_not_an_exchange_failure_is_logged_at_error_and_faults_the_status()
    {
        var logs = new FakeLogCollector();
        var status = new ZabbixAgentStatus("edge");
        var defect = new InvalidOperationException("not an exchange failure");

        await ZabbixAgentLoop.PassAsync("edge", "refresh", status, new FakeLogger<AgentTests>(logs), _ => throw defect, TestContext.Current.CancellationToken);

        Assert.Contains(logs.GetSnapshot(), record => record.Id.Id == 3003 && record.Level == LogLevel.Error && record.Exception == defect);
        Assert.Same(defect, status.Fault);

        await ZabbixAgentLoop.PassAsync("edge", "refresh", status, new FakeLogger<AgentTests>(logs), _ => Task.CompletedTask, TestContext.Current.CancellationToken);

        Assert.Null(status.Fault);                                                     // a good pass clears it: the cadence recovers
    }
}
