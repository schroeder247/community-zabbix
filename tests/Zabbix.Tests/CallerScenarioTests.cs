// This Source Code Form is subject to the terms of the Mozilla Public
// License, v. 2.0. Copyright (c) 2026 schroeder247. SPDX-License-Identifier: MPL-2.0
#pragma warning disable CA2000 // await using disposes what the chain returns, which is the agent it was called on
using System.Diagnostics.Metrics;
using System.Net;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;

namespace Community.Zabbix.Tests;

/// <summary>
/// What a caller writes for each main scenario. These only have to compile: a change that makes one longer, or brings back a
/// wrapper, fails here first (docs/adr/0012-typed-bcl-fluent-api.md).
/// </summary>
public sealed class CallerScenarioTests
{
    private static readonly ZabbixItemKey<long> Requests = new("app.requests");
    private static readonly ZabbixItemKey<long> Pending = new("orders.pending");
    private static readonly ZabbixItemKey<long> ByStatus = new("orders.count");

    [Fact]
    public void The_main_scenarios_read_as_a_caller_writes_them()
    {
        Func<string, ulong, ulong, CancellationToken, Task> api = ApiAsync;
        Func<CancellationToken, Task> wire = WireAsync;
        Func<CancellationToken, Task> agents = AgentsAsync;

        Assert.NotNull(api);
        Assert.NotNull(wire);
        Assert.NotNull(agents);
    }

    private static async Task ApiAsync(string token, ulong groupId, ulong itemId, CancellationToken cancellationToken)
    {
        await using var zabbix = new ZabbixClient("https://zabbix.example/", token);
        await using var login = new ZabbixClient("https://zabbix.example/", new NetworkCredential("user", "password"));

        IReadOnlyList<ZabbixHost> hosts = await zabbix.Hosts.GetAsync(cancellationToken: cancellationToken);
        ZabbixHost host = await zabbix.Hosts["web-01"].GetAsync(cancellationToken);
        IReadOnlyList<ZabbixHost> web = await zabbix.Hosts.GetAsync(
            q =>
            {
                q.Search.VisibleName = "web";
                q.Limit = 10;
            },
            cancellationToken);

        ulong created = await zabbix.Hosts.CreateAsync("web-02", [groupId], h =>
        {
            h.Interfaces = [ZabbixInterface.Agent(IPAddress.Parse("10.0.0.2"))];
            h.Tags = [("env", "prod")];
        }, cancellationToken);
        await zabbix.Hosts.CreateAsync(new("web-03", [groupId]) { VisibleName = "Web 3" }, cancellationToken);
        await zabbix.Hosts["web-02"].UpdateAsync(h => h.Status = ZabbixHostStatus.Disabled, cancellationToken);
        await zabbix.Items[itemId].UpdateAsync(i => i.Delay = TimeSpan.FromMinutes(5), cancellationToken);
        await zabbix.HostGroups["Linux"].UpdateAsync(g => g.Name = "Linux servers", cancellationToken);
        await zabbix.Hosts["web-02"].DeleteAsync(cancellationToken);
        await zabbix.Items[itemId].DeleteAsync(cancellationToken);

        long last = await zabbix.Hosts["web-01"].GetLastAsync(Requests, cancellationToken);
        double load = await zabbix.Hosts["web-01"].GetLastAsync(ZabbixItemKeys.Cpu.Load(), cancellationToken);
        await foreach (ZabbixSample<long> sample in zabbix.Hosts["web-01"].GetHistoryAsync(Requests, TimeSpan.FromHours(1), cancellationToken)) _ = sample.Value + last + load + created + hosts.Count + web.Count + host.TechnicalName!.Length;
    }

    private static async Task WireAsync(CancellationToken cancellationToken)
    {
        using var wire = new ZabbixWireClient("zbx-server-01");
        await wire.SendAsync("web-01", Requests, 42, cancellationToken);
        await wire.SendAsync(batch => batch.Add("web-01", Requests, 42).Add("web-02", Requests, 43), cancellationToken);

        double load = await wire.GetAgentItemAsync("web-01", ZabbixItemKeys.Cpu.Load(), cancellationToken);
        long free = await wire.GetAgentItemAsync("web-01", ZabbixItemKeys.Disk.FreeBytes("/"), cancellationToken);
        _ = load + free;

        using ZabbixWireClient secured = ZabbixWireClient.CreateBuilder()
            .WithServer("zbx-1;zbx-2")
            .WithPsk("web-01", Convert.FromHexString("0123456789abcdef0123456789abcdef"))
            .WithTimeout(TimeSpan.FromSeconds(5))
            .Build();
    }

    private static async Task AgentsAsync(CancellationToken cancellationToken)
    {
        using Meter meter = new("MyApp");
        Counter<long> requests = meter.CreateCounter<long>("app.requests");
        var queue = new Queue<string>();
        await using ZabbixPushAgent push = ZabbixPushAgent.CreateBuilder("web-01")
            .AddServer("zbx-server-01")
            .AddPoll(Pending, TimeSpan.FromMinutes(1), () => queue.Count)
            .AddMeter("MyApp")
            .Build();
        await push.StartAsync(cancellationToken);
        requests.Add(1);

        await using ZabbixActiveAgent active = ZabbixActiveAgent.CreateBuilder("web-01")
            .AddServer("zbx-1;zbx-2")
            .AddItem(new ZabbixItemKey<string>("app.version"), () => "2.4.1")
            .AddItem(new ZabbixItemKey<long>("app.disk.free"), key => new DriveInfo(key[0]).AvailableFreeSpace)
            .Build();
        await active.StartAsync(cancellationToken);

        HostApplicationBuilder builder = Host.CreateApplicationBuilder();
        builder.Services.AddSingleton<IOrders, Orders>();
        builder.Services.AddZabbixActiveAgent("web-01")
            .AddServer("zbx-server-01")
            .AddItem(Pending, (IOrders orders) => orders.Pending)
            .AddItem(ByStatus, key => key[0].Length)
            .AddItems<Community.Zabbix.Items.ZabbixFileSystemItems>()
            .AddHealthCheck();
        builder.Services.AddZabbixPassiveAgent("web-01")
            .AllowPeer(IPNetwork.Parse("10.0.0.0/8"))
            .AddItem(Pending, (IOrders orders, CancellationToken ct) => orders.CountAsync(ct));
        await builder.Build().RunAsync(cancellationToken);
    }

    private interface IOrders
    {
        long Pending { get; }

        ValueTask<long> CountAsync(CancellationToken cancellationToken);
    }

    private sealed class Orders : IOrders
    {
        public long Pending => 0;

        public ValueTask<long> CountAsync(CancellationToken cancellationToken) => ValueTask.FromResult(0L);
    }
}
