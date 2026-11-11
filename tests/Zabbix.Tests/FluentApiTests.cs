// This Source Code Form is subject to the terms of the Mozilla Public
// License, v. 2.0. Copyright (c) 2026 schroeder247. SPDX-License-Identifier: MPL-2.0
using System.Net;
using System.Reflection;
using System.Text.Json;
using Community.Zabbix.Proxy;
using Community.Zabbix.Storage.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Http.Resilience;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace Community.Zabbix.Tests;

/// <summary>The API as a caller writes it: no <c>.Api</c>, parameters filled in a lambda, no wrapper for what's only an id.</summary>
public sealed class FluentApiTests
{
    private sealed record Fluent(ZabbixClient Zabbix, ScriptedApi Api, ServiceProvider Provider) : IDisposable
    {
        public void Dispose() => Provider.Dispose();
    }

    private static Fluent Build(Func<ScriptedApi, ScriptedApi> script)
    {
        var services = new ServiceCollection();
        services.AddSingleton(_ => script(new ScriptedApi()));
        services.AddZabbixClient().Configure(options => { options.Endpoint = new Uri("https://zbx.example/api_jsonrpc.php"); options.ApiToken = "tok"; });
        services.AddHttpClient(ZabbixHttp.GetClientName()).ConfigurePrimaryHttpMessageHandler(provider => provider.GetRequiredService<ScriptedApi>());
        ServiceProvider provider = services.BuildValidated();
        return new Fluent(provider.GetRequiredService<ZabbixClient>(), provider.GetRequiredService<ScriptedApi>(), provider);
    }

    [Fact]
    public async Task A_get_s_parameters_fill_in_a_lambda_and_only_what_is_set_is_sent()
    {
        using Fluent fluent = Build(api => api.On("host.get", "[]"));

        await fluent.Zabbix.Hosts.GetAsync(
            q =>
            {
                q.Filter.Status = [ZabbixHostStatus.Enabled];
                q.Search.VisibleName = "web";
                q.Limit = 10;
            },
            TestContext.Current.CancellationToken);

        JsonElement sent = fluent.Api.ParamsOf("host.get").Single();
        Assert.Equal("0", sent.GetProperty("filter").GetProperty("status")[0].ToString());
        Assert.Equal("web", sent.GetProperty("search").GetProperty("name").GetString());
        Assert.Equal(10, sent.GetProperty("limit").GetInt32());
        Assert.False(sent.TryGetProperty("output", out _));
    }

    [Fact]
    public async Task A_get_with_nothing_set_sends_no_filter_and_no_search()
    {
        using Fluent fluent = Build(api => api.On("host.get", "[]"));

        await fluent.Zabbix.Hosts.GetAsync(q => q.Limit = 1, TestContext.Current.CancellationToken);

        JsonElement sent = fluent.Api.ParamsOf("host.get").Single();
        Assert.False(sent.TryGetProperty("filter", out _));
        Assert.False(sent.TryGetProperty("search", out _));
    }

    [Fact]
    public async Task A_field_the_sdk_does_not_name_is_set_by_its_wire_name()
    {
        using Fluent fluent = Build(api => api.On("host.get", "[]"));

        await fluent.Zabbix.Hosts.GetAsync(q => q.Filter["a_newer_field"] = "x", TestContext.Current.CancellationToken);

        Assert.Equal("x", fluent.Api.ParamsOf("host.get").Single().GetProperty("filter").GetProperty("a_newer_field").GetString());
    }

    [Fact]
    public async Task A_count_fills_its_parameters_in_a_lambda_too()
    {
        using Fluent fluent = Build(api => api.On("host.get", "\"7\""));

        long count = await fluent.Zabbix.Hosts.CountAsync(q => q.Search.VisibleName = "web", TestContext.Current.CancellationToken);

        Assert.Equal(7, count);
        JsonElement sent = fluent.Api.ParamsOf("host.get").Single();
        Assert.True(sent.GetProperty("countOutput").GetBoolean());
        Assert.Equal("web", sent.GetProperty("search").GetProperty("name").GetString());
    }

    [Fact]
    public async Task A_create_takes_what_it_requires_and_an_id_stands_for_its_request()
    {
        using Fluent fluent = Build(api => api.On("host.create", """{"hostids":["10084"]}"""));

        ulong id = await fluent.Zabbix.Hosts.CreateAsync("web-02", [42UL], TestContext.Current.CancellationToken);

        Assert.Equal(10084UL, id);
        JsonElement sent = fluent.Api.ParamsOf("host.create").Single();
        Assert.Equal("web-02", sent.GetProperty("host").GetString());
        Assert.Equal("42", sent.GetProperty("groups")[0].GetProperty("groupid").ToString());
    }

    [Fact]
    public async Task A_request_can_name_its_groups_by_id()
    {
        using Fluent fluent = Build(api => api.On("host.create", """{"hostids":["1"]}"""));

        await fluent.Zabbix.Hosts.CreateAsync(new("web-03", [42UL, 43UL]) { VisibleName = "Web 3" }, TestContext.Current.CancellationToken);

        JsonElement sent = fluent.Api.ParamsOf("host.create").Single();
        Assert.Equal(2, sent.GetProperty("groups").GetArrayLength());
        Assert.Equal("Web 3", sent.GetProperty("name").GetString());
    }

    [Fact]
    public async Task An_id_indexer_reads_and_deletes_one_object_and_makes_no_call_until_asked()
    {
        using Fluent fluent = Build(api => api
            .On("item.get", """[{"itemid":"7","name":"CPU"}]""")
            .On("item.delete", """{"itemids":["7"]}"""));

        ZabbixUpdatableObjectNode<ZabbixItem, ZabbixItemUpdateRequest> node = fluent.Zabbix.Items[7];
        Assert.Empty(fluent.Api.ParamsOf("item.get"));

        ZabbixItem item = await node.GetAsync(TestContext.Current.CancellationToken);
        Assert.Equal("CPU", item.Name);
        Assert.Equal("7", fluent.Api.ParamsOf("item.get").Single().GetProperty("itemids")[0].ToString());

        await node.DeleteAsync(TestContext.Current.CancellationToken);
        Assert.Equal("7", fluent.Api.ParamsOf("item.delete").Single()[0].ToString());
    }

    [Fact]
    public async Task An_id_with_no_object_is_a_not_found_when_it_must_be_there_and_null_when_it_may_not_be()
    {
        using Fluent fluent = Build(api => api.On("item.get", "[]"));

        await Assert.ThrowsAsync<ZabbixNotFoundException>(() => fluent.Zabbix.Items[7].GetAsync(TestContext.Current.CancellationToken));
        Assert.Null(await fluent.Zabbix.Items[7].FindAsync(TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task A_name_indexer_reads_and_deletes_one_host()
    {
        using Fluent fluent = Build(api => api
            .On("host.get", """[{"hostid":"10084","host":"web-01"}]""")
            .On("host.delete", """{"hostids":["10084"]}"""));

        ZabbixHost host = await fluent.Zabbix.Hosts["web-01"].GetAsync(TestContext.Current.CancellationToken);
        Assert.Equal(10084UL, host.HostId);
        Assert.Equal("web-01", fluent.Api.ParamsOf("host.get").Single().GetProperty("filter").GetProperty("host").GetString());

        await fluent.Zabbix.Hosts["web-01"].DeleteAsync(TestContext.Current.CancellationToken);
        Assert.Equal("10084", fluent.Api.ParamsOf("host.delete").Single()[0].ToString());
    }

    [Fact]
    public async Task A_host_of_no_such_name_is_null_to_find_and_not_found_to_read_or_delete()
    {
        using Fluent fluent = Build(api => api.On("host.get", "[]"));

        Assert.Null(await fluent.Zabbix.Hosts["none"].FindAsync(TestContext.Current.CancellationToken));
        await Assert.ThrowsAsync<ZabbixNotFoundException>(() => fluent.Zabbix.Hosts["none"].GetAsync(TestContext.Current.CancellationToken));
        await Assert.ThrowsAsync<ZabbixNotFoundException>(() => fluent.Zabbix.Hosts["none"].DeleteAsync(TestContext.Current.CancellationToken));
        Assert.Empty(fluent.Api.ParamsOf("host.delete"));
    }

    [Fact]
    public void A_name_indexer_on_a_client_built_for_mocking_says_so()
        => Assert.Throws<InvalidOperationException>(() => new FakeHosts()["web-01"]);

    [Fact]
    public void A_copy_of_a_filter_set_by_wire_name_does_not_share_its_fields()
    {
        var first = new ZabbixHostGetFilter { Status = [ZabbixHostStatus.Enabled] };
        first["a"] = "1";

        ZabbixHostGetFilter second = first with { };
        second["a"] = "2";

        Assert.Equal("1", first["a"].ToString());
        Assert.Equal("2", second["a"].ToString());
        Assert.NotEqual(first, second);
    }

    [Fact]
    public void Two_filters_are_equal_when_they_send_the_same_fields_whichever_way_they_were_set()
    {
        var named = new ZabbixHostGetSearch { VisibleName = "web" };
        var byWire = new ZabbixHostGetSearch { ["name"] = "web" };

        Assert.Equal(named, byWire);
        Assert.Equal(named.GetHashCode(), byWire.GetHashCode());
        Assert.Contains("name", named.ToString(), StringComparison.Ordinal);
    }

    [Fact]
    public Task The_lambda_cannot_be_null()
        => Assert.ThrowsAsync<ArgumentNullException>(() => new FakeHosts().GetAsync((Action<ZabbixHostGetParameters>)null!, TestContext.Current.CancellationToken));

    [Fact]
    public async Task A_double_that_overrides_the_get_serves_the_lambda_and_every_other_overload()
    {
        var hosts = new FakeHosts();

        await hosts.GetAsync(q => q.Limit = 3, TestContext.Current.CancellationToken);
        Assert.Equal(3, hosts.Last?.Limit);
        await hosts.GetAsync(TestContext.Current.CancellationToken);
        Assert.Null(hosts.Last?.Limit);
    }

    // ----- the wire client's builder: one set of verbs, on its own container or the application's ----------------------

    [Fact]
    public void A_wire_builder_sets_what_a_wire_client_reads()
    {
        using ZabbixWireClient wire = ZabbixWireClient.CreateBuilder()
            .WithServer("zbx-1;zbx-2:10052")
            .WithToken("token")
            .WithPsk("web-01", Convert.FromHexString("0123456789ABCDEF0123456789ABCDEF"))
            .WithTimeout(TimeSpan.FromSeconds(5))
            .WithCompression()
            .WithBatchSize(10)
            .WithCheckTimeout(TimeSpan.FromSeconds(1))
            .UseTimeProvider(TimeProvider.System)
            .Build();

        Assert.Equal("zbx-1:10051;zbx-2:10052", wire.Options.Server);
        Assert.Equal("token", wire.Options.SessionId);
        Assert.Equal(ZabbixTlsMode.PreSharedKey, wire.Options.Connection.Tls?.Mode);
        Assert.Equal("web-01", wire.Options.Connection.Tls?.PskIdentity);
        Assert.Equal("0123456789abcdef0123456789abcdef", wire.Options.Connection.Tls?.Psk);   // the bytes, as the hex a TLSPSKFile holds
        Assert.Equal(TimeSpan.FromSeconds(5), wire.Options.Connection.Timeout);
        Assert.True(wire.Options.Connection.Compress);
        Assert.Equal(10, wire.Options.BatchSize);
        Assert.Equal(TimeSpan.FromSeconds(1), wire.Options.CheckTimeout);
    }

    [Fact]
    public void A_wire_builder_takes_endpoints_and_certificates()
    {
        using ZabbixWireClient wire = ZabbixWireClient.CreateBuilder()
            .WithServer(new System.Net.DnsEndPoint("zbx-1", 10052), new System.Net.DnsEndPoint("::1", 10051))
            .WithCertificate("c.pem", "k.pem", "ca.pem")
            .Build();

        Assert.Equal("zbx-1:10052;[::1]:10051", wire.Options.Server);
        Assert.Equal(("c.pem", "k.pem", "ca.pem"), (wire.Options.Connection.Tls?.CertificateFile, wire.Options.Connection.Tls?.KeyFile, wire.Options.Connection.Tls?.CaFile));
    }

    [Fact]
    public void With_server_sets_the_one_server_and_a_later_call_replaces_it()
    {
        using ZabbixWireClient wire = ZabbixWireClient.CreateBuilder()
            .Configure(options => options.Server = "zbx-0")
            .WithServer("zbx-1")
            .WithServer(new System.Net.DnsEndPoint("zbx-2", 10052), new System.Net.DnsEndPoint("zbx-3", 10052))
            .Build();

        Assert.Equal("zbx-2:10052;zbx-3:10052", wire.Options.Server);    // one HA cluster, in failover order
    }

    [Fact]
    public void With_server_takes_the_nodes_of_one_cluster_in_zabbix_s_spelling()
    {
        using ZabbixWireClient wire = ZabbixWireClient.CreateBuilder().WithServer("zbx-1;zbx-2:10052").Build();

        Assert.Equal("zbx-1:10051;zbx-2:10052", wire.Options.Server);
    }

    [Theory]
    [InlineData("zbx-1,zbx-2")]      // two destinations are two clients
    [InlineData("zbx-1;zbx-1")]      // a node named twice
    [InlineData("a b")]
    [InlineData(" ")]
    public void With_server_takes_one_server_and_refuses_anything_else_where_it_is_passed(string server)
        => Assert.Equal("server", Assert.ThrowsAny<ArgumentException>(() => ZabbixWireClient.CreateBuilder().WithServer(server)).ParamName);

    [Fact]
    public void With_server_refuses_no_node_or_a_node_named_twice_where_they_are_passed()
    {
        Assert.Equal("nodes", Assert.Throws<ArgumentException>(() => ZabbixWireClient.CreateBuilder().WithServer(Array.Empty<System.Net.DnsEndPoint>())).ParamName);
        Assert.Equal("nodes", Assert.Throws<ArgumentException>(() => ZabbixWireClient.CreateBuilder()
            .WithServer(new System.Net.DnsEndPoint("zbx-1", 10051), new System.Net.DnsEndPoint("zbx-1", 10051))).ParamName);
    }

    [Theory]
    [InlineData(15)]
    [InlineData(257)]
    public void A_pre_shared_key_of_the_wrong_length_is_refused_where_it_is_passed(int bytes)
        => Assert.Equal("key", Assert.Throws<ArgumentException>(() => ZabbixWireClient.CreateBuilder().WithPsk("web-01", new byte[bytes])).ParamName);

    [Fact]
    public void A_wire_builder_builds_once_and_what_it_built_does_not_change()
    {
        ZabbixWireClientBuilder builder = ZabbixWireClient.CreateBuilder().WithServer("zbx-server-01");
        using ZabbixWireClient wire = builder.Build();

        Assert.Equal("zbx-server-01:10051", wire.Options.Server);
        Assert.Throws<InvalidOperationException>(() => builder.Build().Dispose());
        Assert.Throws<InvalidOperationException>(() => builder.WithServer("zbx-server-02"));
        Assert.Throws<InvalidOperationException>(() => builder.UseTimeProvider(TimeProvider.System));
    }

    [Fact]
    public void A_wire_builder_checks_the_options_with_the_rules_a_host_checks_at_start()
    {
        ZabbixWireClientBuilder builder = ZabbixWireClient.CreateBuilder().WithServer("zbx-server-01").WithBatchSize(0);

        InvalidOperationException refused = Assert.Throws<InvalidOperationException>(() => builder.Build().Dispose());

        Assert.Contains(nameof(ZabbixWireClientOptions.BatchSize), refused.Message, StringComparison.Ordinal);
        Assert.IsType<OptionsValidationException>(refused.InnerException);
    }

    [Fact]
    public void A_wire_builder_binds_configuration_in_order_with_its_other_verbs()
    {
        IConfiguration section = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["Server"] = "zbx-1",
            ["BatchSize"] = "50",
            ["Connection:Timeout"] = "00:00:07",
        }).Build();

        using ZabbixWireClient wire = ZabbixWireClient.CreateBuilder()
            .Bind(section)
            .WithServer("zbx-2")
            .WithBatchSize(20)
            .Build();

        Assert.Equal("zbx-2:10051", wire.Options.Server);                // the verb after the section replaces its server
        Assert.Equal(20, wire.Options.BatchSize);
        Assert.Equal(TimeSpan.FromSeconds(7), wire.Options.Connection.Timeout);
    }

    [Fact]
    public void The_same_verbs_set_up_a_named_wire_client_in_an_application_s_container()
    {
        var services = new ServiceCollection();
        IZabbixWireClientBuilder prod = services.AddZabbixWireClient("prod")
            .WithServer(new System.Net.DnsEndPoint("zbx-1", 10051), new System.Net.DnsEndPoint("zbx-2", 10052))
            .WithToken("token")
            .WithBatchSize(10);
        services.AddZabbixWireClient("lab").WithServer("zbx-lab");
        using ServiceProvider provider = services.BuildValidated();
        IZabbixWireClientFactory clients = provider.GetRequiredService<IZabbixWireClientFactory>();

        ZabbixWireClient wire = clients.Create("prod");

        Assert.Equal("prod", prod.Name);
        Assert.Equal(("zbx-1:10051;zbx-2:10052", "token", 10), (wire.Options.Server, wire.Options.SessionId, wire.Options.BatchSize));
        Assert.Same(wire, provider.GetRequiredKeyedService<ZabbixWireClient>("prod"));
        Assert.Equal("zbx-lab:10051", provider.GetRequiredKeyedService<ZabbixWireClient>("lab").Options.Server);
        Assert.False(clients.TryCreate("other", out _));
        Assert.Throws<KeyNotFoundException>(() => clients.Create("other"));
        Assert.Null(provider.GetService<ZabbixWireClient>());     // only the unnamed client is the plain service
    }

    [Fact]
    public void The_unnamed_wire_client_is_the_plain_service_and_a_name_registered_again_is_the_same_client()
    {
        var services = new ServiceCollection();
        services.AddZabbixWireClient().WithServer("zbx-1");
        services.AddZabbixWireClient().WithServer("zbx-2");
        using ServiceProvider provider = services.BuildValidated();

        ZabbixWireClient wire = provider.GetRequiredService<ZabbixWireClient>();

        Assert.Same(wire, provider.GetRequiredService<IZabbixWireClientFactory>().Create(Options.DefaultName));
        Assert.Equal("zbx-2:10051", wire.Options.Server);                 // the second registration's verb replaces the first's server
        Assert.Single(services, descriptor => descriptor.IsKeyedService && descriptor.ServiceType == typeof(ZabbixWireClient));
    }

    [Fact]
    public void A_registered_wire_client_s_options_are_checked_when_the_host_starts()
    {
        var services = new ServiceCollection();
        services.AddZabbixWireClient("prod").WithServer("zbx-1").WithBatchSize(0);
        using ServiceProvider provider = services.BuildValidated();

        OptionsValidationException refused = Assert.Throws<OptionsValidationException>(() => provider.GetRequiredService<IStartupValidator>().Validate());

        Assert.Equal("prod", refused.OptionsName);
    }

    [Fact]
    public void Disposing_a_built_wire_client_disposes_its_container()
    {
        using var loggers = new LoggingProbe();
        ZabbixWireClientBuilder builder = ZabbixWireClient.CreateBuilder();
        builder.Services.AddSingleton<ILoggerFactory>(_ => loggers);   // the container makes it, so the container disposes it
        ZabbixWireClient wire = builder.Build();

        Assert.False(loggers.Disposed);
        wire.Dispose();
        wire.Dispose();

        Assert.True(loggers.Disposed);
    }

    [Fact]
    public async Task A_built_wire_client_logs_through_the_logging_it_was_given_and_leaves_it_to_the_application()
    {
        using var loggers = new LoggingProbe();
        using (ZabbixWireClient wire = ZabbixWireClient.CreateBuilder().WithServer($"127.0.0.1:{FramesTests.ClosedPort()}").UseLogging(loggers).Build())
        {
            await Assert.ThrowsAsync<System.Net.Sockets.SocketException>(() => wire.SendAsync("web-01", new ZabbixItemKey<long>("app.requests"), 1, TestContext.Current.CancellationToken));
        }

        Assert.Contains(typeof(ZabbixWireClient).FullName, loggers.Categories);
        Assert.False(loggers.Disposed);
    }

    // ----- the agents' builders: one set of verbs, on their own container or the application's ------------------------

    private static readonly ZabbixItemKey<long> Pending = new("orders.pending");

    [Fact]
    public async Task Each_agent_s_builder_builds_the_agent_it_describes()
    {
        await using ZabbixPushAgent push = ZabbixPushAgent.CreateBuilder("web-01").AddServer("zbx-1").Configure(o => o.Send.BatchSize = 10).Build();
        await using ZabbixActiveAgent active = ZabbixActiveAgent.CreateBuilder("web-02").AddServer("zbx-1").Configure(o => o.HostMetadata = "Linux").Build();
        await using ZabbixPassiveAgent passive = ZabbixPassiveAgent.CreateBuilder("web-03").AllowPeer(IPAddress.Loopback).Build();

        Assert.Equal(("web-01", "web-01", "zbx-1:10051", 10), (push.Name, push.Options.HostName, push.Options.ServerActive, push.Options.Send.BatchSize));
        Assert.Equal(("web-02", "web-02", "Linux"), (active.Name, active.Options.HostName, active.Options.HostMetadata));
        Assert.Equal(("web-03", "web-03", "127.0.0.1"), (passive.Name, passive.Options.HostName, Assert.Single(passive.Options.AllowedPeers)));
    }

    [Fact]
    public void An_agent_s_builder_builds_once_and_what_it_built_does_not_change()
    {
        ZabbixActiveAgentBuilder builder = ZabbixActiveAgent.CreateBuilder("web-01").AddServer("zbx-1");
        using ZabbixActiveAgent agent = builder.Build();

        Assert.Throws<InvalidOperationException>(() => builder.Build().Dispose());
        Assert.Throws<InvalidOperationException>(() => builder.AddServer("zbx-2"));
        Assert.Throws<InvalidOperationException>(() => builder.AddItem(Pending, () => 1L));
        Assert.Throws<InvalidOperationException>(() => builder.UseTimeProvider(TimeProvider.System));
        Assert.Equal("zbx-1:10051", agent.Options.ServerActive);
    }

    [Fact]
    public void An_agent_s_builder_checks_the_options_with_the_rules_a_host_checks_at_start()
    {
        InvalidOperationException push = Assert.Throws<InvalidOperationException>(() => ZabbixPushAgent.CreateBuilder("web-01").Build().Dispose());
        InvalidOperationException active = Assert.Throws<InvalidOperationException>(() => ZabbixActiveAgent.CreateBuilder("web-01").AddServer("zbx-1")
            .Configure(o => o.HeartbeatInterval = TimeSpan.FromMilliseconds(1500)).Build().Dispose());
        InvalidOperationException passive = Assert.Throws<InvalidOperationException>(() => ZabbixPassiveAgent.CreateBuilder("web-01").Build().Dispose());

        Assert.Contains("ServerActive", push.Message, StringComparison.Ordinal);                // no server
        Assert.Contains("HeartbeatInterval", active.Message, StringComparison.Ordinal);
        Assert.Contains("AllowedPeers", passive.Message, StringComparison.Ordinal);             // no peers
        Assert.All([push, active, passive], refused => Assert.IsType<OptionsValidationException>(refused.InnerException));
    }

    [Fact]
    public void An_agent_s_builder_refuses_a_provider_whose_constructor_needs_what_its_container_lacks()
    {
        ZabbixActiveAgentBuilder builder = ZabbixActiveAgent.CreateBuilder("web-01").AddServer("zbx-1").AddItems<NeedsAReader>();

        InvalidOperationException refused = Assert.Throws<InvalidOperationException>(() => builder.Build().Dispose());

        Assert.IsType<AggregateException>(refused.InnerException);
        Assert.Contains("'web-01'", refused.Message, StringComparison.Ordinal);
        Assert.Contains(nameof(NeedsAReader), refused.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void The_name_is_the_agent_s_host_unless_configure_or_bind_sets_another()
    {
        IConfiguration section = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?> { ["Hostname"] = "bound", ["ServerActive"] = "zbx-1" }).Build();
        using ZabbixActiveAgent named = ZabbixActiveAgent.CreateBuilder("web-01").AddServer("zbx-1").Build();
        using ZabbixActiveAgent configured = ZabbixActiveAgent.CreateBuilder("web-01").AddServer("zbx-1").Configure(o => o.HostName = "other").Build();
        using ZabbixActiveAgent bound = ZabbixActiveAgent.CreateBuilder("web-01").Bind(section).Build();
        var services = new ServiceCollection();
        services.AddZabbixPushAgent("edge-01").AddServer("zbx-1");
        services.AddZabbixPassiveAgent("probe-01").AllowPeer(IPAddress.Loopback);
        services.AddZabbixActiveAgent("roaming-01").AddServer("zbx-1").Configure(o => o.HostName = "roamer");
        services.AddZabbixActiveAgent("roaming-01");                                             // a name registered again keeps what was set
        using ServiceProvider provider = services.BuildValidated();

        Assert.Equal(("web-01", "other", "bound"), (named.Options.HostName, configured.Options.HostName, bound.Options.HostName));
        Assert.Equal("web-01", configured.Name);                                                  // the name stays the agent's
        Assert.Equal("edge-01", provider.GetRequiredService<IOptionsMonitor<ZabbixPushAgentOptions>>().Get("edge-01").HostName);
        Assert.Equal("probe-01", provider.GetRequiredService<IOptionsMonitor<ZabbixPassiveAgentOptions>>().Get("probe-01").HostName);
        Assert.Equal("roamer", provider.GetRequiredService<IOptionsMonitor<ZabbixActiveAgentOptions>>().Get("roaming-01").HostName);
    }

    [Fact]
    public void The_same_verbs_set_up_an_agent_on_its_own_container_and_in_an_application_s()
    {
        static TBuilder Push<TBuilder>(TBuilder builder) where TBuilder : IZabbixPushAgentBuilder
            => builder.AddServer("zbx-1;zbx-2").AddServer(new DnsEndPoint("proxy-b", 10052)).AddPoll(Pending, TimeSpan.FromMinutes(1), () => 42L)
                .UseMemoryStore().Configure(o => o.Send.BatchSize = 10);
        static TBuilder Active<TBuilder>(TBuilder builder) where TBuilder : IZabbixActiveAgentBuilder
            => builder.AddServer("zbx-1;zbx-2").AddServer(new DnsEndPoint("proxy-b", 10052)).Configure(o => o.HostMetadata = "Linux")
                .AddItem(Pending, () => 42L).AddItem("app.version", _ => "2.4.1").AddItems(new ItemProviderTests.FixedItems("1", "app.fixed"));
        static TBuilder Passive<TBuilder>(TBuilder builder) where TBuilder : IZabbixPassiveAgentBuilder
            => builder.AllowPeer(IPNetwork.Parse("10.0.0.0/8")).AllowPeer(IPAddress.IPv6Loopback).AllowPeer("zbx.example")
                .AddItem(Pending, key => key.Parameters.Count).AddItem("app.version", _ => "2.4.1");

        using ZabbixPushAgent push = Push(ZabbixPushAgent.CreateBuilder("web-01")).Build();
        using ZabbixActiveAgent active = Active(ZabbixActiveAgent.CreateBuilder("web-01")).Build();
        using ZabbixPassiveAgent passive = Passive(ZabbixPassiveAgent.CreateBuilder("web-01")).Build();
        var services = new ServiceCollection();
        Push(services.AddZabbixPushAgent("web-01"));
        Active(services.AddZabbixActiveAgent("web-01"));
        Passive(services.AddZabbixPassiveAgent("web-02"));
        using ServiceProvider provider = services.BuildValidated();
        ZabbixPushAgent hostedPush = provider.GetRequiredKeyedService<ZabbixPushAgent>("web-01");
        ZabbixActiveAgent hostedActive = provider.GetRequiredKeyedService<ZabbixActiveAgent>("web-01");
        ZabbixPassiveAgent hostedPassive = provider.GetRequiredKeyedService<ZabbixPassiveAgent>("web-02");

        Assert.Equal("zbx-1:10051;zbx-2:10051,proxy-b:10052", push.Options.ServerActive);
        Assert.Equal((push.Options.ServerActive, push.Options.Send.BatchSize, push.Options.Sources.Count), (hostedPush.Options.ServerActive, hostedPush.Options.Send.BatchSize, hostedPush.Options.Sources.Count));
        Assert.Equal((active.Options.ServerActive, active.Options.HostMetadata), (hostedActive.Options.ServerActive, hostedActive.Options.HostMetadata));
        Assert.Equal(["app.fixed", "app.version", "orders.pending"], active.Items.Keys.Order(StringComparer.Ordinal));
        Assert.Equal(active.Items.Keys.Order(StringComparer.Ordinal), hostedActive.Items.Keys.Order(StringComparer.Ordinal));
        Assert.Equal(["10.0.0.0/8", "::1", "zbx.example"], passive.Options.AllowedPeers);
        Assert.Equal(passive.Options.AllowedPeers, hostedPassive.Options.AllowedPeers);
        Assert.Equal(passive.Items.Keys.Order(StringComparer.Ordinal), hostedPassive.Items.Keys.Order(StringComparer.Ordinal));
    }

    [Fact]
    public void Adding_a_provider_by_type_keeps_each_builder_s_own_type()
    {
        ZabbixActiveAgentBuilder active = ZabbixActiveAgent.CreateBuilder("web-01").AddItems<ItemProviderTests.QueueItems>();
        ZabbixPassiveAgentBuilder passive = ZabbixPassiveAgent.CreateBuilder("web-01").AddItems<ItemProviderTests.QueueItems>();
        var services = new ServiceCollection();
        IZabbixActiveAgentBuilder hostedActive = services.AddZabbixActiveAgent("web-01").AddItems<ItemProviderTests.QueueItems>().AddServer("zbx-1");
        IZabbixPassiveAgentBuilder hostedPassive = services.AddZabbixPassiveAgent("web-02").AddItems<ItemProviderTests.QueueItems>().AllowPeer(IPAddress.Loopback);
        using ZabbixActiveAgent built = active.AddServer("zbx-1").Build();
        using ServiceProvider provider = services.BuildValidated();

        Assert.True(built.Items.ContainsKey("app.queue.depth"));
        Assert.True(provider.GetRequiredKeyedService<ZabbixActiveAgent>("web-01").Items.ContainsKey("app.queue.depth"));
        Assert.True(provider.GetRequiredKeyedService<ZabbixPassiveAgent>("web-02").Items.ContainsKey("app.queue.depth"));
        Assert.Equal(("web-01", "web-01", "web-02"), (passive.Name, hostedActive.Name, hostedPassive.Name));
    }

    [Fact]
    public void An_active_and_a_passive_agent_of_one_name_answer_the_same_items()
    {
        var services = new ServiceCollection();
        services.AddZabbixActiveAgent("web-01").AddServer("zbx-1").AddItem(Pending, () => 42L);
        services.AddZabbixPassiveAgent("web-01").AllowPeer(IPAddress.Loopback).AddItem("app.version", _ => "2.4.1");
        using ServiceProvider provider = services.BuildValidated();

        string[] answered = ["app.version", "orders.pending"];
        Assert.Equal(answered, provider.GetRequiredKeyedService<ZabbixActiveAgent>("web-01").Items.Keys.Order(StringComparer.Ordinal));
        Assert.Equal(answered, provider.GetRequiredKeyedService<ZabbixPassiveAgent>("web-01").Items.Keys.Order(StringComparer.Ordinal));
    }

    [Fact]
    public async Task Disposing_a_built_agent_disposes_its_container_and_leaves_the_application_s_logging()
    {
        using var owned = new LoggingProbe();
        using var given = new LoggingProbe();
        ZabbixPushAgentBuilder builder = ZabbixPushAgent.CreateBuilder("web-01").AddServer("zbx-1");
        builder.Services.AddSingleton<ILoggerFactory>(_ => owned);                     // the container makes it, so the container disposes it
        ZabbixPushAgent agent = builder.Build();
        ZabbixActiveAgent active = ZabbixActiveAgent.CreateBuilder("web-01").AddServer("zbx-1").UseLogging(given).Build();

        Assert.False(owned.Disposed);
        await agent.DisposeAsync();
        await agent.DisposeAsync();
        await active.DisposeAsync();

        Assert.True(owned.Disposed);
        Assert.False(given.Disposed);
        Assert.Contains(typeof(ZabbixComponent).FullName, given.Categories);
    }

    private sealed class NeedsAReader(Uri reader) : ZabbixItemProvider
    {
        public override IReadOnlyList<ZabbixItemDefinition> Items { get; } = [new("app.read", "What the reader reads")];

        public override ValueTask<ZabbixAgentAnswer> GetValueAsync(ZabbixItemKey key, CancellationToken cancellationToken)
            => ValueTask.FromResult(ZabbixAgentAnswer.Supported(key, reader.Host));
    }

    private sealed class LoggingProbe : ILoggerFactory
    {
        private readonly System.Collections.Concurrent.ConcurrentQueue<string> _categories = new();

        public IReadOnlyCollection<string> Categories => _categories;

        public bool Disposed { get; private set; }

        public void AddProvider(ILoggerProvider provider)
        {
        }

        public ILogger CreateLogger(string categoryName)
        {
            _categories.Enqueue(categoryName);
            return Microsoft.Extensions.Logging.Abstractions.NullLogger.Instance;
        }

        public void Dispose() => Disposed = true;
    }

    // ----- the proxies' and the listener's builders -----------------------------------------------------------------------

    private static readonly byte[] Psk = Convert.FromHexString("0123456789abcdef0123456789abcdef");

    private static readonly byte[] Stats = """{"request":"zabbix.stats"}"""u8.ToArray();

    [Fact]
    public async Task Each_proxy_s_builder_builds_the_proxy_it_describes()
    {
        await using ZabbixActiveProxy active = ZabbixActiveProxy.CreateBuilder("proxy-01")
            .WithServer("zbx-1;zbx-2:10052")
            .AllowPeer(IPNetwork.Parse("10.0.0.0/8"))
            .AddTrapper(10060)
            .Configure(o => o.OfflineBuffer = TimeSpan.FromHours(24))
            .Build();
        await using ZabbixPassiveProxy passive = ZabbixPassiveProxy.CreateBuilder("proxy-dmz")
            .WithVersion("7.4")
            .AllowPeer(IPAddress.Loopback)
            .Build();

        Assert.Equal(("proxy-01", "zbx-1:10051;zbx-2:10052", 10060, TimeSpan.FromHours(24)),
            (active.Name, active.Options.Server, active.Options.Trapper.Port, active.Options.OfflineBuffer));
        Assert.Equal(["10.0.0.0/8"], active.Options.Trapper.AllowedPeers);
        Assert.Equal(("proxy-dmz", "7.4.0", ZabbixServerNode.DefaultPort), (passive.Name, passive.Options.Version, passive.Options.Trapper.Port));
        Assert.Equal(["127.0.0.1"], passive.Options.Trapper.AllowedPeers);
    }

    [Fact]
    public void A_proxy_s_builder_builds_once_and_what_it_built_does_not_change()
    {
        ZabbixActiveProxyBuilder active = ZabbixActiveProxy.CreateBuilder("proxy-01").WithServer("zbx-1").AllowPeer(IPAddress.Loopback);
        ZabbixPassiveProxyBuilder passive = ZabbixPassiveProxy.CreateBuilder("proxy-dmz").WithVersion("7.4").AllowPeer(IPAddress.Loopback);
        using ZabbixActiveProxy built = active.Build();
        using ZabbixPassiveProxy builtPassive = passive.Build();

        Assert.Throws<InvalidOperationException>(() => active.Build().Dispose());
        Assert.Throws<InvalidOperationException>(() => passive.Build().Dispose());
        Assert.Throws<InvalidOperationException>(() => active.AllowPeer(IPAddress.IPv6Loopback));
        Assert.Throws<InvalidOperationException>(() => passive.OnTask((task, _) => throw new InvalidOperationException()));
        Assert.Throws<InvalidOperationException>(() => passive.UseMemoryStore());
        Assert.Equal(["127.0.0.1"], built.Options.Trapper.AllowedPeers);
        Assert.Null(builtPassive.Options.TaskHandler);
    }

    [Fact]
    public void A_proxy_s_builder_checks_the_options_with_the_rules_a_host_checks_at_start()
    {
        InvalidOperationException serverless = Assert.Throws<InvalidOperationException>(() => ZabbixActiveProxy.CreateBuilder("proxy-01").AllowPeer(IPAddress.Loopback).Build().Dispose());
        InvalidOperationException versionless = Assert.Throws<InvalidOperationException>(() => ZabbixPassiveProxy.CreateBuilder("proxy-dmz").AllowPeer(IPAddress.Loopback).Build().Dispose());
        InvalidOperationException peerless = Assert.Throws<InvalidOperationException>(() => ZabbixPassiveProxy.CreateBuilder("proxy-dmz").WithVersion("7.4").Build().Dispose());

        Assert.Contains("Server", serverless.Message, StringComparison.Ordinal);
        Assert.Contains("Version", versionless.Message, StringComparison.Ordinal);
        Assert.Contains("AllowedPeers", peerless.Message, StringComparison.Ordinal);
        Assert.All([serverless, versionless, peerless], refused => Assert.IsType<OptionsValidationException>(refused.InnerException));
        Assert.Contains("'proxy-dmz'", peerless.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void A_proxy_reports_to_one_server_or_one_cluster_so_a_later_with_server_replaces_it()
    {
        // Zabbix's active proxy takes one Server, a node or one HA cluster's nodes joined by ';', and refuses a ',' at start
        // (proxy.c zbx_validate_config). WithServer sets that one server, so a second call replaces the first.
        using ZabbixActiveProxy joined = ZabbixActiveProxy.CreateBuilder("proxy-01").WithServer("zbx-1;zbx-2").AllowPeer(IPAddress.Loopback).Build();
        using ZabbixActiveProxy nodes = ZabbixActiveProxy.CreateBuilder("proxy-01").WithServer(new DnsEndPoint("zbx-1", 10051), new DnsEndPoint("zbx-2", 10052)).AllowPeer(IPAddress.Loopback).Build();
        using ZabbixActiveProxy replaced = ZabbixActiveProxy.CreateBuilder("proxy-01").WithServer("zbx-1").WithServer("zbx-2").AllowPeer(IPAddress.Loopback).Build();
        var services = new ServiceCollection();
        services.AddZabbixActiveProxy("proxy-02").WithServer("zbx-1").WithServer("zbx-2;zbx-3").AllowPeer(IPAddress.Loopback);
        using ServiceProvider provider = services.BuildServiceProvider();
        IConfiguration bound = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?> { ["Server"] = "zbx-1,zbx-2", ["Trapper:AllowedPeers:0"] = "127.0.0.1" }).Build();

        Assert.Equal("zbx-1:10051;zbx-2:10051", joined.Options.Server);
        Assert.Equal("zbx-1:10051;zbx-2:10052", nodes.Options.Server);
        Assert.Equal("zbx-2:10051", replaced.Options.Server);
        Assert.Equal("zbx-2:10051;zbx-3:10051", provider.GetRequiredService<IOptionsMonitor<ZabbixProxyOptions>>().Get("proxy-02").Server);
        InvalidOperationException comma = Assert.Throws<InvalidOperationException>(() => ZabbixActiveProxy.CreateBuilder("proxy-03").Bind(bound).Build().Dispose());  // a file's comma
        Assert.Contains("names 2 servers", comma.Message, StringComparison.Ordinal);
        Assert.Equal("server", Assert.Throws<ArgumentException>(() => ZabbixActiveProxy.CreateBuilder("proxy-04").WithServer("zbx-1,zbx-2")).ParamName);   // where it's passed
    }

    [Fact]
    public void Allow_peer_writes_each_form_as_the_text_zabbix_reads_on_every_builder_of_a_listener()
    {
        static TBuilder Peers<TBuilder>(TBuilder builder) where TBuilder : IZabbixListenerBuilder
            => builder.AllowPeer(IPNetwork.Parse("10.0.0.0/8")).AllowPeer(IPAddress.IPv6Loopback).AllowPeer("zbx.example");

        using ZabbixPassiveAgent agent = Peers(ZabbixPassiveAgent.CreateBuilder("web-01")).Build();
        using ZabbixActiveProxy active = Peers(ZabbixActiveProxy.CreateBuilder("proxy-01").WithServer("zbx-1")).Build();
        using ZabbixPassiveProxy passive = Peers(ZabbixPassiveProxy.CreateBuilder("proxy-dmz").WithVersion("7.4")).Build();
        using Listening.ZabbixListener listener = Peers(Listening.ZabbixListener.CreateBuilder()).OnRequest((_, _) => ValueTask.FromResult(ReadOnlyMemory<byte>.Empty)).Build();
        var services = new ServiceCollection();
        Peers(services.AddZabbixPassiveProxy("proxy-dmz").WithVersion("7.4"));
        using ServiceProvider provider = services.BuildValidated();

        string[] text = ["10.0.0.0/8", "::1", "zbx.example"];
        Assert.Equal(text, agent.Options.AllowedPeers);
        Assert.Equal(text, active.Options.Trapper.AllowedPeers);
        Assert.Equal(text, passive.Options.Trapper.AllowedPeers);
        Assert.Equal(text, provider.GetRequiredKeyedService<ZabbixPassiveProxy>("proxy-dmz").Options.Trapper.AllowedPeers);
        Assert.Throws<ArgumentException>(() => ZabbixActiveProxy.CreateBuilder("proxy-01").AllowPeer("10.0.0.1"));       // an address is typed
        Assert.Equal("0.0.0.0/0", Peered(Listening.ZabbixListener.CreateBuilder().AllowPeer(default(IPNetwork))));            // .NET reads a default network as every IPv4 address
        Assert.Throws<ArgumentNullException>(() => ZabbixPassiveProxy.CreateBuilder("proxy-dmz").AllowPeer((IPAddress)null!));
    }

    // The one peer a listener's builder lets in.
    private static string Peered(Listening.ZabbixListenerBuilder builder) => Assert.Single(Listened(builder).AllowedPeers);

    // The options a listener's builder holds, read from its container as Build reads them.
    private static ZabbixListenerOptions Listened(Listening.ZabbixListenerBuilder builder)
    {
        using ServiceProvider services = builder.Services.BuildServiceProvider();
        return services.GetRequiredService<IOptionsMonitor<ZabbixListenerOptions>>().Get(builder.Name);
    }

    [Fact]
    public void With_psk_is_the_proxy_s_own_key_and_allow_psk_a_peer_s_both_kept_as_hex()
    {
        using ZabbixActiveProxy active = ZabbixActiveProxy.CreateBuilder("proxy-01").WithServer("zbx-1").AllowPeer(IPAddress.Loopback)
            .WithPsk("proxy-01", Psk)
            .AllowPsk("web-01", Psk)
            .Build();
        using ZabbixPassiveProxy passive = ZabbixPassiveProxy.CreateBuilder("proxy-dmz").WithVersion("7.4").AllowPeer(IPAddress.Loopback)
            .WithPsk("proxy-dmz", Psk)
            .Build();
        using ZabbixPassiveProxy mixed = ZabbixPassiveProxy.CreateBuilder("proxy-dmz").WithVersion("7.4").AllowPeer(IPAddress.Loopback)
            .AddTrapper(0, o => o.TlsAccept = ZabbixTlsAcceptModes.Unencrypted | ZabbixTlsAcceptModes.Psk)
            .AllowPsk("web-01", Psk)
            .Build();

        // An active proxy connects to its server with its own key, and its trapper takes an agent's by identity.
        Assert.Equal((ZabbixTlsMode.PreSharedKey, "proxy-01", "0123456789abcdef0123456789abcdef"),
            (active.Options.Connection.Tls!.Mode, active.Options.Connection.Tls.PskIdentity, active.Options.Connection.Tls.Psk));
        Assert.Equal("0123456789abcdef0123456789abcdef", active.Options.Trapper.PreSharedKeys["web-01"]);
        Assert.Equal(ZabbixTlsAcceptModes.Psk, active.Options.Trapper.TlsAccept);                         // pre-shared keys only
        // A passive proxy takes its server with its own key.
        Assert.Equal(("proxy-dmz", "0123456789abcdef0123456789abcdef", ZabbixTlsMode.Unencrypted),
            (passive.Options.Trapper.Tls!.PskIdentity, passive.Options.Trapper.Tls.Psk, passive.Options.Trapper.Tls.Mode));
        Assert.Equal(ZabbixTlsAcceptModes.Psk, passive.Options.Trapper.TlsAccept);
        Assert.Null(passive.Options.Connection.Tls);
        Assert.Equal(ZabbixTlsAcceptModes.Unencrypted | ZabbixTlsAcceptModes.Psk, mixed.Options.Trapper.TlsAccept);   // a set chosen first is kept
        Assert.Throws<ArgumentException>(() => ZabbixPassiveProxy.CreateBuilder("proxy-dmz").WithPsk("proxy-dmz", new byte[15]));
        Assert.Throws<ArgumentException>(() => ZabbixActiveProxy.CreateBuilder("proxy-01").AllowPsk("", Psk));
    }

    [Fact]
    public async Task On_task_on_config_on_stats_and_add_provider_reach_the_proxy_they_build()
    {
        ZabbixProxyTaskCallback onTask = (task, _) => ValueTask.FromResult(new ZabbixProxyTaskResult(ZabbixProxyTaskType.RemoteCommandResult, task.Clock, task.Ttl, 0, "ok", 0));
        Func<ZabbixProxyConfig, ZabbixProxyConfig, CancellationToken, ValueTask<ZabbixProxyConfigVerdict>> onConfig = (_, _, _) => ValueTask.FromResult(ZabbixProxyConfigVerdict.Refuse("not today"));
        ZabbixProxyDataCallback provider = (_, _) => ValueTask.FromResult<ZabbixProxyDataPage?>(null);
        await using ZabbixPassiveProxy proxy = ZabbixPassiveProxy.CreateBuilder("proxy-dmz")
            .WithVersion("7.4")
            .AddTrapper(0, o => o.Address = "127.0.0.1")
            .AllowPeer(IPAddress.Loopback)
            .OnTask(onTask)
            .OnConfig(onConfig)
            .OnStats((_, _) => ValueTask.FromResult<IReadOnlyDictionary<string, JsonElement>?>(new Dictionary<string, JsonElement> { ["uptime"] = JsonSerializer.SerializeToElement(42) }))
            .AddProvider(provider)
            .Build();
        await using ZabbixActiveProxy active = ZabbixActiveProxy.CreateBuilder("proxy-01").WithServer("zbx-1").AllowPeer(IPAddress.Loopback).OnTask(onTask).AddProvider(provider).Build();

        await proxy.StartAsync(TestContext.Current.CancellationToken);
        JsonElement stats = await AskAsync(proxy, Stats);

        Assert.Same(onTask, proxy.Options.TaskHandler);
        Assert.Same(onConfig, proxy.Options.ConfigHandler);
        Assert.Same(provider, Assert.Single(proxy.Options.Providers));
        Assert.Same(onTask, active.Options.TaskHandler);
        Assert.Same(provider, Assert.Single(active.Options.Providers));
        Assert.Equal(42, stats.GetProperty("data").GetProperty("uptime").GetInt32());
    }

    [Fact]
    public async Task A_later_on_handler_replaces_an_earlier_one()
    {
        await using ZabbixPassiveProxy proxy = ZabbixPassiveProxy.CreateBuilder("proxy-dmz")
            .WithVersion("7.4")
            .AddTrapper(0, o => o.Address = "127.0.0.1")
            .AllowPeer(IPAddress.Loopback)
            .OnStats((_, _) => throw new InvalidOperationException("replaced"))
            .OnStats((_, _) => ValueTask.FromResult<IReadOnlyDictionary<string, JsonElement>?>(new Dictionary<string, JsonElement> { ["kept"] = JsonSerializer.SerializeToElement(true) }))
            .Build();
        await proxy.StartAsync(TestContext.Current.CancellationToken);

        JsonElement stats = await AskAsync(proxy, Stats);

        Assert.True(stats.GetProperty("data").GetProperty("kept").GetBoolean());
    }

    // One framed request to a passive proxy, and its reply.
    private static async Task<JsonElement> AskAsync(ZabbixPassiveProxy proxy, byte[] request)
    {
        var bound = (IPEndPoint)proxy.Listener!.LocalEndPoint!;
        using var client = new System.Net.Sockets.TcpClient();
        await client.ConnectAsync(bound.Address, bound.Port, TestContext.Current.CancellationToken);
        await using System.Net.Sockets.NetworkStream stream = client.GetStream();
        await Protocol.ZabbixHeader.WriteAsync(stream, request, TestContext.Current.CancellationToken);
        return JsonDocument.Parse(await Protocol.ZabbixHeader.ReadAsync(stream, TestContext.Current.CancellationToken)).RootElement.Clone();
    }

    [Fact]
    public void Add_trapper_takes_one_options_type_on_a_push_agent_and_on_a_proxy()
    {
        Action<ZabbixListenerOptions> trapper = o => { o.AllowedPeers.Add("10.0.0.0/8"); o.MaxConcurrentConnections = 64; };
        var services = new ServiceCollection();
        services.AddZabbixPushAgent("relay").AddServer("zbx-1").AddTrapper(10052, trapper);
        services.AddZabbixActiveProxy("proxy-01").WithServer("zbx-1").AddTrapper(10053, trapper);
        services.AddZabbixPassiveProxy("proxy-dmz").WithVersion("7.4").AddTrapper(10054, trapper);
        using ServiceProvider provider = services.BuildValidated();

        ZabbixListenerOptions relay = provider.GetRequiredService<IOptionsMonitor<ZabbixListenerOptions>>().Get("relay");
        ZabbixListenerOptions active = provider.GetRequiredKeyedService<ZabbixActiveProxy>("proxy-01").Options.Trapper;
        ZabbixListenerOptions passive = provider.GetRequiredKeyedService<ZabbixPassiveProxy>("proxy-dmz").Options.Trapper;
        Assert.Equal([(10052, 64), (10053, 64), (10054, 64)], new[] { (relay.Port, relay.MaxConcurrentConnections), (active.Port, active.MaxConcurrentConnections), (passive.Port, passive.MaxConcurrentConnections) });
        Assert.All([relay, active, passive], options => Assert.Equal(["10.0.0.0/8"], options.AllowedPeers));
    }

    [Fact]
    public void The_same_verbs_set_up_a_proxy_on_its_own_container_and_in_an_application_s()
    {
        static TBuilder Shared<TBuilder>(TBuilder builder) where TBuilder : IZabbixProxyBuilder
            => builder.AddTrapper(0, o => o.Timeout = TimeSpan.FromSeconds(5)).AllowPeer(IPNetwork.Parse("10.0.0.0/8")).AllowPsk("web-01", Psk)
                .UseStore((policy, services) => new Storage.ZabbixMemoryStore<ZabbixValue>(policy, services))
                .AddProvider((_, _) => ValueTask.FromResult<ZabbixProxyDataPage?>(null)).Configure(o => o.PageSize = 10);
        static TBuilder Active<TBuilder>(TBuilder builder) where TBuilder : IZabbixActiveProxyBuilder
            => Shared(builder).WithServer("zbx-1;zbx-2").WithPsk("proxy-01", Psk);
        static TBuilder Passive<TBuilder>(TBuilder builder) where TBuilder : IZabbixPassiveProxyBuilder
            => Shared(builder).WithVersion(new Version(7, 4)).WithPsk("proxy-dmz", Psk).OnConfig((_, _, _) => ValueTask.FromResult(ZabbixProxyConfigVerdict.Accepted));

        using ZabbixActiveProxy active = Active(ZabbixActiveProxy.CreateBuilder("proxy-01")).Build();
        using ZabbixPassiveProxy passive = Passive(ZabbixPassiveProxy.CreateBuilder("proxy-dmz")).Build();
        var services = new ServiceCollection();
        Active(services.AddZabbixActiveProxy("proxy-01"));
        Passive(services.AddZabbixPassiveProxy("proxy-dmz"));                                   // an active and a passive proxy in one container
        using ServiceProvider provider = services.BuildValidated();
        ZabbixActiveProxy hostedActive = provider.GetRequiredKeyedService<ZabbixActiveProxy>("proxy-01");
        ZabbixPassiveProxy hostedPassive = provider.GetRequiredKeyedService<ZabbixPassiveProxy>("proxy-dmz");

        foreach ((ZabbixProxyOptions built, ZabbixProxyOptions hosted) in new[] { (active.Options, hostedActive.Options), (passive.Options, hostedPassive.Options) })
        {
            Assert.Equal((built.Server, built.Version, built.PageSize, built.Trapper.Timeout, built.Trapper.TlsAccept), (hosted.Server, hosted.Version, hosted.PageSize, hosted.Trapper.Timeout, hosted.Trapper.TlsAccept));
            Assert.Equal(built.Trapper.AllowedPeers, hosted.Trapper.AllowedPeers);
            Assert.Equal(built.Trapper.PreSharedKeys, hosted.Trapper.PreSharedKeys);
            Assert.Equal((built.Storage is null, built.Providers.Count, built.ConfigHandler is null), (hosted.Storage is null, hosted.Providers.Count, hosted.ConfigHandler is null));
        }
        Assert.Equal(("zbx-1:10051;zbx-2:10051", "7.4.0"), (active.Options.Server, passive.Options.Version));
        Assert.NotNull(active.Options.Storage);
        Assert.Equal("proxy-01", active.Options.Connection.Tls!.PskIdentity);
        Assert.Equal("proxy-dmz", passive.Options.Trapper.Tls!.PskIdentity);
    }

    [Fact]
    public void Use_entity_framework_store_serves_a_proxy_s_builder_as_it_serves_a_push_agent_s()
    {
        using ZabbixActiveProxy proxy = ZabbixActiveProxy.CreateBuilder("proxy-01").WithServer("zbx-1").AllowPeer(IPAddress.Loopback)
            .UseEntityFrameworkStore(db => db.UseSqlite("Data Source=:memory:"))
            .Build();
        using ZabbixActiveProxy memory = ZabbixActiveProxy.CreateBuilder("proxy-01").WithServer("zbx-1").AllowPeer(IPAddress.Loopback)
            .UseEntityFrameworkStore(db => db.UseSqlite("Data Source=:memory:"))
            .UseMemoryStore()
            .Build();

        Assert.NotNull(proxy.Options.Storage);
        Assert.Null(memory.Options.Storage);                                                        // a later Use…Store replaces it
    }

    [Fact]
    public async Task A_proxy_s_health_check_is_its_role_s()
    {
        var services = new ServiceCollection();
        services.AddZabbixActiveProxy("proxy-01").WithServer("zbx-1").AllowPeer(IPAddress.Loopback).AddHealthCheck();
        services.AddZabbixPassiveProxy("proxy-dmz").WithVersion("7.4").AllowPeer(IPAddress.Loopback).AddHealthCheck();
        await using ServiceProvider provider = services.BuildValidated();

        Microsoft.Extensions.Diagnostics.HealthChecks.HealthReport report = await provider.GetRequiredService<Microsoft.Extensions.Diagnostics.HealthChecks.HealthCheckService>()
            .CheckHealthAsync(TestContext.Current.CancellationToken);

        Assert.Equal(["zabbix-active-proxy-proxy-01", "zabbix-passive-proxy-proxy-dmz"], report.Entries.Keys.Order(StringComparer.Ordinal));
        Assert.All(report.Entries.Values, entry => Assert.Equal(["zabbix", "proxy"], entry.Tags));
        Assert.Equal(Microsoft.Extensions.Diagnostics.HealthChecks.HealthStatus.Degraded, report.Entries["zabbix-passive-proxy-proxy-dmz"].Status);   // not started
    }

    [Fact]
    public async Task A_listener_s_builder_binds_kestrel_s_socket_transport_and_answers_a_connection()
    {
        System.Threading.Channels.Channel<ZabbixValue> values = System.Threading.Channels.Channel.CreateBounded<ZabbixValue>(
            new System.Threading.Channels.BoundedChannelOptions(16) { FullMode = System.Threading.Channels.BoundedChannelFullMode.Wait });
        await using Listening.ZabbixListener listener = Listening.ZabbixListener.CreateBuilder()
            .UseSocketTransport(new IPEndPoint(IPAddress.Loopback, 0))
            .AllowPeer(IPAddress.Loopback)
            .OnRequest(new Listening.ZabbixTrapper(values.Writer).Handler)
            .Build();

        await listener.StartingAsync(TestContext.Current.CancellationToken);
        await listener.StartAsync(TestContext.Current.CancellationToken);
        var bound = (IPEndPoint)listener.LocalEndPoint!;
        using var sender = new ZabbixSender(bound.Address.ToString(), bound.Port);
        ZabbixSenderResponse response = await sender.SendAsync([new ZabbixValue("web-01", "app.requests", "42", DateTimeOffset.UnixEpoch)], TestContext.Current.CancellationToken);

        Assert.True(bound.Port > 0);
        Assert.Equal(1, response.Processed);
        Assert.True(values.Reader.TryRead(out ZabbixValue? taken));
        Assert.Equal("web-01", taken.Host);
        Assert.Equal("42", taken.Value);
    }

    [Fact]
    public async Task A_listener_s_builder_takes_a_conversation_of_several_frames()
    {
        await using Listening.ZabbixListener listener = Listening.ZabbixListener.CreateBuilder()
            .UseSocketTransport(new IPEndPoint(IPAddress.Loopback, 0))
            .AllowPeer(IPAddress.Loopback)
            .OnRequest(async (conversation, cancellationToken) =>
            {
                await conversation.WriteAsync("""{"response":"success","info":"first"}"""u8.ToArray(), cancellationToken);
                await conversation.WriteAsync("""{"response":"success","info":"second"}"""u8.ToArray(), cancellationToken);
            })
            .Build();
        await listener.StartingAsync(TestContext.Current.CancellationToken);
        await listener.StartAsync(TestContext.Current.CancellationToken);
        int port = ((IPEndPoint)listener.LocalEndPoint!).Port;

        using var client = new System.Net.Sockets.TcpClient();
        await client.ConnectAsync(IPAddress.Loopback, port, TestContext.Current.CancellationToken);
        await using System.Net.Sockets.NetworkStream stream = client.GetStream();
        await Protocol.ZabbixHeader.WriteAsync(stream, """{"request":"anything"}"""u8.ToArray(), TestContext.Current.CancellationToken);
        JsonElement first = JsonDocument.Parse(await Protocol.ZabbixHeader.ReadAsync(stream, TestContext.Current.CancellationToken)).RootElement;
        JsonElement second = JsonDocument.Parse(await Protocol.ZabbixHeader.ReadAsync(stream, TestContext.Current.CancellationToken)).RootElement;

        Assert.Equal(("first", "second"), (first.GetProperty("info").GetString(), second.GetProperty("info").GetString()));
    }

    [Fact]
    public void A_listener_s_builder_needs_what_answers_and_who_may_connect_and_builds_once()
    {
        InvalidOperationException unanswered = Assert.Throws<InvalidOperationException>(() => Listening.ZabbixListener.CreateBuilder().AllowPeer(IPAddress.Loopback).Build().Dispose());
        InvalidOperationException peerless = Assert.Throws<InvalidOperationException>(() =>
            Listening.ZabbixListener.CreateBuilder().OnRequest((_, _) => ValueTask.FromResult(ReadOnlyMemory<byte>.Empty)).Build().Dispose());
        Listening.ZabbixListenerBuilder builder = Listening.ZabbixListener.CreateBuilder()
            .AllowPeer(IPAddress.Loopback)
            .AllowPsk("web-01", Psk)
            .OnRequest((_, _) => ValueTask.FromResult(ReadOnlyMemory<byte>.Empty));
        using Listening.ZabbixListener built = builder.Build();

        Assert.Contains("OnRequest", unanswered.Message, StringComparison.Ordinal);
        Assert.Contains("AllowedPeers", peerless.Message, StringComparison.Ordinal);
        Assert.IsType<OptionsValidationException>(peerless.InnerException);
        Assert.Throws<InvalidOperationException>(() => builder.Build().Dispose());
        Assert.Throws<InvalidOperationException>(() => builder.AllowPeer(IPAddress.IPv6Loopback));
        Assert.Throws<InvalidOperationException>(() => builder.UseSocketTransport(10051));
        Assert.Throws<ArgumentOutOfRangeException>(() => Listening.ZabbixListener.CreateBuilder().UseSocketTransport(65536));
    }

    [Fact]
    public async Task A_listener_takes_what_its_builder_s_container_holds_and_disposes_that_container_but_not_the_application_s_logging()
    {
        LoggerFactory? built = null;
        using var application = new LoggerFactory();
        Listening.ZabbixListenerBuilder owned = Listening.ZabbixListener.CreateBuilder()
            .AllowPeer(IPAddress.Loopback)
            .OnRequest((_, _) => ValueTask.FromResult(ReadOnlyMemory<byte>.Empty));
        owned.Services.AddSingleton<ILoggerFactory>(_ => built = new LoggerFactory());     // the container builds it, so owns it
        Listening.ZabbixListenerBuilder given = Listening.ZabbixListener.CreateBuilder()
            .AllowPeer(IPAddress.Loopback)
            .UseLogging(application)
            .OnRequest((_, _) => ValueTask.FromResult(ReadOnlyMemory<byte>.Empty));

        await using (owned.Build())
        {
            Assert.NotNull(built);                                                         // Build read it from the container
            Assert.Throws<InvalidOperationException>(() => owned.Services.AddSingleton(TimeProvider.System));   // fixed once built
        }
        await using (given.Build())
        {
        }

        Assert.Equal(Options.DefaultName, owned.Name);
        Assert.Throws<ObjectDisposedException>(() => built!.CreateLogger("after"));        // went with the listener
        Assert.NotNull(application.CreateLogger("after"));                                  // the application's stays its own
    }

    [Fact]
    public void A_listener_s_socket_transport_port_binds_every_interface_and_its_credentials_set_what_it_accepts()
    {
        Listening.ZabbixListenerBuilder builder = Listening.ZabbixListener.CreateBuilder()
            .UseSocketTransport(new IPEndPoint(IPAddress.Loopback, 10061))
            .UseSocketTransport(10062)                                                            // a later call replaces it
            .AllowPeer(IPAddress.Loopback)
            .WithCertificate("/etc/zabbix/listener.crt", "/etc/zabbix/listener.key", "/etc/zabbix/ca.crt")
            .AllowPsk("web-01", Psk);
        ZabbixListenerOptions seen = Listened(builder);

        Assert.Equal(("0.0.0.0", 10062), (seen.Address, seen.Port));

        Assert.Equal(ZabbixTlsAcceptModes.Certificate | ZabbixTlsAcceptModes.Psk, seen.TlsAccept);
        Assert.Equal(("/etc/zabbix/listener.crt", "/etc/zabbix/listener.key", "/etc/zabbix/ca.crt"), (seen.Tls!.CertificateFile, seen.Tls.KeyFile, seen.Tls.CaFile));
        Assert.Equal(ZabbixTlsMode.Unencrypted, seen.Tls.Mode);                                // a listener's accept set decides, not Mode
    }

    // ----- the API client's builder: one set of verbs, on its own container or the application's ------------------------

    // Every verb of an API client's builder, written once over IZabbixClientBuilder: one chain sets up a client of its own and a
    // registered one.
    private static TBuilder SetUpClient<TBuilder>(TBuilder builder) where TBuilder : IZabbixClientBuilder
        => builder
            .WithEndpoint("https://zabbix.example/")
            .WithToken("token")
            .WithFrontend(new Uri("https://frontend.example/"))
            .WithMaxConnectionsPerServer(20)
            .WithMaxResponseContentBytes(1024)
            .WithTimeout(TimeSpan.FromMinutes(1))
            .WithTimeout("history.get", TimeSpan.FromMinutes(2))
            .Configure(options => options.Timeouts["configuration.import"] = TimeSpan.FromMinutes(5))
            .ConfigureResilience(resilience => resilience.Retry.MaxRetryAttempts = 5);

    [Fact]
    public async Task The_same_verbs_set_up_a_client_on_its_own_container_and_in_an_application_s()
    {
        ZabbixClientBuilder standalone = SetUpClient(ZabbixClient.CreateBuilder());
        var services = new ServiceCollection();
        IZabbixClientBuilder registered = SetUpClient(services.AddZabbixClient("prod"));
        using ServiceProvider own = standalone.Services.BuildServiceProvider();
        using ServiceProvider application = services.BuildValidated();

        // What the verbs wrote, read as the client reads it: the same registrations in both containers.
        foreach ((ServiceProvider provider, string name) in new[] { (own, Options.DefaultName), (application, "prod") })
        {
            ZabbixClientOptions options = provider.GetRequiredService<IOptionsMonitor<ZabbixClientOptions>>().Get(name);
            HttpStandardResilienceOptions resilience = provider.GetRequiredService<IOptionsMonitor<HttpStandardResilienceOptions>>().Get(ZabbixHttp.GetResilienceName(name));
            Assert.Equal((new Uri("https://zabbix.example/"), new Uri("https://frontend.example/"), 20, 1024L), (options.Endpoint, options.FrontendUri, options.MaxConnectionsPerServer, options.MaxResponseContentBytes));
            Assert.Equal((TimeSpan.FromMinutes(2), TimeSpan.FromMinutes(5)), (options.Timeouts["history.get"], options.Timeouts["configuration.import"]));
            Assert.Equal((TimeSpan.FromMinutes(1), TimeSpan.FromMinutes(1), 5), (resilience.AttemptTimeout.Timeout, resilience.TotalRequestTimeout.Timeout, resilience.Retry.MaxRetryAttempts));
            Assert.True(resilience.CircuitBreaker.SamplingDuration >= TimeSpan.FromMinutes(2));   // twice an attempt, as the standard options require
            Assert.IsType<ZabbixApiTokenCredential>(provider.GetRequiredKeyedService<ZabbixCredential>(name));
        }

        await using ZabbixClient built = standalone.Build();
        ZabbixClient prod = application.GetRequiredService<IZabbixClientFactory>().Create("prod");
        Assert.Equal("prod", registered.Name);
        Assert.Equal((built.Endpoint, built.FrontendUri), (prod.Endpoint, prod.FrontendUri));
        Assert.Equal("https://zabbix.example/api_jsonrpc.php", built.Endpoint.AbsoluteUri);
    }

    [Fact]
    public void A_client_builder_builds_once_and_what_it_built_does_not_change()
    {
        ZabbixClientBuilder builder = ZabbixClient.CreateBuilder().WithEndpoint("https://zabbix.example/");
        using ZabbixClient zabbix = builder.Build();

        Assert.Throws<InvalidOperationException>(() => builder.Build().Dispose());
        Assert.Throws<InvalidOperationException>(() => builder.WithEndpoint("https://other.example/"));
        Assert.Throws<InvalidOperationException>(() => builder.WithToken("token"));
        Assert.Throws<InvalidOperationException>(() => builder.UseTimeProvider(TimeProvider.System));
        Assert.Equal("https://zabbix.example/api_jsonrpc.php", zabbix.Endpoint.AbsoluteUri);
    }

    [Fact]
    public void A_client_builder_checks_the_options_with_the_rules_a_host_checks_at_start()
    {
        InvalidOperationException nowhere = Assert.Throws<InvalidOperationException>(() => ZabbixClient.CreateBuilder().WithToken("token").Build().Dispose());
        InvalidOperationException capped = Assert.Throws<InvalidOperationException>(() => ZabbixClient.CreateBuilder().WithEndpoint("https://zabbix.example/").WithMaxConnectionsPerServer(-1).Build().Dispose());
        InvalidOperationException clear = Assert.Throws<InvalidOperationException>(() => ZabbixClient.CreateBuilder().WithEndpoint("http://zabbix.example/").WithToken("token").Build().Dispose());

        Assert.Contains(nameof(ZabbixClientOptions.Endpoint), nowhere.Message, StringComparison.Ordinal);
        Assert.Contains(nameof(ZabbixClientOptions.MaxConnectionsPerServer), capped.Message, StringComparison.Ordinal);
        Assert.Contains(nameof(ZabbixClientOptions.AllowUnencryptedCredentials), clear.Message, StringComparison.Ordinal);
        Assert.All([nowhere, capped, clear], refused => Assert.IsType<OptionsValidationException>(refused.InnerException));
    }

    [Fact]
    public void A_verb_refuses_what_it_is_passed_where_it_is_passed()
    {
        ZabbixClientBuilder builder = ZabbixClient.CreateBuilder();

        Assert.Equal("endpoint", Assert.Throws<ArgumentException>(() => builder.WithEndpoint(new Uri("zabbix", UriKind.Relative))).ParamName);
        Assert.Throws<UriFormatException>(() => builder.WithEndpoint("zabbix.example"));
        Assert.Equal("token", Assert.ThrowsAny<ArgumentException>(() => builder.WithToken("a token")).ParamName);
        Assert.Equal("method", Assert.ThrowsAny<ArgumentException>(() => builder.WithTimeout(" ", TimeSpan.FromMinutes(1))).ParamName);
    }

    [Fact]
    public async Task A_client_builder_binds_configuration_in_order_with_its_other_verbs()
    {
        IConfiguration section = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["Endpoint"] = "https://configured.example/",
            ["FrontendUri"] = "https://frontend.example/",
        }).Build();

        await using ZabbixClient zabbix = ZabbixClient.CreateBuilder()
            .Bind(section)
            .WithEndpoint("https://zabbix.example/")
            .Build();

        Assert.Equal("https://zabbix.example/api_jsonrpc.php", zabbix.Endpoint.AbsoluteUri);   // the later verb
        Assert.Equal("https://frontend.example/", zabbix.FrontendUri.AbsoluteUri);            // the section
    }

    [Fact]
    public void Configure_sets_any_option_of_a_registered_client_in_order_with_the_other_verbs()
    {
        var services = new ServiceCollection();
        IZabbixClientBuilder prod = services.AddZabbixClient("prod")
            .Configure(options =>
            {
                options.Endpoint = new Uri("https://other.example/");
                options.MaxConnectionsPerServer = 7;
            })
            .WithEndpoint("https://zabbix.example/");
        services.AddZabbixClient("prod").Configure(options => options.AllowUnencryptedCredentials = true);   // the same client, continued
        using ServiceProvider provider = services.BuildValidated();

        ZabbixClientOptions options = provider.GetRequiredService<IOptionsMonitor<ZabbixClientOptions>>().Get("prod");

        Assert.Equal("prod", prod.Name);
        Assert.Equal((new Uri("https://zabbix.example/"), 7, true), (options.Endpoint, options.MaxConnectionsPerServer, options.AllowUnencryptedCredentials));
        Assert.Equal("https://zabbix.example/api_jsonrpc.php", provider.GetRequiredService<IZabbixClientFactory>().Create("prod").Endpoint.AbsoluteUri);
    }

    [Fact]
    public async Task Configure_resilience_reaches_a_client_its_builder_builds()
    {
        using var failing = new FailingApi();
        await using ZabbixClient zabbix = ZabbixClient.CreateBuilder()
            .WithEndpoint("https://zabbix.example/")
            .ConfigureResilience(resilience =>
            {
                resilience.Retry.MaxRetryAttempts = 1;
                resilience.Retry.Delay = TimeSpan.FromMilliseconds(1);
                resilience.CircuitBreaker.MinimumThroughput = 1000;
            })
            .ConfigureHttpClientBuilder(http => http.ConfigurePrimaryHttpMessageHandler(() => failing))
            .Build();

        await Assert.ThrowsAsync<HttpRequestException>(() => zabbix.Hosts.GetAsync(TestContext.Current.CancellationToken));

        Assert.Equal(2, failing.Attempts);   // the first, and the one retry the builder allows
    }

    [Fact]
    public async Task A_later_credential_verb_replaces_an_earlier_one()
    {
        using var api = new ScriptedApi().On("host.get", "[]");
        using var bearers = new BearerProbe(api);
        await using ZabbixClient second = ZabbixClient.CreateBuilder()
            .WithEndpoint("https://zabbix.example/")
            .WithToken("first")
            .WithToken("second")
            .ConfigureHttpClientBuilder(http => http.ConfigurePrimaryHttpMessageHandler(() => bearers))
            .Build();
        // Plain http to another host takes no credential; the anonymous one given last is the client's, so it builds.
        await using ZabbixClient anonymous = ZabbixClient.CreateBuilder()
            .WithEndpoint("http://zabbix.example/")
            .WithToken("first")
            .WithCredential(ZabbixCredential.Anonymous)
            .ConfigureHttpClientBuilder(http => http.ConfigurePrimaryHttpMessageHandler(() => bearers))
            .Build();

        await second.Hosts.GetAsync(TestContext.Current.CancellationToken);
        await anonymous.Hosts.GetAsync(TestContext.Current.CancellationToken);

        Assert.Equal(["second", null], bearers.Seen);
    }

    [Fact]
    public async Task A_constructor_builds_the_client_its_builder_would()
    {
        await using var constructed = new ZabbixClient("https://zabbix.example/zabbix/", "token");
        await using ZabbixClient built = ZabbixClient.CreateBuilder().WithEndpoint("https://zabbix.example/zabbix/").WithToken("token").Build();

        Assert.Equal((built.Name, built.Endpoint, built.FrontendUri), (constructed.Name, constructed.Endpoint, constructed.FrontendUri));
        Assert.Equal(built.Transport.GetType(), constructed.Transport.GetType());
        Assert.Equal("https://zabbix.example/zabbix/api_jsonrpc.php", constructed.Endpoint.AbsoluteUri);
    }

    [Fact]
    public void Disposing_a_built_client_disposes_its_container()
    {
        using var loggers = new LoggingProbe();
        ZabbixClientBuilder builder = ZabbixClient.CreateBuilder().WithEndpoint("https://zabbix.example/");
        builder.Services.AddSingleton<ILoggerFactory>(_ => loggers);   // the container makes it, so the container disposes it
        ZabbixClient zabbix = builder.Build();

        Assert.False(loggers.Disposed);
        zabbix.Dispose();
        zabbix.Dispose();

        Assert.True(loggers.Disposed);
    }

    [Fact]
    public async Task A_built_client_logs_through_the_logging_it_was_given_and_leaves_it_to_the_application()
    {
        using var loggers = new LoggingProbe();
        ZabbixClient zabbix = ZabbixClient.CreateBuilder().WithEndpoint("https://zabbix.example/").UseLogging(loggers).Build();

        await zabbix.DisposeAsync();

        Assert.Contains(typeof(ZabbixClient).FullName, loggers.Categories);
        Assert.False(loggers.Disposed);
    }

    // ----- a group's node, and its hosts by name ----------------------------------------------------------------------

    [Fact]
    public async Task A_group_name_indexer_reads_and_deletes_one_group_and_makes_no_call_until_asked()
    {
        using Fluent fluent = Build(api => api
            .On("hostgroup.get", """[{"groupid":"7","name":"Linux"}]""")
            .On("hostgroup.delete", """{"groupids":["7"]}"""));

        ZabbixHostGroupNode linux = fluent.Zabbix.HostGroups["Linux"];
        Assert.Empty(fluent.Api.Calls);

        ZabbixHostGroup group = await linux.GetAsync(TestContext.Current.CancellationToken);
        await linux.DeleteAsync(TestContext.Current.CancellationToken);

        Assert.Equal(("Linux", 7UL), (linux.Name, group.GroupId));
        Assert.Equal("Linux", fluent.Api.ParamsOf("hostgroup.get").First().GetProperty("filter").GetProperty("name").GetString());
        Assert.Equal("7", fluent.Api.ParamsOf("hostgroup.delete").Single()[0].ToString());
    }

    [Fact]
    public async Task A_group_of_no_such_name_is_null_to_find_and_not_found_to_read_or_delete()
    {
        using Fluent fluent = Build(api => api.On("hostgroup.get", "[]"));

        Assert.Null(await fluent.Zabbix.HostGroups["none"].FindAsync(TestContext.Current.CancellationToken));
        await Assert.ThrowsAsync<ZabbixNotFoundException>(() => fluent.Zabbix.HostGroups["none"].GetAsync(TestContext.Current.CancellationToken));
        await Assert.ThrowsAsync<ZabbixNotFoundException>(() => fluent.Zabbix.HostGroups["none"].DeleteAsync(TestContext.Current.CancellationToken));
        Assert.Empty(fluent.Api.ParamsOf("hostgroup.delete"));
    }

    [Fact]
    public async Task A_group_reaches_its_hosts_by_name_and_reads_a_host_only_in_the_group()
    {
        using Fluent fluent = Build(api => api
            .On("item.get", "[]")
            .On("host.get", """[{"hostid":"10084","host":"web-01"}]""")
            .On("hostgroup.get", """[{"groupid":"7","name":"Linux"}]"""));

        ZabbixHostNode web = fluent.Zabbix.HostGroups["Linux"].Hosts["web-01"];
        List<ZabbixValue> latest = await web.GetLatestAsync(TestContext.Current.CancellationToken).ToListAsync(TestContext.Current.CancellationToken);
        List<ZabbixHostNode> hosts = await fluent.Zabbix.HostGroups["Linux"].Hosts.ToListAsync(TestContext.Current.CancellationToken);

        Assert.Equal("web-01", web.Name);
        Assert.Empty(latest);
        JsonElement sent = fluent.Api.ParamsOf("item.get").First();
        Assert.Equal(("web-01", "Linux"), (sent.GetProperty("host").GetString(), sent.GetProperty("group").GetString()));
        Assert.Equal(["web-01"], hosts.Select(host => host.Name));
        Assert.Equal("7", fluent.Api.ParamsOf("host.get").Last().GetProperty("groupids")[0].GetString());
    }

    [Fact]
    public void A_group_name_indexer_on_a_client_built_for_mocking_says_so()
        => Assert.Throws<InvalidOperationException>(() => new FakeGroups()["Linux"]);

    // ----- cancellation: the token last, positionally, and an overload without it --------------------------------------

    [Fact]
    public async Task A_read_takes_its_token_last_positionally_or_no_token()
    {
        using Fluent fluent = Build(api => api.On("host.get", """[{"hostid":"10084","host":"web-01"}]"""));
        CancellationToken token = TestContext.Current.CancellationToken;

        IReadOnlyList<ZabbixHost> all = await fluent.Zabbix.Hosts.GetAsync(token);
        await fluent.Zabbix.Hosts.GetAsync(q => q.Limit = 1, token);
        await fluent.Zabbix.Hosts.GetAsync(new ZabbixHostGetParameters { Limit = 2 }, token);
        await fluent.Zabbix.Hosts.GetAsync(cancellationToken: token);
#pragma warning disable xUnit1051 // the overloads without a token are what this pins
        await fluent.Zabbix.Hosts.GetAsync();
        await fluent.Zabbix.Hosts.GetAsync(q => q.Limit = 3);
        await fluent.Zabbix.Hosts.GetAsync(new ZabbixHostGetParameters { Limit = 4 });
#pragma warning restore xUnit1051

        Assert.Equal("web-01", Assert.Single(all).TechnicalName);
        Assert.Equal([null, 1, 2, null, null, 3, 4], fluent.Api.ParamsOf("host.get").Select(sent => sent.TryGetProperty("limit", out JsonElement limit) ? limit.GetInt32() : (int?)null));
    }

    [Fact]
    public async Task A_cancelled_token_stops_every_overload_before_it_sends()
    {
        using Fluent fluent = Build(api => api.On("host.get", "[]").On("item.get", "[]"));
        using var stop = new CancellationTokenSource();
        await stop.CancelAsync();
        CancellationToken cancelled = stop.Token;

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => fluent.Zabbix.Hosts.GetAsync(cancelled));
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => fluent.Zabbix.Hosts.GetAsync(q => q.Limit = 1, cancelled));
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => fluent.Zabbix.Hosts.CountAsync(cancelled));
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => fluent.Zabbix.Hosts.CreateAsync("web-02", [1UL], cancelled));
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => fluent.Zabbix.Hosts.DeleteAsync(1UL, cancelled));
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => fluent.Zabbix.SendAsync("host.get", JsonDocument.Parse("{}").RootElement, cancelled));
        await Assert.ThrowsAnyAsync<OperationCanceledException>(async () =>
            await fluent.Zabbix.Hosts["web-01"].GetHistoryAsync(ZabbixItemKeys.Cpu.Load(), TimeSpan.FromHours(1), cancelled).ToListAsync(TestContext.Current.CancellationToken));

        Assert.Empty(fluent.Api.Calls);
    }

    [Fact]
    public void Every_async_member_of_the_api_client_takes_an_optional_token_last()
    {
        static bool Async(Type type) => DesignConformanceTests.ReturnsTask(type) || (type.IsGenericType && type.GetGenericTypeDefinition() == typeof(IAsyncEnumerable<>));
        static string[] Shape(MethodInfo method) => [.. method.GetParameters().Select(parameter => parameter.ParameterType.ToString())];
        MethodInfo[] members = [.. typeof(ZabbixClient).Assembly.GetExportedTypes()
            .SelectMany(type => type.GetMethods(BindingFlags.Public | BindingFlags.Instance | BindingFlags.Static | BindingFlags.DeclaredOnly))
            .Where(method => !method.IsSpecialName && Async(method.ReturnType) && method.Name != nameof(IAsyncEnumerable<int>.GetAsyncEnumerator) && method.Name != nameof(IAsyncDisposable.DisposeAsync))];

        // Every overload takes the token last and optional, so none forwards to another without it (ADR 0013, section 6).
        IEnumerable<string> untokened = members
            .Where(method => method.GetParameters() is not [.., { ParameterType: var last, HasDefaultValue: true }] || last != typeof(CancellationToken))
            .Select(method => $"{method.DeclaringType!.Name}.{method.Name}({string.Join(", ", Shape(method))})");

        Assert.True(members.Length > 500, $"{members.Length} async members found");
        DesignConformanceTests.AssertNone(untokened);
    }

    [Fact]
    public async Task A_typed_history_takes_its_window_and_a_token_or_none()
    {
        using Fluent fluent = Build(api => api
            .On("item.get", """[{"itemid":"1","hostid":"10","key_":"system.cpu.load[all,avg1]","value_type":"0","hosts":[{"hostid":"10","host":"web-01"}]}]""")
            .On("history.get", "[]"));
        ZabbixHostNode web = fluent.Zabbix.Hosts["web-01"];

        List<ZabbixSample<double>> windowed = await web.GetHistoryAsync(ZabbixItemKeys.Cpu.Load(), TimeSpan.FromHours(1), TestContext.Current.CancellationToken).ToListAsync(TestContext.Current.CancellationToken);
#pragma warning disable xUnit1051 // a call that leaves the optional token out is what this pins
        List<ZabbixSample<double>> bare = await web.GetHistoryAsync(ZabbixItemKeys.Cpu.Load(), TimeSpan.FromHours(1)).ToListAsync(TestContext.Current.CancellationToken);
        List<ZabbixSample<double>> ranged = await web.GetHistoryAsync(ZabbixItemKeys.Cpu.Load(), DateTimeOffset.UnixEpoch, null).ToListAsync(TestContext.Current.CancellationToken);
#pragma warning restore xUnit1051

        Assert.All([windowed, bare, ranged], Assert.Empty);
        Assert.Equal(3, fluent.Api.ParamsOf("item.get").Count(sent => sent.GetProperty("filter").GetProperty("key_").GetString() == "system.cpu.load[all,avg1]"));
        Assert.Contains(fluent.Api.ParamsOf("history.get"), sent => sent.GetProperty("time_from").ToString() == "0");
    }

    [Fact]
    public async Task A_client_takes_a_token_or_a_login_directly()
    {
        await using var byToken = new ZabbixClient("https://zabbix.example/", "token");
        await using var byLogin = new ZabbixClient(new Uri("https://zabbix.example/"), new System.Net.NetworkCredential("user", "password"));

        Assert.Equal(byToken.Endpoint, byLogin.Endpoint);
    }

    [Fact]
    public void An_endpoint_reads_and_writes_zabbix_spelling()
    {
        Assert.Equal(new System.Net.DnsEndPoint("web-01", 10051), ZabbixEndPoint.Parse("web-01"));
        Assert.Equal(new System.Net.DnsEndPoint("web-01", 10050), ZabbixEndPoint.Parse("web-01", 10050));
        Assert.Equal(new System.Net.DnsEndPoint("::1", 10051), ZabbixEndPoint.Parse("[::1]:10051"));
        Assert.Equal("[::1]:10051", ZabbixEndPoint.Format(new System.Net.DnsEndPoint("::1", 10051)));
        Assert.False(ZabbixEndPoint.TryParse("not a host", 10051, out _));

        IReadOnlyList<IReadOnlyList<System.Net.DnsEndPoint>> active = ZabbixEndPoint.ParseServerActive("a;b:1,c");
        Assert.Equal(2, active.Count);
        Assert.Equal(new System.Net.DnsEndPoint("b", 1), active[0][1]);
    }

    private sealed class FakeHosts : ZabbixHostClient
    {
        public ZabbixHostGetParameters? Last { get; private set; }

        public override Task<IReadOnlyList<ZabbixHost>> GetAsync(ZabbixHostGetParameters parameters, CancellationToken cancellationToken)
        {
            Last = parameters;
            return Task.FromResult<IReadOnlyList<ZabbixHost>>([]);
        }
    }

    private sealed class FakeGroups : ZabbixHostGroupClient;

    // Answers every request with a server error, so a read is retried as often as the resilience handler allows.
    private sealed class FailingApi : HttpMessageHandler
    {
        private int _attempts;

        public int Attempts => Volatile.Read(ref _attempts);

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Interlocked.Increment(ref _attempts);
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.InternalServerError));
        }
    }

    // The bearer token each request carries, in order, before the scripted API answers it.
    private sealed class BearerProbe(HttpMessageHandler api) : DelegatingHandler(api)
    {
        private readonly List<string?> _seen = [];

        public IReadOnlyList<string?> Seen
        {
            get
            {
                lock (_seen) return [.. _seen];
            }
        }

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            lock (_seen) _seen.Add(request.Headers.Authorization?.Parameter);
            return base.SendAsync(request, cancellationToken);
        }
    }
}
