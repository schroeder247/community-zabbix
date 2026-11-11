// This Source Code Form is subject to the terms of the Mozilla Public
// License, v. 2.0. Copyright (c) 2026 schroeder247. SPDX-License-Identifier: MPL-2.0
using System.Diagnostics.Metrics;
using System.Globalization;
using System.Net;
using System.Text.Json;
using Community.Zabbix.Diagnostics;
using Community.Zabbix.Hosting;
using Community.Zabbix.Protocol;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Diagnostics.Metrics.Testing;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Options;

namespace Community.Zabbix.Tests;

/// <summary>
/// <c>ServerActive=a;b,c</c> as <c>zbx_set_data_destination_hosts</c> reads it (<c>cfg.c</c> 853-908): <c>,</c>
/// separates independent destinations, <c>;</c> the HA nodes of one, an address named twice is refused — and an agent
/// sends every value to every destination with its own store and accounting.
/// </summary>
public sealed class ServerActiveTests
{
    [Theory]
    [InlineData("zbx1", new[] { "zbx1:10051" })]
    [InlineData("zbx1;zbx2:10052", new[] { "zbx1:10051;zbx2:10052" })]
    [InlineData("zbx1;zbx2,proxy-b", new[] { "zbx1:10051;zbx2:10051", "proxy-b:10051" })]
    [InlineData("[::1]:10060,10.0.0.1;fe80::1", new[] { "[::1]:10060", "10.0.0.1:10051;[fe80::1]:10051" })]
    public void Commas_separate_destinations_and_semicolons_the_nodes_of_one(string text, string[] destinations)
    {
        ZabbixServerActive serverActive = ZabbixServerActive.Parse(text, CultureInfo.InvariantCulture);

        Assert.Equal(destinations, serverActive.Select(destination => string.Join(';', destination)));
        Assert.Equal(serverActive, ZabbixServerActive.Parse(serverActive.ToString(), CultureInfo.InvariantCulture));   // it formats as Zabbix writes it
    }

    [Theory]
    [InlineData("zbx1;zbx1", "specified more than once")]
    [InlineData("zbx1,zbx1:10051", "specified more than once")]                          // the same address in two destinations
    [InlineData("zbx1;;zbx2", "is invalid")]
    [InlineData("zbx1,", "is invalid")]
    [InlineData("a b", "is invalid")]
    [InlineData("", "is invalid")]
    public void A_value_upstream_refuses_is_refused_in_upstreams_words(string text, string reason)
    {
        Assert.False(ZabbixServerActive.TryParse(text, CultureInfo.InvariantCulture, out _, out string? error));
        Assert.Contains(reason, error, StringComparison.Ordinal);
        Assert.StartsWith("error parsing the \"ServerActive\" parameter", error, StringComparison.Ordinal);
    }

    [Fact]
    public void The_builders_verbs_add_an_ha_node_or_a_destination()
    {
        ZabbixServerActive serverActive = ZabbixServerActive.Empty
            .WithFailover(new ZabbixServerNode("zbx1"))
            .WithFailover(new ZabbixServerNode("zbx2"))
            .WithDestination([new ZabbixServerNode("proxy-b")]);

        Assert.Equal("zbx1:10051;zbx2:10051,proxy-b:10051", serverActive.ToString());
        Assert.Throws<ArgumentException>(() => serverActive.WithFailover(new ZabbixServerNode("zbx1")));
    }

    [Fact]
    public void Each_AddServer_is_one_destination_and_its_nodes_are_its_ha_cluster()
    {
        var services = new ServiceCollection();
        services.AddZabbixPushAgent("edge").AddServer(new DnsEndPoint("zbx1", 10051), new DnsEndPoint("zbx2", 10051)).AddServer("proxy-b").AddServer("dr:10052");
        services.AddZabbixActiveAgent("active").AddServer(new DnsEndPoint("zbx1", 10052)).AddServer("zbx2;zbx3");
        services.AddZabbixActiveProxy("proxy").WithServer(new DnsEndPoint("zbx1", 10051), new DnsEndPoint("zbx2", 10051)).AllowPeer(IPAddress.Loopback);
        using ServiceProvider provider = services.BuildServiceProvider();

        Assert.Equal("zbx1:10051;zbx2:10051,proxy-b:10051,dr:10052", provider.GetRequiredService<IOptionsMonitor<ZabbixPushAgentOptions>>().Get("edge").ServerActive);
        Assert.Equal("zbx1:10052,zbx2:10051;zbx3:10051", provider.GetRequiredService<IOptionsMonitor<ZabbixActiveAgentOptions>>().Get("active").ServerActive);
        Assert.Equal("zbx1:10051;zbx2:10051", provider.GetRequiredService<IOptionsMonitor<ZabbixProxyOptions>>().Get("proxy").Server);   // one server, as an HA cluster
    }

    [Fact]
    public void Add_server_appends_a_destination_after_the_ones_set_before_it()
    {
        IConfiguration section = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?> { ["ServerActive"] = "zbx-0;zbx-00" }).Build();

        using ZabbixPushAgent agent = ZabbixPushAgent.CreateBuilder("edge").Bind(section).AddServer("zbx-1").Build();

        Assert.Equal("zbx-0;zbx-00,zbx-1:10051", agent.Options.ServerActive);
        Assert.Equal(2, ZabbixEndPoint.ParseServerActive(agent.Options.ServerActive!).Count);
    }

    [Theory]
    [InlineData("zbx-1,zbx-2")]       // two destinations are two calls
    [InlineData("zbx-1;zbx-1")]       // a node named twice
    [InlineData("a b")]
    [InlineData(" ")]
    public void Add_server_takes_one_destination_and_refuses_anything_else_where_it_is_passed(string server)
    {
        Assert.Equal("server", Assert.ThrowsAny<ArgumentException>(() => ZabbixPushAgent.CreateBuilder("edge").AddServer(server)).ParamName);
        Assert.Equal("server", Assert.ThrowsAny<ArgumentException>(() => new ServiceCollection().AddZabbixActiveAgent("edge").AddServer(server)).ParamName);
    }

    [Fact]
    public void Add_server_refuses_no_node_and_a_node_named_twice_where_they_are_passed()
    {
        Assert.Equal("nodes", Assert.Throws<ArgumentException>(() => ZabbixActiveAgent.CreateBuilder("edge").AddServer()).ParamName);
        Assert.Equal("nodes", Assert.Throws<ArgumentException>(() => ZabbixPushAgent.CreateBuilder("edge").AddServer(new DnsEndPoint("zbx1", 10051), new DnsEndPoint("zbx1", 10051))).ParamName);
        Assert.Equal("nodes", Assert.Throws<ArgumentException>(() => ZabbixPushAgent.CreateBuilder("edge").AddServer(new DnsEndPoint("zbx1", 0))).ParamName);
    }

    [Fact]
    public async Task Two_calls_are_two_destinations_each_with_a_status_of_its_own()
    {
        await using ZabbixPushAgent agent = ZabbixPushAgent.CreateBuilder("edge")
            .AddServer(new DnsEndPoint("127.0.0.1", FramesTests.ClosedPort()), new DnsEndPoint("127.0.0.2", FramesTests.ClosedPort()))
            .AddServer(new DnsEndPoint("127.0.0.3", FramesTests.ClosedPort()))
            .Build();

        await agent.StartAsync(TestContext.Current.CancellationToken);

        Assert.Equal(2, agent.Statuses.Count);
        Assert.Contains(";127.0.0.2:", agent.Statuses[0].Name, StringComparison.Ordinal);     // the first destination is the HA pair
        Assert.Contains("127.0.0.3:", agent.Statuses[1].Name, StringComparison.Ordinal);
    }

    [Fact]
    public void An_agent_s_server_active_that_does_not_parse_stops_the_host_at_start()
    {
        var services = new ServiceCollection();
        services.AddZabbixPushAgent("edge").AddServer("zbx1").AddServer("zbx1");
        using ServiceProvider provider = services.BuildValidated();

        OptionsValidationException failure = Assert.Throws<OptionsValidationException>(() => provider.GetRequiredService<IOptionsMonitor<ZabbixPushAgentOptions>>().Get("edge"));

        Assert.Contains(failure.Failures, message => message.Contains("specified more than once", StringComparison.Ordinal));
    }

    [Fact]
    public async Task Every_destination_gets_every_value_and_counts_its_own_deliveries()
    {
        var (first, firstReceived) = MockTrapper.Collect(3);
        var (second, secondReceived) = MockTrapper.Collect(3);
        var services = new ServiceCollection();
        int n = 0;
        services.AddZabbixPushAgent("edge")
            .AddPoll(TimeSpan.FromMilliseconds(20), _ => new ValueTask<IEnumerable<ZabbixValue>>([new ZabbixValue("web01", "cpu", (++n).ToString(CultureInfo.InvariantCulture), DateTimeOffset.UnixEpoch)]))
            .AddServer(new DnsEndPoint("127.0.0.1", first))
            .AddServer(new DnsEndPoint("127.0.0.1", second))
            .Configure(options => { options.Send.BatchSize = 1; options.Send.DrainTimeout = TimeSpan.FromMilliseconds(200); });
        using ServiceProvider provider = services.BuildValidated();
        IHostedService hosted = provider.GetServices<IHostedService>().Single();
        ZabbixPushAgent host = provider.GetRequiredKeyedService<ZabbixPushAgent>("edge");

        await hosted.StartAsync(TestContext.Current.CancellationToken);
        int toFirst = await firstReceived.WaitAsync(TimeSpan.FromSeconds(10), TestContext.Current.CancellationToken);
        int toSecond = await secondReceived.WaitAsync(TimeSpan.FromSeconds(10), TestContext.Current.CancellationToken);
        await hosted.StopAsync(TestContext.Current.CancellationToken);

        Assert.True(toFirst >= 3 && toSecond >= 3);
        Assert.Equal(2, host.Statuses.Count);
        Assert.All(host.Statuses, status => Assert.True(status.Delivered >= 3));
        Assert.Equal($"edge[127.0.0.1:{second}]", host.Statuses[1].Name);
    }

    [Fact]
    public void Adding_a_zabbix_agent_registers_the_wire_s_telemetry_once_on_the_host_s_meter_factory()
    {
        var services = new ServiceCollection();
        services.AddZabbixClient("prod").Configure(options => options.Endpoint = new Uri("https://zbx.example/api_jsonrpc.php"));
        services.AddZabbixPushAgent("edge").AddServer("zbx1");
        services.AddZabbixActiveAgent("active").AddServer("zbx1");
        using ServiceProvider provider = services.BuildValidated();
        IMeterFactory meters = provider.GetRequiredService<IMeterFactory>();
        using var redirects = new MetricCollector<long>(meters, ZabbixTelemetryNames.Wire, ZabbixWireTelemetry.RedirectInstrument);

        Assert.Single(services, descriptor => descriptor.ServiceType == typeof(ZabbixWireTelemetry));
        Assert.Single(services, descriptor => descriptor.ServiceType == typeof(ZabbixAgentTelemetry));
        Assert.Same(provider.GetRequiredService<ZabbixWireTelemetry>(), provider.GetRequiredService<ZabbixWireTelemetry>());
        Assert.NotNull(redirects.Instrument);                                            // the wire's meter is the host's factory's
    }

    [Fact]
    public async Task Each_active_destination_asks_for_its_own_items_with_its_own_session()
    {
        var first = new List<JsonElement>();
        var second = new List<JsonElement>();
        await using var one = new FramesTests.Peer((stream, frame, stopping) => Answer(first, stream, frame, stopping));
        await using var two = new FramesTests.Peer((stream, frame, stopping) => Answer(second, stream, frame, stopping));
        var services = new ServiceCollection();
        services.AddZabbixActiveAgent("active").AddServer(new DnsEndPoint(one.Node.Host, one.Node.Port)).AddServer(new DnsEndPoint(two.Node.Host, two.Node.Port))
            .Configure(options => { options.HeartbeatInterval = TimeSpan.Zero; options.RefreshInterval = TimeSpan.FromDays(1); });
        await using ServiceProvider provider = services.BuildValidated();
        var hosted = (IHostedLifecycleService)provider.GetServices<IHostedService>().Single();

        await hosted.StartingAsync(TestContext.Current.CancellationToken);
        await hosted.StoppingAsync(TestContext.Current.CancellationToken);
        await hosted.StopAsync(TestContext.Current.CancellationToken);
        await hosted.StoppedAsync(TestContext.Current.CancellationToken);

        string sessionOne = Assert.Single(first).GetProperty("session").GetString()!;
        string sessionTwo = Assert.Single(second).GetProperty("session").GetString()!;
        Assert.NotEqual(sessionOne, sessionTwo);

        static Task Answer(List<JsonElement> seen, System.Net.Sockets.NetworkStream stream, ReadOnlyMemory<byte> frame, CancellationToken stopping)
        {
            lock (seen) seen.Add(JsonDocument.Parse(frame).RootElement.Clone());
            return ZabbixHeader.WriteAsync(stream, """{"response":"success","data":[],"config_revision":1}"""u8.ToArray(), stopping);
        }
    }
}
