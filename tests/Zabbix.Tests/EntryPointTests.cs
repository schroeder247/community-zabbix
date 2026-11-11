// This Source Code Form is subject to the terms of the Mozilla Public
// License, v. 2.0. Copyright (c) 2026 schroeder247. SPDX-License-Identifier: MPL-2.0
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Time.Testing;

namespace Community.Zabbix.Tests;

/// <summary>
/// The container-free entry points (docs/adr/0007-container-free-runners.md): a client asks only for what connecting needs.
/// The frontend's address is enough for the API, a credential has a factory, and a wire client takes a host or
/// <c>host:port</c> as Zabbix's own configuration spells one.
/// </summary>
public sealed class EntryPointTests
{
    [Theory]
    [InlineData("https://zabbix.example/", "https://zabbix.example/api_jsonrpc.php")]
    [InlineData("https://zabbix.example", "https://zabbix.example/api_jsonrpc.php")]
    [InlineData("https://zabbix.example/zabbix", "https://zabbix.example/zabbix/api_jsonrpc.php")]
    [InlineData("https://zabbix.example/zabbix/", "https://zabbix.example/zabbix/api_jsonrpc.php")]
    [InlineData("http://localhost:8080/", "http://localhost:8080/api_jsonrpc.php")]
    [InlineData("https://zabbix.example/api_jsonrpc.php", "https://zabbix.example/api_jsonrpc.php")]
    [InlineData("https://zabbix.example/zabbix/API_JSONRPC.PHP", "https://zabbix.example/zabbix/API_JSONRPC.PHP")]
    [InlineData("https://zabbix.example/gateway.php", "https://zabbix.example/gateway.php")]
    public void The_api_endpoint_is_the_frontend_s_api_jsonrpc_php_unless_a_script_is_named(string address, string endpoint)
        => Assert.Equal(endpoint, ZabbixOptions.GetApiEndpoint(new Uri(address)).AbsoluteUri);

    [Fact]
    public void A_relative_address_is_left_for_the_endpoint_s_own_validation()
    {
        var relative = new Uri("zabbix", UriKind.Relative);

        Assert.Same(relative, ZabbixOptions.GetApiEndpoint(relative));
    }

    [Fact]
    public void A_client_built_from_the_frontend_s_address_posts_to_its_api()
    {
        using var client = new ZabbixClient("https://zabbix.example/", ZabbixCredential.FromApiToken("token"));

        Assert.Equal("https://zabbix.example/api_jsonrpc.php", client.Endpoint.AbsoluteUri);
    }

    [Fact]
    public void A_registered_client_reads_the_frontend_s_address_the_same_way()
    {
        var services = new ServiceCollection();
        services.AddZabbix().Configure(options => options.Endpoint = new Uri("https://zabbix.example/zabbix/"));
        using ServiceProvider provider = services.BuildValidated();

        Assert.Equal("https://zabbix.example/zabbix/api_jsonrpc.php", provider.GetRequiredService<ZabbixClient>().Endpoint.AbsoluteUri);
    }

    [Fact]
    public void The_string_constructors_take_an_absolute_url_only()
    {
        Assert.Throws<ArgumentNullException>(() => new ZabbixClient((string)null!, ZabbixCredential.Anonymous));
        Assert.Throws<UriFormatException>(() => new ZabbixClient("zabbix.example", ZabbixCredential.Anonymous));
        using var client = new ZabbixClient("https://zabbix.example/", ZabbixCredential.Anonymous);

        Assert.Equal("https://zabbix.example/api_jsonrpc.php", client.Endpoint.AbsoluteUri);
    }

    [Fact]
    public async Task The_caller_s_logging_and_clock_reach_a_client_and_stay_the_caller_s()
    {
        var time = new FakeTimeProvider();
        using var loggers = new TrackedLoggerFactory();
        ZabbixClient client = ZabbixClient.CreateBuilder().WithEndpoint(new Uri("https://zabbix.example/")).UseLogging(loggers).UseTimeProvider(time).Build();

        Assert.Same(loggers, client.Loggers);
        Assert.Same(time, client.Time);
        await client.DisposeAsync();
        Assert.False(loggers.Disposed);
    }

    [Fact]
    public void The_credential_factories_build_the_credential_types()
    {
        ZabbixApiTokenCredential token = ZabbixCredential.FromApiToken("0123456789abcdef");
        ZabbixSessionCredential login = ZabbixCredential.FromLogin("Admin", "zabbix");

        Assert.NotNull(token);
        Assert.Equal("Admin", login.UserName);
        Assert.Throws<ArgumentException>(() => ZabbixCredential.FromApiToken("a token\n"));
        Assert.Throws<ArgumentException>(() => ZabbixCredential.FromLogin("Admin", ""));
    }

    [Theory]
    [InlineData("web-01", 10050, "web-01", 10050)]
    [InlineData("web-01:10070", 10050, "web-01", 10070)]
    [InlineData("10.0.0.1", 10051, "10.0.0.1", 10051)]
    [InlineData("fe80::1", 10050, "fe80::1", 10050)]
    [InlineData("[::1]:30051", 10050, "::1", 30051)]
    [InlineData("zbx_server", 10051, "zbx_server", 10051)]
    public void A_node_read_from_text_takes_the_client_s_port_when_it_names_none(string text, int defaultPort, string host, int port)
    {
        ZabbixServerNode node = ZabbixServerNode.Parse(text, defaultPort, "node");

        Assert.Equal(host, node.Host);
        Assert.Equal(port, node.Port);
    }

    [Theory]
    [InlineData("")]
    [InlineData("web-01:port")]
    [InlineData("a:1:2")]
    [InlineData("web-01:70000")]
    public void Text_that_is_not_a_node_is_refused_naming_the_caller_s_parameter(string text)
    {
        ArgumentException refused = Assert.ThrowsAny<ArgumentException>(() => new ZabbixGetter(text));

        Assert.Equal("host", refused.ParamName);
    }

    [Fact]
    public void The_wire_clients_read_host_and_port_from_one_string()
    {
        using var getter = new ZabbixGetter("web-01:10070");
        using var wire = new ZabbixServerWire("zbx-server:10052", sessionId: null);

        Assert.Throws<ArgumentException>(() => new ZabbixServerWire("zbx-server:port"));
        Assert.Throws<ArgumentException>(() => new ZabbixSender("zbx-server:port"));
    }

    [Fact]
    public void A_proxy_reads_its_server_s_host_and_port_from_one_string_as_an_agent_does()
    {
        var services = new ServiceCollection();
        services.AddZabbixActiveProxy("proxy-01").WithServer("zbx-server:10052").AllowPeer(System.Net.IPAddress.Loopback);
        services.AddZabbixActiveProxy("proxy-02").WithServer("zbx-server").AllowPeer(System.Net.IPAddress.Loopback);
        using ServiceProvider provider = services.BuildServiceProvider();
        var options = provider.GetRequiredService<Microsoft.Extensions.Options.IOptionsMonitor<ZabbixProxyOptions>>();

        Assert.Equal(new ZabbixServerNode("zbx-server", 10052).ToString(), options.Get("proxy-01").Server);
        Assert.Equal(new ZabbixServerNode("zbx-server").ToString(), options.Get("proxy-02").Server);
        ArgumentException refused = Assert.Throws<ArgumentException>(() => new ServiceCollection().AddZabbixActiveProxy("proxy-03").WithServer("zbx-server:port"));
        Assert.Equal("server", refused.ParamName);
    }

    [Fact]
    public async Task A_sender_named_by_host_and_port_delivers_to_that_trapper()
    {
        var (port, received) = MockTrapper.Collect(1);
        using var sender = new ZabbixSender($"127.0.0.1:{port}");

        ZabbixSenderResponse response = await sender.SendAsync([new ZabbixValue("web-01", "app.requests", "42", DateTimeOffset.UnixEpoch)], TestContext.Current.CancellationToken);

        Assert.Equal(1, response.Processed);
        Assert.Equal(1, await received.WaitAsync(TimeSpan.FromSeconds(10), TestContext.Current.CancellationToken));
    }

    private sealed class TrackedLoggerFactory : ILoggerFactory
    {
        public bool Disposed { get; private set; }

        public void AddProvider(ILoggerProvider provider)
        {
        }

        public ILogger CreateLogger(string categoryName) => NullLogger.Instance;

        public void Dispose() => Disposed = true;
    }
}
