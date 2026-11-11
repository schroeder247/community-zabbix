// This Source Code Form is subject to the terms of the Mozilla Public
// License, v. 2.0. Copyright (c) 2026 schroeder247. SPDX-License-Identifier: MPL-2.0
using System.Text.Json;
using Community.Zabbix.Proxy;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Testing;

namespace Community.Zabbix.Tests;

/// <summary>
/// The extension points behind the <c>Register-Zbx*ProxyDataProvider</c>/<c>TaskHandler</c> cmdlets: a builder's
/// <c>AddProvider</c> merges a provider's page into the upload loop's own, and its
/// <c>OnTask</c> runs a task the server hands back in its acknowledgement and uploads the result.
/// </summary>
public sealed class ProxyProviderTests
{
    private const string Hosts = "[10,\"web-01\",0]";
    private const string Items = "[100,2,10,\"cpu\",0,\"\",0]";
    private static readonly TimeSpan Guard = TimeSpan.FromSeconds(5);

    private sealed record Running(ServiceProvider Provider, ZabbixActiveProxy Proxy, FakeLogCollector Logs) : IAsyncDisposable
    {
        public async ValueTask DisposeAsync()
        {
            var hosted = (IHostedLifecycleService)Provider.GetServices<IHostedService>().Single();
            await hosted.StoppingAsync(default);
            await hosted.StopAsync(default);
            await hosted.StoppedAsync(default);
            await Provider.DisposeAsync();
        }
    }

    private static async Task<Running> StartAsync(ZabbixServerNode server, Action<IZabbixActiveProxyBuilder>? compose = null)
    {
        var services = new ServiceCollection();
        services.AddLogging(logging => logging.AddFakeLogging().SetMinimumLevel(LogLevel.Debug));
        services.AddMetrics();
        IZabbixActiveProxyBuilder proxy = services.AddZabbixActiveProxy("proxy-01")
            .AddServer(server.ToDnsEndPoint())
            .AddTrapper(0, o => o.Address = "127.0.0.1")
            .AllowPeer(System.Net.IPAddress.Loopback)
            .Configure(o => { o.DataInterval = TimeSpan.FromMilliseconds(20); o.ConfigInterval = TimeSpan.FromMinutes(1); });
        compose?.Invoke(proxy);
        ServiceProvider provider = services.BuildValidated();
        var hosted = (IHostedLifecycleService)provider.GetServices<IHostedService>().Single();
        await hosted.StartingAsync(default);
        await hosted.StartAsync(default);
        return new Running(provider, provider.GetRequiredKeyedService<ZabbixActiveProxy>("proxy-01"), provider.GetFakeLogCollector());
    }

    private static async Task<FakeZabbixServer> ServerAsync()
    {
        var server = new FakeZabbixServer();
        server.Configure(Hosts, Items);
        await server.StartAsync();
        return server;
    }

    [Fact]
    public async Task A_registered_provider_s_page_reaches_the_server()
    {
        await using FakeZabbixServer server = await ServerAsync();
        await using Running proxy = await StartAsync(server.Node, builder => builder.AddProvider((_, _) => ValueTask.FromResult<ZabbixProxyDataPage?>(
            new ZabbixProxyDataPage(History: [new ZabbixProxyHistoryItem(1, 100, new ZabbixClock(1_700_000_000, 0), "provided")]))));

        JsonElement page = await server.NextAsync("proxy data").WaitAsync(Guard, TestContext.Current.CancellationToken);

        JsonElement item = Assert.Single(page.GetProperty("history data").EnumerateArray());
        Assert.Equal("provided", item.GetProperty("value").GetString());
    }

    [Fact]
    public async Task A_task_in_the_ack_reaches_the_handler_and_its_result_is_uploaded()
    {
        await using FakeZabbixServer server = await ServerAsync();
        server.PendingTasksJson = """[{"type":2,"clock":42,"ttl":0,"command_type":0,"command":"echo hi","execute_on":1,"parent_taskid":7}]""";
        var seen = new TaskCompletionSource<ZabbixProxyTask>();
        await using Running proxy = await StartAsync(server.Node, builder => builder
            .OnTask((task, _) =>
            {
                seen.TrySetResult(task);
                return ValueTask.FromResult(new ZabbixProxyTaskResult(ZabbixProxyTaskType.RemoteCommandResult, task.Clock, task.Ttl, Status: 0, "ok", task.ParentTaskId ?? 0UL));
            })
            .AddProvider((_, _) => ValueTask.FromResult<ZabbixProxyDataPage?>(
                new ZabbixProxyDataPage(History: [new ZabbixProxyHistoryItem(1, 100, new ZabbixClock(1_700_000_000, 0), "kick")]))));

        ZabbixProxyTask task = await seen.Task.WaitAsync(Guard, TestContext.Current.CancellationToken);

        Assert.Equal("echo hi", task.Command);
        Assert.Equal(7UL, task.ParentTaskId);
        JsonElement resultFrame = await WaitForAsync(server, static frame => frame.TryGetProperty("tasks", out _));
        JsonElement result = resultFrame.GetProperty("tasks")[0];
        Assert.Equal("ok", result.GetProperty("info").GetString());
        Assert.Equal(7, result.GetProperty("parent_taskid").GetInt64());
    }

    [Fact]
    public async Task A_provider_that_throws_is_logged_and_does_not_stop_the_turn()
    {
        await using FakeZabbixServer server = await ServerAsync();
        await using Running proxy = await StartAsync(server.Node, builder => builder
            .AddProvider((_, _) => throw new InvalidOperationException("boom"))
            .AddProvider((_, _) => ValueTask.FromResult<ZabbixProxyDataPage?>(
                new ZabbixProxyDataPage(History: [new ZabbixProxyHistoryItem(1, 100, new ZabbixClock(1_700_000_000, 0), "still-sent")]))));

        JsonElement page = await server.NextAsync("proxy data").WaitAsync(Guard, TestContext.Current.CancellationToken);

        Assert.Equal("still-sent", page.GetProperty("history data")[0].GetProperty("value").GetString());
        Assert.Contains(proxy.Logs.GetSnapshot(), record => record.Id.Id == 7017 && record.Level == LogLevel.Warning);
    }

    [Fact]
    public async Task A_task_handler_that_throws_is_logged_and_reports_the_failure_back()
    {
        await using FakeZabbixServer server = await ServerAsync();
        server.PendingTasksJson = """[{"type":2,"clock":42,"ttl":0,"command_type":0,"command":"echo hi","execute_on":1,"parent_taskid":9}]""";
        var handled = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        await using Running proxy = await StartAsync(server.Node, builder => builder
            .OnTask((_, _) =>
            {
                handled.TrySetResult();
                throw new InvalidOperationException("handler boom");
            })
            .AddProvider((_, _) => ValueTask.FromResult<ZabbixProxyDataPage?>(
                new ZabbixProxyDataPage(History: [new ZabbixProxyHistoryItem(1, 100, new ZabbixClock(1_700_000_000, 0), "kick")]))));

        await handled.Task.WaitAsync(Guard, TestContext.Current.CancellationToken);
        JsonElement frame = await WaitForAsync(server, static frame => frame.TryGetProperty("tasks", out _));

        JsonElement result = frame.GetProperty("tasks")[0];
        Assert.Equal(1, result.GetProperty("status").GetInt32());
        Assert.Equal("handler boom", result.GetProperty("info").GetString());
        Assert.Contains(proxy.Logs.GetSnapshot(), record => record.Id.Id == 7018 && record.Level == LogLevel.Warning);
    }

    [Fact]
    public async Task Providers_are_asked_in_the_order_they_were_added_and_their_pages_go_in_one_upload()
    {
        await using FakeZabbixServer server = await ServerAsync();
        await using Running proxy = await StartAsync(server.Node, builder => builder
            .AddProvider((_, _) => ValueTask.FromResult<ZabbixProxyDataPage?>(
                new ZabbixProxyDataPage(History: [new ZabbixProxyHistoryItem(1, 100, new ZabbixClock(1_700_000_000, 0), "first")])))
            .AddProvider((_, _) => ValueTask.FromResult<ZabbixProxyDataPage?>(null))
            .AddProvider((_, _) => ValueTask.FromResult<ZabbixProxyDataPage?>(
                new ZabbixProxyDataPage(History: [new ZabbixProxyHistoryItem(2, 100, new ZabbixClock(1_700_000_000, 0), "second")]))));

        JsonElement page = await WaitForAsync(server, static frame => frame.TryGetProperty("history data", out _));

        Assert.Equal(["first", "second"], page.GetProperty("history data").EnumerateArray().Select(row => row.GetProperty("value").GetString()));
    }

    [Fact]
    public async Task Availability_and_task_results_written_to_their_stores_reach_the_server()
    {
        await using FakeZabbixServer server = await ServerAsync();
        await using Running proxy = await StartAsync(server.Node);

        await proxy.Proxy.AvailabilityStore!.WriteAsync(new ZabbixProxyAvailabilityItem(InterfaceId: 7, Available: 1, Error: null), TestContext.Current.CancellationToken);
        await proxy.Proxy.TasksStore!.WriteAsync(new ZabbixProxyTaskResult(ZabbixProxyTaskType.RemoteCommandResult, Clock: DateTimeOffset.FromUnixTimeSeconds(42), Ttl: 0, Status: 0, "done", ParentTaskId: 11), TestContext.Current.CancellationToken);
        await WaitForAsync(server, frame => server.StoredAvailability.Count > 0 && server.StoredTasks.Count > 0);

        ZabbixProxyAvailabilityItem availability = Assert.Single(server.StoredAvailability);
        Assert.Equal((7UL, 1), (availability.InterfaceId, availability.Available));
        ZabbixProxyTaskResult result = Assert.Single(server.StoredTasks);
        Assert.Equal(("done", 11UL), (result.Info, result.ParentTaskId));
    }

    private static async Task<JsonElement> WaitForAsync(FakeZabbixServer server, Func<JsonElement, bool> matches)
    {
        DateTime deadline = DateTime.UtcNow + Guard;
        while (DateTime.UtcNow < deadline)
        {
            JsonElement frame = await server.NextAsync("proxy data").WaitAsync(Guard, TestContext.Current.CancellationToken);
            if (matches(frame)) return frame;
        }
        throw new TimeoutException("no matching 'proxy data' frame arrived");
    }
}
