// This Source Code Form is subject to the terms of the Mozilla Public
// License, v. 2.0. Copyright (c) 2026 schroeder247. SPDX-License-Identifier: MPL-2.0
using System.Net;
using System.Text;
using System.Text.Json;
using Community.Zabbix.Listening;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;

namespace Community.Zabbix.Tests;

/// <summary>The passive agent: a listener whose handler answers keys in both of the server's forms.</summary>
public sealed class PassiveAgentTests
{
    [Fact]
    public async Task The_classic_form_answers_a_value_or_not_supported_with_the_reason()
    {
        var agent = new ZabbixPassiveCheckResponder(ZabbixItemDictionary.BuiltIn("web-01").Add("custom.echo", key => key[0]));

        ReadOnlyMemory<byte> ping = await agent.Handler(new ZabbixRequest("agent.ping\n"u8.ToArray(), null), TestContext.Current.CancellationToken);
        ReadOnlyMemory<byte> echo = await agent.Handler(new ZabbixRequest("custom.echo[hello]"u8.ToArray(), null), TestContext.Current.CancellationToken);
        ReadOnlyMemory<byte> unknown = await agent.Handler(new ZabbixRequest("app.unknown[/]"u8.ToArray(), null), TestContext.Current.CancellationToken);

        Assert.Equal("1", Encoding.UTF8.GetString(ping.Span));
        Assert.Equal("hello", Encoding.UTF8.GetString(echo.Span));
        Assert.StartsWith("ZBX_NOTSUPPORTED\0This agent does not answer the key.", Encoding.UTF8.GetString(unknown.Span), StringComparison.Ordinal);
    }

    [Fact]
    public async Task Add_refuses_a_key_already_registered_and_the_indexer_replaces_it()
    {
        ZabbixItemDictionary handlers = ZabbixItemDictionary.BuiltIn("web-01");         // Add means what Dictionary.Add means

        ArgumentException refused = Assert.Throws<ArgumentException>(() => handlers.Add("agent.ping", _ => "2"));
        handlers["agent.ping"] = (key, _) => ValueTask.FromResult(ZabbixAgentAnswer.Supported(key, "2"));

        Assert.Contains("agent.ping", refused.Message, StringComparison.Ordinal);
        Assert.Equal("2", (await handlers.AnswerAsync(ZabbixItemKey.Parse("agent.ping", null), TimeSpan.FromSeconds(1), TimeProvider.System, TestContext.Current.CancellationToken)).Value);
    }

    [Fact]
    public async Task An_async_handler_answers_its_value_and_its_throw_is_the_not_supported_reason()
    {
        var handlers = new ZabbixItemDictionary()
            .Add("custom.async", async (key, cancellationToken) => { await Task.Yield(); return key[0]; })
            .Add("custom.broken", (key, _) => ValueTask.FromException<string>(new InvalidOperationException("The source is offline.")));

        ZabbixAgentAnswer value = await handlers.AnswerAsync(ZabbixItemKey.Parse("custom.async[hello]", null), TimeSpan.FromSeconds(5), TimeProvider.System, TestContext.Current.CancellationToken);
        ZabbixAgentAnswer broken = await handlers.AnswerAsync(ZabbixItemKey.Parse("custom.broken", null), TimeSpan.FromSeconds(5), TimeProvider.System, TestContext.Current.CancellationToken);

        Assert.Equal(ZabbixAgentAnswer.Supported(ZabbixItemKey.Parse("custom.async[hello]", null), "hello"), value);
        Assert.Equal(ZabbixAgentAnswer.NotSupported(ZabbixItemKey.Parse("custom.broken", null), "The source is offline."), broken);
    }

    [Fact]
    public void The_answer_factories_say_supported_or_not_and_the_indexer_names_its_parameter_as_Add_does()
    {
        ZabbixItemKey key = ZabbixItemKey.Parse("agent.ping", null);

        Assert.True(ZabbixAgentAnswer.Supported(key, null).IsSupported);
        Assert.False(ZabbixAgentAnswer.NotSupported(key, "This agent does not answer the key.").IsSupported);
        Assert.Throws<ArgumentNullException>(() => ZabbixAgentAnswer.NotSupported(key, null!));
        Assert.Equal(
            typeof(ZabbixItemDictionary).GetMethod(nameof(ZabbixItemDictionary.Add), [typeof(string), typeof(ZabbixItemCallback)])!.GetParameters()[0].Name,
            typeof(ZabbixItemDictionary).GetProperty("Item", [typeof(string)])!.GetIndexParameters()[0].Name);
    }

    [Fact]
    public async Task A_hosted_agent_s_own_handler_replaces_the_built_in_one_of_that_name()
    {
        var services = new ServiceCollection();
        services.AddZabbixPassiveAgent("edge").Configure(o => { o.Address = "127.0.0.1"; o.Port = 0; o.AllowedPeers.Add("127.0.0.1"); }).AddItem("agent.ping", _ => "own");
        using ServiceProvider provider = services.BuildValidated();
        var hosted = (IHostedLifecycleService)provider.GetServices<IHostedService>().Single();
        await hosted.StartingAsync(TestContext.Current.CancellationToken);
        await hosted.StartAsync(TestContext.Current.CancellationToken);
        var bound = (IPEndPoint)provider.GetRequiredKeyedService<ZabbixPassiveAgent>("edge").Listener!.LocalEndPoint!;

        using ZabbixGetter getter = new ZabbixGetter(new ZabbixServerNode(bound.Address.ToString(), bound.Port));
        ZabbixAgentAnswer ping = await getter.GetAsync("agent.ping", TestContext.Current.CancellationToken);

        Assert.Equal("own", ping.Value);
        await hosted.StopAsync(TestContext.Current.CancellationToken);
    }

    [Fact]
    public async Task The_json_form_answers_every_key_with_a_value_or_an_error()
    {
        var agent = new ZabbixPassiveCheckResponder(ZabbixItemDictionary.BuiltIn("web-01").Add("slow", async (key, token) => { await Task.Delay(Timeout.InfiniteTimeSpan, token); return new ZabbixAgentAnswer(key, "never", null); }));

        ReadOnlyMemory<byte> reply = await agent.Handler(new ZabbixRequest("""{"request":"passive checks","data":[{"key":"agent.hostname"},{"key":"nope"},{"key":"slow","timeout":1}]}"""u8.ToArray(), null), TestContext.Current.CancellationToken);

        using JsonDocument answer = JsonDocument.Parse(reply);
        JsonElement data = answer.RootElement.GetProperty("data");
        Assert.Equal("7.4.0", answer.RootElement.GetProperty("version").GetString());
        Assert.Equal("web-01", data[0].GetProperty("value").GetString());
        Assert.Equal("This agent does not answer the key.", data[1].GetProperty("error").GetString());
        Assert.Contains("deadline", data[2].GetProperty("error").GetString(), StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("""{"request":"active checks","data":[{"key":"agent.ping","timeout":3}]}""", "unknown request \"active checks\"")]
    [InlineData("""{"data":[{"key":"agent.ping","timeout":3}]}""", "cannot find the \"request\" object in the received JSON object")]
    [InlineData("""{"request":"passive checks"}""", "cannot find the \"data\" array in the received JSON object")]
    [InlineData("""{"request":"passive checks","data":[]}""", "received empty \"data\" tag")]
    public async Task A_json_request_that_is_not_a_passive_check_is_answered_in_json_with_the_reason(string request, string reason)
    {
        // listener.c 8.0:54-144 answers such a request in JSON, so the agent does too, not in plaintext.
        var agent = new ZabbixPassiveCheckResponder(ZabbixItemDictionary.BuiltIn("web-01"));

        using JsonDocument answer = JsonDocument.Parse(await agent.Handler(new ZabbixRequest(Encoding.UTF8.GetBytes(request), null), TestContext.Current.CancellationToken));

        Assert.Equal(reason, answer.RootElement.GetProperty("error").GetString());
        Assert.Equal("7.4.0", answer.RootElement.GetProperty("version").GetString());
    }

    [Theory]
    [InlineData(0)]
    [InlineData(601)]
    public async Task A_check_timeout_outside_1_to_600_seconds_is_that_key_s_error(int timeout)
    {
        var agent = new ZabbixPassiveCheckResponder(ZabbixItemDictionary.BuiltIn("web-01"));

        using JsonDocument answer = JsonDocument.Parse(await agent.Handler(new ZabbixRequest(Encoding.UTF8.GetBytes($$"""{"request":"passive checks","data":[{"key":"agent.ping","timeout":{{timeout}}}]}"""), null), TestContext.Current.CancellationToken));

        Assert.Equal("Unsupported timeout value.", answer.RootElement.GetProperty("data")[0].GetProperty("error").GetString());
    }

    [Fact]
    public async Task Text_that_only_looks_like_json_is_read_as_a_classic_key_as_upstream_reads_it()
    {
        var agent = new ZabbixPassiveCheckResponder(ZabbixItemDictionary.BuiltIn("web-01"));

        ReadOnlyMemory<byte> reply = await agent.Handler(new ZabbixRequest("{not json"u8.ToArray(), null), TestContext.Current.CancellationToken);

        Assert.StartsWith("ZBX_NOTSUPPORTED\0", Encoding.UTF8.GetString(reply.Span), StringComparison.Ordinal);
    }

    [Fact]
    public async Task A_hosted_passive_agent_answers_the_getter_over_the_platform_s_transport()
    {
        var services = new ServiceCollection();
        services.AddZabbixPassiveAgent("edge").Configure(o => { o.Address = "127.0.0.1"; o.Port = 0; o.Hostname = "edge-01"; o.AllowedPeers.Add("127.0.0.1"); }).AddItem("custom.answer", _ => "42");
        using ServiceProvider provider = services.BuildValidated();
        var hosted = (IHostedLifecycleService)provider.GetServices<IHostedService>().Single();
        await hosted.StartingAsync(TestContext.Current.CancellationToken);
        await hosted.StartAsync(TestContext.Current.CancellationToken);
        var bound = (IPEndPoint)provider.GetRequiredKeyedService<ZabbixPassiveAgent>("edge").Listener!.LocalEndPoint!;
        using var getter = new ZabbixGetter(new ZabbixServerNode(bound.Address.ToString(), bound.Port));

        ZabbixAgentAnswer hostname = await getter.GetAsync("agent.hostname", TestContext.Current.CancellationToken);
        ZabbixAgentAnswer custom = await getter.GetAsync("custom.answer", TestContext.Current.CancellationToken);
        ZabbixAgentAnswer missing = await getter.GetAsync("missing.key", TestContext.Current.CancellationToken);

        Assert.Equal("edge-01", hostname.Value);
        Assert.Equal("42", custom.Value);
        Assert.False(missing.IsSupported);
        await hosted.StoppingAsync(TestContext.Current.CancellationToken);
        await hosted.StopAsync(TestContext.Current.CancellationToken);
        await hosted.StoppedAsync(TestContext.Current.CancellationToken);
    }

    [Fact]
    public void Adding_a_passive_agent_twice_by_name_hosts_one_agent_with_both_keys()
    {
        var services = new ServiceCollection();
        services.AddZabbixPassiveAgent("edge").Configure(o => o.AllowedPeers.Add("127.0.0.1")).AddItem("custom.one", _ => "1");
        services.AddZabbixPassiveAgent("edge").AddItem("custom.two", _ => "2");
        using ServiceProvider provider = services.BuildValidated();

        Assert.Single(provider.GetServices<IHostedService>());
        Assert.Equal(2, provider.GetKeyedServices<ZabbixItemDictionary>(new ZabbixItemsKey("edge")).Count());
    }

    [Fact]
    public void A_passive_agent_without_allowed_peers_fails_validation_at_start()
    {
        var services = new ServiceCollection();
        services.AddZabbixPassiveAgent("edge");
        using ServiceProvider provider = services.BuildValidated();

        Assert.Throws<Microsoft.Extensions.Options.OptionsValidationException>(() => provider.GetRequiredService<Microsoft.Extensions.Options.IOptionsMonitor<ZabbixPassiveAgentOptions>>().Get("edge"));
    }

    [Fact]
    public void Allow_peer_writes_zabbix_s_server_line_from_an_address_a_network_and_a_name()
    {
        using ZabbixPassiveAgent agent = ZabbixPassiveAgent.CreateBuilder("edge")
            .AllowPeer(IPAddress.Parse("192.0.2.10"))
            .AllowPeer(IPNetwork.Parse("10.0.0.0/8"))
            .AllowPeer(IPNetwork.Parse("fd00::/8"))
            .AllowPeer("zbx-01.example")
            .Build();

        Assert.Equal(["192.0.2.10", "10.0.0.0/8", "fd00::/8", "zbx-01.example"], agent.Options.AllowedPeers);
        Assert.Equal(4, agent.Options.ParseAllowedPeers().Count);
    }

    [Theory]
    [InlineData("10.0.0.5")]                  // an address is an IPAddress
    [InlineData("10.0.0.0/8")]                // a network is an IPNetwork
    [InlineData("zbx-01.example,zbx-02")]     // one peer a call
    [InlineData(" ")]
    public void Allow_peer_takes_a_dns_name_as_text_and_refuses_anything_else_where_it_is_passed(string hostName)
        => Assert.Equal("hostName", Assert.ThrowsAny<ArgumentException>(() => ZabbixPassiveAgent.CreateBuilder("edge").AllowPeer(hostName)).ParamName);

    [Fact]
    public async Task A_built_passive_agent_answers_a_getter_with_the_name_as_its_host()
    {
        await using ZabbixPassiveAgent agent = ZabbixPassiveAgent.CreateBuilder("edge-01")
            .AllowPeer(IPAddress.Loopback)
            .Configure(o => { o.Address = "127.0.0.1"; o.Port = 0; })
            .AddItem(new ZabbixItemKey<long>("custom.answer"), () => 42L)
            .Build();
        await agent.StartAsync(TestContext.Current.CancellationToken);
        var bound = (IPEndPoint)agent.Listener!.LocalEndPoint!;
        using var getter = new ZabbixGetter($"127.0.0.1:{bound.Port}");

        Assert.Equal("edge-01", (await getter.GetAsync("agent.hostname", TestContext.Current.CancellationToken)).Value);
        Assert.Equal("42", (await getter.GetAsync("custom.answer", TestContext.Current.CancellationToken)).Value);
    }
}
