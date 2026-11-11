// This Source Code Form is subject to the terms of the Mozilla Public
// License, v. 2.0. Copyright (c) 2026 schroeder247. SPDX-License-Identifier: MPL-2.0
using System.Diagnostics.Metrics;
using System.Net;
using System.Threading.Channels;
using Community.Zabbix.Diagnostics;
using Community.Zabbix.Storage;
using Community.Zabbix.Storage.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Diagnostics.Metrics.Testing;
using Microsoft.Extensions.Hosting;

namespace Community.Zabbix.Tests;

/// <summary>
/// The stores a host builds: one per destination, from the host's own policy and services (its name, loggers, meter factory
/// and clock), whether the host builds the in-memory default or the caller supplies the backing through an agent's <c>UseStore</c> or a proxy's <c>AddStore</c>.
/// </summary>
public sealed class HostingStoreTests
{
    private static async Task<IHostedLifecycleService> StartingAsync(ServiceProvider provider)
    {
        var hosted = (IHostedLifecycleService)provider.GetServices<IHostedService>().Single();
        await hosted.StartingAsync(TestContext.Current.CancellationToken);
        return hosted;
    }

    private static async Task StoppedAsync(IHostedLifecycleService hosted)
    {
        await hosted.StopAsync(TestContext.Current.CancellationToken);
        await hosted.StoppedAsync(TestContext.Current.CancellationToken);
    }

    [Fact]
    public async Task An_agent_s_own_store_is_built_once_per_destination_from_the_agent_s_policy_and_the_host_s_services()
    {
        var built = new List<(ZabbixStorePolicy<ZabbixValue> Policy, ZabbixStoreServices Services)>();
        var services = new ServiceCollection();
        services.AddZabbixPushAgent("edge")
            .AddServer(new DnsEndPoint("127.0.0.1", 10061)).AddServer(new DnsEndPoint("127.0.0.2", 10061))
            .Configure(o => { o.MaxItems = 7; o.Overflow = BoundedChannelFullMode.DropWrite; })
            .UseStore((policy, store) =>
            {
                built.Add((policy, store));
                return new ZabbixMemoryStore<ZabbixValue>(policy, store);
            });
        await using ServiceProvider provider = services.BuildValidated();

        IHostedLifecycleService hosted = await StartingAsync(provider);

        Assert.Equal(2, built.Count);
        Assert.Equal(2, built.Select(store => store.Services.Name).Distinct().Count());          // a durable backing gets a table per destination
        Assert.All(built, store => Assert.Equal((7, BoundedChannelFullMode.DropWrite), (store.Policy.MaxItems, store.Policy.Overflow)));
        Assert.All(built, store => Assert.Same(provider.GetRequiredService<IMeterFactory>(), store.Services.Meters));
        Assert.All(built, store => Assert.Same(provider.GetRequiredService<TimeProvider>(), store.Services.Time));
        await StoppedAsync(hosted);
    }

    [Fact]
    public async Task One_store_handed_to_two_destinations_is_refused_at_start()
    {
        // Two send loops draining one store would each take a share of the values: every destination must get every value.
        await using var shared = new ZabbixMemoryStore<ZabbixValue>();
        var services = new ServiceCollection();
        services.AddZabbixPushAgent("edge").AddServer(new DnsEndPoint("127.0.0.1", 10061)).AddServer(new DnsEndPoint("127.0.0.2", 10061)).UseStore((_, _) => shared);
        await using ServiceProvider provider = services.BuildValidated();
        var hosted = (IHostedLifecycleService)provider.GetServices<IHostedService>().Single();

        InvalidOperationException refused = await Assert.ThrowsAsync<InvalidOperationException>(() => hosted.StartingAsync(TestContext.Current.CancellationToken));

        Assert.Contains("own store", refused.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task A_later_use_of_a_store_replaces_an_earlier_one()
    {
        var built = new List<string>();
        static ZabbixStore<ZabbixValue> Memory(List<string> built, string which, ZabbixStorePolicy<ZabbixValue> policy, ZabbixStoreServices services)
        {
            built.Add(which);
            return new ZabbixMemoryStore<ZabbixValue>(policy, services);
        }
        await using ZabbixPushAgent replaced = ZabbixPushAgent.CreateBuilder("edge").AddServer(new DnsEndPoint("127.0.0.1", 10061))
            .UseStore((policy, services) => Memory(built, "first", policy, services))
            .UseStore((provider, policy, services) => Memory(built, "second", policy, services))
            .Build();
        await using ZabbixPushAgent memory = ZabbixPushAgent.CreateBuilder("edge").AddServer(new DnsEndPoint("127.0.0.1", 10061))
            .UseStore((policy, services) => Memory(built, "dropped", policy, services))
            .UseMemoryStore()
            .Build();

        await replaced.StartAsync(TestContext.Current.CancellationToken);
        await memory.StartAsync(TestContext.Current.CancellationToken);

        Assert.Equal(["second"], built);
        Assert.Null(memory.Options.Storage);
    }

    [Fact]
    public async Task The_entity_framework_store_is_one_table_per_destination_and_a_later_use_replaces_it()
    {
        string file = Path.Combine(Path.GetTempPath(), $"zbx-agent-store-{Guid.NewGuid():N}.db");
        try
        {
            await using (ZabbixPushAgent agent = ZabbixPushAgent.CreateBuilder("edge").AddServer(new DnsEndPoint("127.0.0.1", 10061))
                .UseEntityFrameworkStore(db => db.UseSqlite($"Data Source={file};Pooling=False"))
                .Build())
            await using (ServiceProvider none = new ServiceCollection().BuildServiceProvider())
            await using (ZabbixStore<ZabbixValue> store = agent.Options.Storage!(none, new ZabbixStorePolicy<ZabbixValue>(), new ZabbixStoreServices { Name = "edge" }))
            {
                Assert.StartsWith("zbx_store_edge_", Assert.IsType<ZabbixEntityFrameworkStore<ZabbixValue>>(store).Table, StringComparison.Ordinal);
            }

            using ZabbixPushAgent memory = ZabbixPushAgent.CreateBuilder("edge").AddServer(new DnsEndPoint("127.0.0.1", 10061))
                .UseEntityFrameworkStore(db => db.UseSqlite($"Data Source={file};Pooling=False"))
                .UseMemoryStore()
                .Build();
            Assert.Null(memory.Options.Storage);
        }
        finally
        {
            File.Delete(file);
        }
    }

    [Fact]
    public async Task An_agent_s_default_store_counts_on_the_host_s_meter_factory()
    {
        var services = new ServiceCollection();
        services.AddZabbixPushAgent("edge").AddServer(new DnsEndPoint("127.0.0.1", 10061));
        await using ServiceProvider provider = services.BuildValidated();
        using var pending = new MetricCollector<long>(provider.GetRequiredService<IMeterFactory>(), ZabbixTelemetryNames.Store, "zabbix.store.pending");

        IHostedLifecycleService hosted = await StartingAsync(provider);
        pending.RecordObservableInstruments();

        Assert.Contains(pending.GetMeasurementSnapshot(), sample => (string?)sample.Tags["zabbix.store.name"] == "edge");
        await StoppedAsync(hosted);
    }

    [Fact]
    public async Task An_active_agent_s_store_counts_on_the_host_s_meter_factory()
    {
        var services = new ServiceCollection();
        services.AddZabbixActiveAgent("edge").AddServer(new DnsEndPoint("127.0.0.1", 10061));
        await using ServiceProvider provider = services.BuildValidated();
        using var pending = new MetricCollector<long>(provider.GetRequiredService<IMeterFactory>(), ZabbixTelemetryNames.Store, "zabbix.store.pending");

        IHostedLifecycleService hosted = await StartingAsync(provider);
        pending.RecordObservableInstruments();

        Assert.Contains(pending.GetMeasurementSnapshot(), sample => (string?)sample.Tags["zabbix.store.name"] == "edge");
        await StoppedAsync(hosted);
    }

    [Fact]
    public async Task A_proxy_s_own_store_gets_the_proxy_s_policy_and_the_host_s_services()
    {
        (ZabbixStorePolicy<ZabbixValue> Policy, ZabbixStoreServices Services)? built = null;
        var services = new ServiceCollection();
        services.AddZabbixPassiveProxy("proxy-01")
            .AddTrapper(0, o => o.Address = "127.0.0.1")
            .AllowPeer(System.Net.IPAddress.Loopback)
            .WithVersion("7.4.15")
            .Configure(o => o.MaxItems = 9)
            .UseStore((policy, store) =>
            {
                built = (policy, store);
                return new ZabbixMemoryStore<ZabbixValue>(policy, store);
            });
        await using ServiceProvider provider = services.BuildValidated();

        IHostedLifecycleService hosted = await StartingAsync(provider);

        Assert.NotNull(built);
        Assert.Equal(9, built.Value.Policy.MaxItems);
        Assert.NotNull(built.Value.Policy.Admit);                                                // the proxy's admission rule travels with it
        Assert.Equal("proxy-01", built.Value.Services.Name);
        Assert.Same(provider.GetRequiredService<IMeterFactory>(), built.Value.Services.Meters);
        await StoppedAsync(hosted);
    }
}
