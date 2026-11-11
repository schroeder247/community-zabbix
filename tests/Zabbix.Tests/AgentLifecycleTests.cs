// This Source Code Form is subject to the terms of the Mozilla Public
// License, v. 2.0. Copyright (c) 2026 schroeder247. SPDX-License-Identifier: MPL-2.0
using System.Net;
using Community.Zabbix.Storage;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Diagnostics.HealthChecks;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace Community.Zabbix.Tests;

/// <summary>The agent is one phase per lifecycle event.</summary>
public sealed class AgentLifecycleTests
{
    [Fact]
    public async Task The_agent_runs_its_phases_in_the_host_s_order_and_drains_before_it_disposes()
    {
        var (port, received) = MockTrapper.Collect(2);
        var services = new ServiceCollection();
        using var gate = new SemaphoreSlim(0);
        async IAsyncEnumerable<ZabbixValue> Source([System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken token = default)
        {
            yield return new ZabbixValue("web01", "a", "1", DateTimeOffset.UnixEpoch);
            yield return new ZabbixValue("web01", "b", "2", DateTimeOffset.UnixEpoch);
            await gate.WaitAsync(token);                                              // runs until the host stops the sources
        }
        services.AddZabbixPushAgent("edge").AddSource(_ => Source()).AddServer(new DnsEndPoint("127.0.0.1", port)).Configure(o => o.Send.BatchSize = 2);
        using ServiceProvider provider = services.BuildValidated();
        IHostedLifecycleService hosted = Assert.IsAssignableFrom<IHostedLifecycleService>(provider.GetServices<IHostedService>().Single());

        await hosted.StartingAsync(TestContext.Current.CancellationToken);
        await hosted.StartAsync(TestContext.Current.CancellationToken);
        await hosted.StartedAsync(TestContext.Current.CancellationToken);
        Assert.Equal(2, await received.WaitAsync(TimeSpan.FromSeconds(10), TestContext.Current.CancellationToken));         // delivered while running
        await hosted.StoppingAsync(TestContext.Current.CancellationToken);                                          // the source's wait is cancelled
        await hosted.StopAsync(TestContext.Current.CancellationToken);                                              // the drain empties the completed store
        await hosted.StoppedAsync(TestContext.Current.CancellationToken);

        ZabbixAgentStatus status = provider.GetRequiredKeyedService<ZabbixPushAgent>("edge").Status;
        Assert.Equal(2, status.Delivered);
        Assert.NotNull(status.LastDelivered);
        Assert.Equal(0, status.Stored);
    }

    [Fact]
    public async Task A_source_that_ends_on_its_own_timeout_turns_the_agent_unhealthy()
    {
        using var serving = new CancellationTokenSource();
        var (port, _) = MockTrapper.Serve(1, serving.Token);
        static async IAsyncEnumerable<ZabbixValue> TimesOut([System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken token = default)
        {
            await Task.Yield();
            yield return new ZabbixValue("web01", "a", "1", DateTimeOffset.UnixEpoch);
            throw new TaskCanceledException("The request was canceled due to the configured HttpClient.Timeout.");
        }
        var services = new ServiceCollection();
        services.AddZabbixPushAgent("edge").AddSource(_ => TimesOut()).AddServer(new DnsEndPoint("127.0.0.1", port)).AddHealthCheck();
        using ServiceProvider provider = services.BuildValidated();
        IHostedLifecycleService hosted = new Hosting.ZabbixHostedComponent(provider.GetRequiredKeyedService<ZabbixPushAgent>("edge"));

        await hosted.StartingAsync(TestContext.Current.CancellationToken);
        await hosted.StartAsync(TestContext.Current.CancellationToken);
        ZabbixAgentStatus status = provider.GetRequiredKeyedService<ZabbixPushAgent>("edge").Status;
        // The status has no change signal to await, and the fault is set after the source's 3002 record is written.
#pragma warning disable RS0030
        for (int i = 0; i < 500 && status.Fault is null; i++) await Task.Delay(10, TestContext.Current.CancellationToken);
#pragma warning restore RS0030
        HealthReport report = await provider.GetRequiredService<HealthCheckService>().CheckHealthAsync(TestContext.Current.CancellationToken);

        Assert.IsType<TaskCanceledException>(status.Fault);
        Assert.Equal(HealthStatus.Unhealthy, report.Entries["zabbix-agent-edge"].Status);
        await hosted.StopAsync(TestContext.Current.CancellationToken);
        await hosted.StoppedAsync(TestContext.Current.CancellationToken);
        await serving.CancelAsync();
    }

    [Fact]
    public async Task A_stop_the_host_gave_up_on_ends_the_drain_before_its_sender_goes_and_reports_nothing_rejected()
    {
        await using var silent = new FramesTests.Peer(FramesTests.Peer.Stall);              // takes the batch, never answers
        var services = new ServiceCollection();
        services.AddLogging(logging => logging.AddFakeLogging().SetMinimumLevel(LogLevel.Debug));
        async IAsyncEnumerable<ZabbixValue> Source([System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken token = default)
        {
            yield return new ZabbixValue("web01", "a", "1", DateTimeOffset.UnixEpoch);
            await Task.Delay(Timeout.InfiniteTimeSpan, token);
        }
        ZabbixMemoryStore<ZabbixValue>? store = null;
        services.AddZabbixPushAgent("edge").AddSource(_ => Source()).AddServer(new DnsEndPoint("127.0.0.1", silent.Node.Port))
            .UseStore((policy, storeServices) => store = new ZabbixMemoryStore<ZabbixValue>(policy, storeServices))
            .Configure(o => { o.Send.BatchSize = 1; o.Connection.Timeout = TimeSpan.FromMinutes(5); });
        using ServiceProvider provider = services.BuildValidated();
        IHostedLifecycleService hosted = Assert.IsAssignableFrom<IHostedLifecycleService>(provider.GetServices<IHostedService>().Single());
        await hosted.StartingAsync(TestContext.Current.CancellationToken);
        await hosted.StartAsync(TestContext.Current.CancellationToken);
        await silent.Frames.Reader.ReadAsync(TestContext.Current.CancellationToken).AsTask().WaitAsync(TimeSpan.FromSeconds(10), TestContext.Current.CancellationToken);   // the batch is in flight

        using var gaveUp = new CancellationTokenSource();
        await gaveUp.CancelAsync();
        await hosted.StoppingAsync(TestContext.Current.CancellationToken);
        // The source ends and the pump completes the store. The in-flight batch is leased, so the store answers "nothing
        // more to lease" exactly when it is complete: the drain the host gives up on then holds a completed store's batch,
        // which nobody reads again, whatever the scheduler does between the two.
        Assert.False(await store!.WaitToLeaseAsync(TestContext.Current.CancellationToken).AsTask().WaitAsync(TimeSpan.FromSeconds(10), TestContext.Current.CancellationToken));
        await hosted.StopAsync(gaveUp.Token);                                                // the host's patience is already spent
        await hosted.StoppedAsync(TestContext.Current.CancellationToken).WaitAsync(TimeSpan.FromSeconds(10), TestContext.Current.CancellationToken);

        ZabbixAgentStatus status = provider.GetRequiredKeyedService<ZabbixPushAgent>("edge").Status;
        Assert.Equal(0, status.Rejected);                                                    // never sent, so never refused
        Assert.Equal(1, status.Abandoned);
        Assert.DoesNotContain(provider.GetFakeLogCollector().GetSnapshot(), record => record.Level >= LogLevel.Error);
    }
}
