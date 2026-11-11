// This Source Code Form is subject to the terms of the Mozilla Public
// License, v. 2.0. Copyright (c) 2026 schroeder247. SPDX-License-Identifier: MPL-2.0
using System.Globalization;
using System.Text;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Http.Resilience;
using Microsoft.Extensions.Options;

namespace Community.Zabbix.Tests;

/// <summary>
/// One document, two forms: a configuration file (JSON here; any <c>IConfiguration</c> source binds the same) and the
/// fluent chain bind the same named rows, across every section the graph
/// has (servers, agents, an agent's trapper, active agents, passive agents, proxies of either mode): one composition
/// root, locked here so the forms cannot drift apart as a section is added.
/// </summary>
public sealed class DocumentTests
{
    private const string Json = """
        { "Zabbix": {
            "Servers": {
              "prod": { "Endpoint": "https://prod.example/api_jsonrpc.php", "ApiToken": "tok-prod", "MaxConnectionsPerServer": 7, "Resilience": { "Retry": { "MaxRetryAttempts": 5 } } },
              "staging": { "Endpoint": "https://staging.example/api_jsonrpc.php" } },
            "PushAgents": {
              "edge-01": { "ServerActive": "zbx-server-01;zbx-server-02:10052", "MaxItems": 10000, "Send": { "BatchSize": 100, "FlushInterval": "00:00:02" },
                           "Connection": { "Timeout": "00:00:03", "Tls": { "Mode": "PreSharedKey", "PskIdentity": "edge", "Psk": "0123456789abcdef0123456789abcdef" } },
                           "Trapper": { "Port": 0, "AllowedPeers": [ "127.0.0.1" ] } } },
            "ActiveAgents": {
              "roaming-01": { "ServerActive": "zbx-server-01", "Hostname": "roamer" } },
            "PassiveAgents": {
              "probe-01": { "Port": 0, "AllowedPeers": [ "127.0.0.1" ], "Hostname": "prober" } },
            "Proxies": {
              "proxy-01": { "Mode": "Passive", "Version": "7.4.15", "Trapper": { "Port": 0, "AllowedPeers": [ "127.0.0.1" ] } } } } }
        """;

    private static ServiceProvider FromDocument(IConfiguration root)
    {
        var services = new ServiceCollection();
        services.AddZabbixClients(root.GetSection(ZabbixConfigurationSections.Section));
        services.AddZabbixAgents(root.GetSection(ZabbixConfigurationSections.Section));
        services.AddZabbixProxies(root.GetSection(ZabbixConfigurationSections.Section));
        return services.BuildValidated();
    }

    private static ServiceProvider Fluent()
    {
        var services = new ServiceCollection();
        services.AddZabbix("prod").Configure(options => { options.Endpoint = new Uri("https://prod.example/api_jsonrpc.php"); options.ApiToken = "tok-prod"; options.MaxConnectionsPerServer = 7; });
        services.Configure<HttpStandardResilienceOptions>(ZabbixHttp.GetResilienceName("prod"), options => options.Retry.MaxRetryAttempts = 5);
        services.AddZabbix("staging").Configure(options => options.Endpoint = new Uri("https://staging.example/api_jsonrpc.php"));
        services.AddZabbixPushAgent("edge-01").AddServer(new ZabbixServerNode("zbx-server-01"), new ZabbixServerNode("zbx-server-02", 10052)).Configure(options =>
        {
            options.MaxItems = 10000;
            options.Send.BatchSize = 100;
            options.Send.FlushInterval = TimeSpan.FromSeconds(2);
            options.Connection.Timeout = TimeSpan.FromSeconds(3);
            options.Connection.Tls = new ZabbixTlsOptions { Mode = ZabbixTlsMode.PreSharedKey, PskIdentity = "edge", Psk = "0123456789abcdef0123456789abcdef" };
        }).AddTrapper(0, o => o.AllowedPeers.Add("127.0.0.1"));
        services.AddZabbixActiveAgent("roaming-01").AddServer("zbx-server-01").Configure(o => o.Hostname = "roamer");
        services.AddZabbixPassiveAgent("probe-01").Configure(o => { o.Port = 0; o.AllowedPeers.Add("127.0.0.1"); o.Hostname = "prober"; });
        services.AddZabbixPassiveProxy("proxy-01").WithVersion("7.4.15").AddTrapper(0).AllowPeer(System.Net.IPAddress.Loopback);
        return services.BuildValidated();
    }

    private static void AssertTheDocument(ServiceProvider provider)
    {
        IOptionsMonitor<ZabbixOptions> servers = provider.GetRequiredService<IOptionsMonitor<ZabbixOptions>>();
        IOptionsMonitor<ZabbixPushAgentOptions> agents = provider.GetRequiredService<IOptionsMonitor<ZabbixPushAgentOptions>>();
        IOptionsMonitor<HttpStandardResilienceOptions> resilience = provider.GetRequiredService<IOptionsMonitor<HttpStandardResilienceOptions>>();

        Assert.Equal(new Uri("https://prod.example/api_jsonrpc.php"), servers.Get("prod").Endpoint);
        Assert.Equal("tok-prod", servers.Get("prod").ApiToken);
        Assert.Equal(7, servers.Get("prod").MaxConnectionsPerServer);
        Assert.Equal(5, resilience.Get(ZabbixHttp.GetResilienceName("prod")).Retry.MaxRetryAttempts);
        Assert.Equal(new Uri("https://staging.example/api_jsonrpc.php"), servers.Get("staging").Endpoint);

        ZabbixPushAgentOptions edge = agents.Get("edge-01");
        ZabbixServerActive serverActive = ZabbixServerActive.Parse(edge.ServerActive!, CultureInfo.InvariantCulture);
        Assert.Equal(["zbx-server-01:10051", "zbx-server-02:10052"], Assert.Single(serverActive).Select(node => node.ToString()));   // one HA destination
        Assert.Equal(10000, edge.MaxItems);
        Assert.Equal(100, edge.Send.BatchSize);
        Assert.Equal(TimeSpan.FromSeconds(2), edge.Send.FlushInterval);
        Assert.Equal(TimeSpan.FromSeconds(3), edge.Connection.Timeout);
        Assert.Equal(ZabbixTlsMode.PreSharedKey, edge.Connection.Tls!.Mode);
        Assert.Equal("edge", edge.Connection.Tls.PskIdentity);

        ZabbixActiveAgentOptions roaming = provider.GetRequiredService<IOptionsMonitor<ZabbixActiveAgentOptions>>().Get("roaming-01");
        Assert.Equal("roamer", roaming.Hostname);
        Assert.Equal("zbx-server-01:10051", Assert.Single(Assert.Single(ZabbixServerActive.Parse(roaming.ServerActive!, CultureInfo.InvariantCulture))).ToString());

        ZabbixPassiveAgentOptions probe = provider.GetRequiredService<IOptionsMonitor<ZabbixPassiveAgentOptions>>().Get("probe-01");
        Assert.Equal("prober", probe.Hostname);
        Assert.Equal(["127.0.0.1"], probe.AllowedPeers);

        ZabbixProxyOptions proxy = provider.GetRequiredService<IOptionsMonitor<ZabbixProxyOptions>>().Get("proxy-01");
        Assert.Equal(["127.0.0.1"], proxy.Trapper.AllowedPeers);
        Assert.Equal("7.4.15", proxy.Version);

        Assert.Equal(5, provider.GetServices<IHostedService>().Count());   // the agent, its trapper, the active agent, the passive agent, the passive proxy
        Assert.Equal("prod.example", provider.GetRequiredService<IZabbixClientFactory>().Create("prod").Endpoint.Host);
        provider.GetRequiredService<IStartupValidator>().Validate();
    }

    [Fact]
    public void A_json_document_binds_the_same()
    {
        using ServiceProvider provider = FromDocument(new ConfigurationBuilder().AddJsonStream(new MemoryStream(Encoding.UTF8.GetBytes(Json))).Build());
        AssertTheDocument(provider);
    }

    [Fact]
    public void Each_root_given_the_host_s_whole_configuration_reads_its_zabbix_section()
    {
        // builder.Configuration passed where the Zabbix section belongs: a configuration with none of the document's maps
        // but a Zabbix section is read from that section, by all three roots alike, instead of registering nothing.
        IConfigurationRoot root = new ConfigurationBuilder().AddJsonStream(new MemoryStream(Encoding.UTF8.GetBytes(Json))).Build();
        var services = new ServiceCollection();
        services.AddZabbixClients(root);
        services.AddZabbixAgents(root);
        services.AddZabbixProxies(root);
        using ServiceProvider provider = services.BuildValidated();
        AssertTheDocument(provider);
    }

    [Fact]
    public void A_configuration_that_holds_a_map_is_the_document_itself_even_beside_a_zabbix_key()
    {
        var services = new ServiceCollection();
        services.AddZabbixClients(new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["Servers:prod:Endpoint"] = "https://prod.example/api_jsonrpc.php",
            ["Zabbix:Servers:other:Endpoint"] = "https://other.example/api_jsonrpc.php",
        }).Build());
        using ServiceProvider provider = services.BuildValidated();

        Assert.Equal("prod.example", provider.GetRequiredService<IZabbixClientFactory>().Create("prod").Endpoint.Host);
        Assert.False(provider.GetRequiredService<IZabbixClientFactory>().TryCreate("other", out _));
    }

    [Fact]
    public void The_fluent_chain_binds_the_same()
    {
        using ServiceProvider provider = Fluent();
        AssertTheDocument(provider);
    }

    [Fact]
    public void A_proxy_row_s_mode_chooses_the_verb_a_document_never_names()
    {
        var active = new ServiceCollection();
        active.AddZabbixProxies(new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["Zabbix:Proxies:p1:Server"] = "zbx1",
            ["Zabbix:Proxies:p1:Trapper:Port"] = "0",
            ["Zabbix:Proxies:p1:Trapper:AllowedPeers:0"] = "127.0.0.1",
        }).Build().GetSection(ZabbixConfigurationSections.Section));
        using ServiceProvider activeProvider = active.BuildValidated();
        Assert.IsType<ZabbixActiveProxy>(activeProvider.GetRequiredKeyedService<ZabbixActiveProxy>("p1"));

        var passive = new ServiceCollection();
        passive.AddZabbixProxies(new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["Zabbix:Proxies:p2:Mode"] = "Passive",
            ["Zabbix:Proxies:p2:Version"] = "7.4.15",
            ["Zabbix:Proxies:p2:Trapper:Port"] = "0",
            ["Zabbix:Proxies:p2:Trapper:AllowedPeers:0"] = "127.0.0.1",
        }).Build().GetSection(ZabbixConfigurationSections.Section));
        using ServiceProvider passiveProvider = passive.BuildValidated();
        Assert.IsType<ZabbixPassiveProxy>(passiveProvider.GetRequiredKeyedService<ZabbixPassiveProxy>("p2"));
    }

    [Fact]
    public void The_agent_root_binds_agents_and_active_agents_and_leaves_the_servers_to_the_sdk()
    {
        // ZabbixAgentsServiceCollectionExtensions.AddZabbixAgents lives in Zabbix.Agent, which references neither Zabbix.Proxy nor
        // Zabbix.Sdk: a consumer with no listener gets the sections that package can build, and the API's servers are
        // the API package's root to bind.
        var services = new ServiceCollection();
        services.AddZabbixAgents(Document());
        using ServiceProvider provider = services.BuildValidated();

        Assert.Equal("zbx1", provider.GetRequiredService<IOptionsMonitor<ZabbixPushAgentOptions>>().Get("edge").ServerActive);
        Assert.Equal("zbx1", provider.GetRequiredService<IOptionsMonitor<ZabbixActiveAgentOptions>>().Get("roaming").ServerActive);
        Assert.Null(provider.GetService<IZabbixClientFactory>());
    }

    [Fact]
    public void The_proxy_root_leaves_the_active_agents_to_the_agent_root()
    {
        var services = new ServiceCollection();
        services.AddZabbixProxies(Document());
        using ServiceProvider provider = services.BuildValidated();

        Assert.Equal("zbx1", provider.GetRequiredService<IOptionsMonitor<ZabbixPushAgentOptions>>().Get("edge").ServerActive);
        Assert.Null(provider.GetKeyedService<ZabbixActiveAgent>("roaming"));
    }

    [Fact]
    public void The_sdk_root_binds_the_servers_and_leaves_the_agent_sections_unregistered()
    {
        // ZabbixConfigurationSections.AddZabbixClients lives in Zabbix.Sdk, which does not reference Zabbix.Agent: an API-only
        // consumer's document may carry agent sections, and nothing tries to host them.
        var services = new ServiceCollection();
        services.AddZabbixClients(Document());
        using ServiceProvider provider = services.BuildValidated();

        Assert.Equal(new Uri("https://prod.example/api_jsonrpc.php"), provider.GetRequiredService<IOptionsMonitor<ZabbixOptions>>().Get("prod").Endpoint);
        Assert.Empty(provider.GetServices<Microsoft.Extensions.Hosting.IHostedService>());
    }

    private static IConfigurationSection Document() => new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
    {
        ["Zabbix:Servers:prod:Endpoint"] = "https://prod.example/api_jsonrpc.php",
        ["Zabbix:PushAgents:edge:ServerActive"] = "zbx1",
        ["Zabbix:ActiveAgents:roaming:ServerActive"] = "zbx1",
    }).Build().GetSection(ZabbixConfigurationSections.Section);
}
