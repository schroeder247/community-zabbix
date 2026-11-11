// This Source Code Form is subject to the terms of the Mozilla Public
// License, v. 2.0. Copyright (c) 2026 schroeder247. SPDX-License-Identifier: MPL-2.0
using System.Net;
using System.Runtime.CompilerServices;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Testing;

namespace Community.Zabbix.Tests;

/// <summary>
/// Adversarial tests of the agent hosts' ends: a container disposed while the pump is writing (no stop first), and
/// an item handler that throws on every check.
/// </summary>
public sealed class AdversarialAgentHostTests
{
    [Fact]
    public async Task An_agent_disposed_while_its_pump_writes_ends_its_loops_before_its_stores_and_logs_no_fault()
    {
        // A trapper that keeps answering: the only faults the agent may log are its own, not a send to a port that closed.
        using var serving = new CancellationTokenSource();
        var (port, received) = MockTrapper.Serve(1, serving.Token);
        var services = new ServiceCollection();
        services.AddLogging(logging => logging.AddFakeLogging().SetMinimumLevel(LogLevel.Debug));
        // A source that does not watch its token: it keeps yielding until the pump stops asking.
        static async IAsyncEnumerable<ZabbixValue> Endless([EnumeratorCancellation] CancellationToken token = default)
        {
            for (int i = 0; ; i++)
            {
                yield return new ZabbixValue("web01", "cpu", i.ToString(System.Globalization.CultureInfo.InvariantCulture), DateTimeOffset.UnixEpoch);
                await Task.Yield();
            }
        }
        services.AddZabbixPushAgent("edge").AddSource(_ => Endless()).AddServer(new DnsEndPoint("127.0.0.1", port)).Configure(o => o.Send.BatchSize = 1);
        ServiceProvider provider = services.BuildValidated();
        var hosted = (IHostedLifecycleService)provider.GetServices<IHostedService>().Single();
        await hosted.StartingAsync(TestContext.Current.CancellationToken);
        await hosted.StartAsync(TestContext.Current.CancellationToken);
        await received.WaitAsync(TimeSpan.FromSeconds(10), TestContext.Current.CancellationToken);   // pumping and sending
        FakeLogCollector logs = provider.GetFakeLogCollector();

        await provider.DisposeAsync().AsTask().WaitAsync(TimeSpan.FromSeconds(20), TimeProvider.System, TestContext.Current.CancellationToken);
        await serving.CancelAsync();

        Assert.DoesNotContain(logs.GetSnapshot(), record => record.Level >= LogLevel.Error);
    }

    [Fact]
    public async Task An_item_handler_that_always_throws_is_answered_not_supported_and_logged_once_per_key_with_its_exception()
    {
        var logs = new FakeLogCollector();
        var logger = new FakeLogger(logs);
        var handlers = new ZabbixItemDictionary();
        handlers.Add("broken", new ZabbixItemCallback((_, _) => throw new InvalidOperationException("the probe failed")));
        ZabbixItemKey key = ZabbixItemKey.Parse("broken[1]");

        ZabbixAgentAnswer first = await handlers.AnswerLoggedAsync(key, TimeSpan.FromSeconds(1), TimeProvider.System, logger, TestContext.Current.CancellationToken);
        ZabbixAgentAnswer second = await handlers.AnswerLoggedAsync(ZabbixItemKey.Parse("broken[2]"), TimeSpan.FromSeconds(1), TimeProvider.System, logger, TestContext.Current.CancellationToken);

        Assert.False(first.IsSupported);
        Assert.Equal("the probe failed", first.Error);
        Assert.False(second.IsSupported);
        FakeLogRecord logged = Assert.Single(logs.GetSnapshot(), record => record.Id.Id == 3211);
        Assert.Equal(LogLevel.Warning, logged.Level);
        Assert.IsType<InvalidOperationException>(logged.Exception);
    }

    [Fact]
    public async Task Two_thousand_items_due_at_once_run_at_most_the_configured_number_of_checks_at_a_time()
    {
        const int Items = 2000;
        string checks = "{\"response\":\"success\",\"config_revision\":1,\"data\":["
            + string.Join(",", Enumerable.Range(1, Items).Select(i => $"{{\"itemid\":{i},\"key\":\"slow[{i}]\",\"delay\":\"1h\"}}")) + "]}";
        await using AdversarialScriptedServer server = await AdversarialScriptedServer.StartAsync((kind, _, _) => kind == "active checks"
            ? checks
            : """{"response":"success","info":"processed: 1; failed: 0; total: 1; seconds spent: 0.000100"}""");
        int running = 0, most = 0, done = 0;
        var all = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var services = new ServiceCollection();
        services.AddZabbixActiveAgent("many").AddServer(new DnsEndPoint(server.Node.Host, server.Node.Port)).AddItem("slow", async (key, cancellationToken) =>
        {
            int now = Interlocked.Increment(ref running);
            int seen;
            while ((seen = Volatile.Read(ref most)) < now && Interlocked.CompareExchange(ref most, now, seen) != seen) { }
            await Task.Delay(TimeSpan.FromMilliseconds(2), TimeProvider.System, cancellationToken);
            Interlocked.Decrement(ref running);
            if (Interlocked.Increment(ref done) == Items) all.TrySetResult();
            return new ZabbixAgentAnswer(key, "1", null);
        }).Configure(o =>
        {
            o.HostName = "edge-01";
            o.HeartbeatInterval = TimeSpan.Zero;
            o.RefreshInterval = TimeSpan.FromHours(1);
            o.MaxConcurrentChecks = 8;
        });
        await using ServiceProvider provider = services.BuildValidated();
        var hosted = (IHostedLifecycleService)provider.GetServices<IHostedService>().Single();
        await hosted.StartingAsync(TestContext.Current.CancellationToken);
        await hosted.StartAsync(TestContext.Current.CancellationToken);

        await all.Task.WaitAsync(TimeSpan.FromSeconds(60), TimeProvider.System, TestContext.Current.CancellationToken);
        await hosted.StoppingAsync(TestContext.Current.CancellationToken);
        await hosted.StopAsync(TestContext.Current.CancellationToken);
        await hosted.StoppedAsync(TestContext.Current.CancellationToken);

        Assert.InRange(most, 1, 8);
    }
}
