// This Source Code Form is subject to the terms of the Mozilla Public
// License, v. 2.0. Copyright (c) 2026 schroeder247. SPDX-License-Identifier: MPL-2.0
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Http.Resilience;
using Microsoft.Extensions.Options;

namespace Community.Zabbix.Tests;

/// <summary>The options graph: validated before anything starts, bound from code or a section, and one transport per container.</summary>
public sealed class OptionsTests
{
    [Fact]
    public void A_missing_endpoint_fails_at_start_not_at_the_first_request()
    {
        var services = new ServiceCollection();
        services.AddZabbix();
        using ServiceProvider provider = services.BuildValidated();

        OptionsValidationException failure = Assert.Throws<OptionsValidationException>(() => provider.GetRequiredService<IStartupValidator>().Validate());

        Assert.Contains("Endpoint", failure.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void A_section_binds_the_endpoint_the_token_and_the_resilience_words()
    {
        IConfiguration section = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["Endpoint"] = "https://zbx.example/api_jsonrpc.php",
            ["ApiToken"] = "tok",
            ["Resilience:Retry:MaxRetryAttempts"] = "7",
        }).Build();
        var services = new ServiceCollection();
        services.AddZabbix().Bind(section);
        using ServiceProvider provider = services.BuildValidated();

        ZabbixOptions options = provider.GetRequiredService<IOptions<ZabbixOptions>>().Value;
        HttpStandardResilienceOptions resilience = provider.GetRequiredService<IOptionsMonitor<HttpStandardResilienceOptions>>().Get(ZabbixHttp.GetResilienceName());

        Assert.Equal(new Uri("https://zbx.example/api_jsonrpc.php"), options.Endpoint);
        Assert.Equal("tok", options.ApiToken);
        Assert.Equal(7, resilience.Retry.MaxRetryAttempts);
    }

    [Fact]
    public void The_transport_is_one_singleton()
    {
        var services = new ServiceCollection();
        services.AddZabbix().Configure(options => options.Endpoint = new Uri("https://zbx.example/api_jsonrpc.php"));
        using ServiceProvider provider = services.BuildValidated();

        Assert.Same(provider.GetRequiredService<IZabbixTransport>(), provider.GetRequiredService<IZabbixTransport>());
    }

    [Fact]
    public void A_token_in_the_document_becomes_the_credential_and_none_means_anonymous()
    {
        var withToken = new ServiceCollection();
        withToken.AddZabbix().Configure(options => { options.Endpoint = new Uri("https://zbx.example/"); options.ApiToken = "tok"; });
        var without = new ServiceCollection();
        without.AddZabbix().Configure(options => options.Endpoint = new Uri("https://zbx.example/"));

        using ServiceProvider first = withToken.BuildValidated();
        using ServiceProvider second = without.BuildValidated();

        Assert.IsType<ZabbixApiTokenCredential>(first.GetRequiredKeyedService<ZabbixCredential>(Options.DefaultName));
        Assert.Same(ZabbixCredential.Anonymous, second.GetRequiredKeyedService<ZabbixCredential>(Options.DefaultName));
    }

    [Fact]
    public void The_builder_binds_a_named_server_from_a_section_as_its_add_overload_does()
    {
        IConfiguration section = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["Endpoint"] = "https://zbx.example/api_jsonrpc.php",
            ["Resilience:Retry:MaxRetryAttempts"] = "5",
        }).Build();
        var services = new ServiceCollection();
        services.AddZabbix("prod").Bind(section);
        using ServiceProvider provider = services.BuildValidated();

        Assert.Equal(new Uri("https://zbx.example/api_jsonrpc.php"), provider.GetRequiredService<IOptionsMonitor<ZabbixOptions>>().Get("prod").Endpoint);
        Assert.Equal(5, provider.GetRequiredService<IOptionsMonitor<HttpStandardResilienceOptions>>().Get(ZabbixHttp.GetResilienceName("prod")).Retry.MaxRetryAttempts);
    }

    [Fact]
    public void Per_method_timeouts_bind_from_a_section_and_a_non_positive_one_fails_at_start()
    {
        IConfiguration good = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["Endpoint"] = "https://zbx.example/api_jsonrpc.php",
            ["Timeouts:configuration.import"] = "00:10:00",
        }).Build();
        IConfiguration bad = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["Endpoint"] = "https://zbx.example/api_jsonrpc.php",
            ["Timeouts:host.get"] = "00:00:00",
        }).Build();
        var goodServices = new ServiceCollection();
        goodServices.AddZabbix().Bind(good);
        var badServices = new ServiceCollection();
        badServices.AddZabbix().Bind(bad);
        using ServiceProvider goodProvider = goodServices.BuildValidated();
        using ServiceProvider badProvider = badServices.BuildValidated();

        Assert.Equal(TimeSpan.FromMinutes(10), goodProvider.GetRequiredService<IOptions<ZabbixOptions>>().Value.Timeouts["configuration.import"]);
        OptionsValidationException failure = Assert.Throws<OptionsValidationException>(() => badProvider.GetRequiredService<IStartupValidator>().Validate());
        Assert.Contains("Timeouts:host.get", failure.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void A_trapper_section_binds_the_same_row_a_fluent_add_trapper_would()
    {
        IConfiguration section = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["Port"] = "0",
            ["AllowedPeers:0"] = "10.0.0.0/8",
            ["MaxConcurrentConnections"] = "500",
        }).Build();
        var services = new ServiceCollection();
        services.AddZabbixPushAgent("edge").AddServer("zbx1").AddTrapper(section);
        using ServiceProvider provider = services.BuildValidated();

        ZabbixListenerOptions options = provider.GetRequiredService<IOptionsMonitor<ZabbixListenerOptions>>().Get("edge");
        Assert.Equal(0, options.Port);
        Assert.Equal(["10.0.0.0/8"], options.AllowedPeers);
        Assert.Equal(500, options.MaxConcurrentConnections);
        provider.GetRequiredService<IStartupValidator>().Validate();
    }

    [Fact]
    public void A_trapper_section_that_omits_the_port_still_gets_the_trapper_port()
    {
        IConfiguration section = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["AllowedPeers:0"] = "10.0.0.0/8",
        }).Build();
        var services = new ServiceCollection();
        services.AddZabbixPushAgent("edge").AddTrapper(section);
        using ServiceProvider provider = services.BuildValidated();

        Assert.Equal(ZabbixServerNode.DefaultPort, provider.GetRequiredService<IOptionsMonitor<ZabbixListenerOptions>>().Get("edge").Port);
    }

    [Fact]
    public void A_proxy_row_is_active_unless_its_mode_says_passive_as_upstream_s_proxymode_is()
    {
        IConfiguration document = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["Proxies:edge:Server"] = "zbx-1",
            ["Proxies:edge:Trapper:AllowedPeers:0"] = "127.0.0.1",
            ["Proxies:dmz:Mode"] = "Passive",
            ["Proxies:dmz:Version"] = "7.4",
            ["Proxies:dmz:Trapper:AllowedPeers:0"] = "127.0.0.1",
            ["Proxies:dmz1:Mode"] = "1",                                                // ProxyMode's number reads too
            ["Proxies:dmz1:Version"] = "7.4",
            ["Proxies:dmz1:Trapper:AllowedPeers:0"] = "127.0.0.1",
        }).Build();
        var services = new ServiceCollection();

        services.AddZabbixProxies(document);

        Assert.Contains(services, descriptor => descriptor.IsKeyedService && descriptor.ServiceType == typeof(ZabbixActiveProxy) && Equals(descriptor.ServiceKey, "edge"));
        Assert.Contains(services, descriptor => descriptor.IsKeyedService && descriptor.ServiceType == typeof(ZabbixPassiveProxy) && Equals(descriptor.ServiceKey, "dmz"));
        Assert.Contains(services, descriptor => descriptor.IsKeyedService && descriptor.ServiceType == typeof(ZabbixPassiveProxy) && Equals(descriptor.ServiceKey, "dmz1"));
        Assert.Null(typeof(ZabbixProxyOptions).GetProperty("Mode"));                       // the class is the role, not an option
    }

    [Fact]
    public void A_mode_left_on_a_row_bound_to_a_proxy_directly_is_passed_over()
    {
        // Mode only steers AddZabbixProxies's choice of class; code that already chose one binds the rest of the row, and a
        // stray Mode the row happens to carry (from a shared base configuration) changes nothing.
        IConfiguration row = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["Mode"] = "Passive",
            ["Server"] = "zbx1",
            ["Trapper:AllowedPeers:0"] = "10.0.0.0/8",
        }).Build();
        var services = new ServiceCollection();
        services.AddZabbixActiveProxy("p").Bind(row).AddTrapper(0);
        using ServiceProvider provider = services.BuildValidated();

        Assert.Equal("zbx1", provider.GetRequiredKeyedService<ZabbixActiveProxy>("p").Options.Server);
    }
}
