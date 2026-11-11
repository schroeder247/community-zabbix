// This Source Code Form is subject to the terms of the Mozilla Public
// License, v. 2.0. Copyright (c) 2026 schroeder247. SPDX-License-Identifier: MPL-2.0
using System.Net;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;

namespace Community.Zabbix.Tests;

/// <summary>
/// An agent's <c>Items</c> is one table of its own, live while the agent runs: a key added after the start is answered, the
/// built-in keys fill the names left free, and two agents never share one. The PowerShell sessions add their scriptblocks here.
/// </summary>
public sealed class AgentItemsTests
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private static readonly Community.Zabbix.Items.ZabbixItemCollector s_collector = new(Community.Zabbix.Items.ZabbixHostFiles.Host, TimeProvider.System);

    [Fact]
    public async Task A_key_added_to_a_registered_agent_after_the_start_is_answered()
    {
        using IHost host = await TestHost.StartAsync(services => services.AddZabbixPassiveAgent("edge")
            .Configure(o => { o.Address = "127.0.0.1"; o.Port = 0; o.Hostname = "edge-01"; o.AllowedPeers.Add("127.0.0.1"); }), Ct);
        try
        {
            ZabbixPassiveAgent agent = host.Services.GetRequiredKeyedService<ZabbixPassiveAgent>("edge");
            var bound = (IPEndPoint)agent.Listener!.LocalEndPoint!;
            using var client = new ZabbixGetter(new ZabbixServerNode(bound.Address.ToString(), bound.Port));
            Assert.False((await client.GetAsync("app.late", Ct)).IsSupported);

            agent.Items.Add("app.late", _ => "late");

            Assert.Equal("late", (await client.GetAsync("app.late", Ct)).Value);
            Assert.Equal("edge-01", (await client.GetAsync("agent.hostname", Ct)).Value);
        }
        finally
        {
            await host.StopAsync(Ct);
        }
    }

    [Fact]
    public void The_built_in_keys_fill_only_the_names_left_free()
    {
        ZabbixItemCallback own = (key, _) => ValueTask.FromResult(new ZabbixAgentAnswer(key, "own", null));
        var items = new ZabbixItemDictionary().Add("agent.hostname", own);

        items.AddDefaults("edge-01", null, bundled: true, s_collector);

        Assert.Same(own, items["agent.hostname"]);
        Assert.True(items.ContainsKey("agent.ping"));
        Assert.True(items.ContainsKey("vfs.fs.size"));
    }

    [Fact]
    public void Add_refuses_a_name_held_and_the_indexer_replaces_it()
    {
        ZabbixItemCallback first = (key, _) => ValueTask.FromResult(new ZabbixAgentAnswer(key, "first", null));
        ZabbixItemCallback second = (key, _) => ValueTask.FromResult(new ZabbixAgentAnswer(key, "second", null));
        var items = new ZabbixItemDictionary().Add("app.one", first);

        Assert.Throws<ArgumentException>(() => items.Add("app.one", second));
        items["app.one"] = second;

        Assert.Same(second, items["app.one"]);
    }

    [Theory]
    [InlineData(42L, "42")]
    [InlineData(-7L, "-7")]
    public async Task A_whole_number_answer_is_formatted_as_Zabbix_reads_it(long value, string expected)
    {
        var items = new ZabbixItemDictionary().Add("app.count", _ => value).Add("app.count.async", (_, _) => ValueTask.FromResult(value));

        Assert.Equal(expected, (await items.AnswerAsync(ZabbixItemKey.Parse("app.count", null), TimeSpan.FromSeconds(3), Ct)).Value);
        Assert.Equal(expected, (await items.AnswerAsync(ZabbixItemKey.Parse("app.count.async", null), TimeSpan.FromSeconds(3), Ct)).Value);
    }

    [Fact]
    public async Task A_number_answer_ignores_the_current_culture_and_refuses_what_Zabbix_cannot_store()
    {
        var items = new ZabbixItemDictionary().Add("app.ratio", _ => 1.5).Add("app.nan", _ => double.NaN);
        System.Globalization.CultureInfo before = System.Globalization.CultureInfo.CurrentCulture;
        System.Globalization.CultureInfo.CurrentCulture = System.Globalization.CultureInfo.GetCultureInfo("de-DE");
        try
        {
            Assert.Equal("1.5", (await items.AnswerAsync(ZabbixItemKey.Parse("app.ratio", null), TimeSpan.FromSeconds(3), Ct)).Value);
        }
        finally
        {
            System.Globalization.CultureInfo.CurrentCulture = before;
        }
        Assert.False((await items.AnswerAsync(ZabbixItemKey.Parse("app.nan", null), TimeSpan.FromSeconds(3), Ct)).IsSupported);
    }

    [Fact]
    public void Two_agents_in_one_container_keep_their_own_tables_and_key_rules()
    {
        var services = new ServiceCollection();
        services.AddZabbixActiveAgent("a").Configure(o => { o.ServerActive = "127.0.0.1"; o.KeyRules.Add("DenyKey=*"); });
        services.AddZabbixActiveAgent("b").Configure(o => o.ServerActive = "127.0.0.1");
        using ServiceProvider provider = services.BuildServiceProvider();

        ZabbixActiveAgent a = provider.GetRequiredKeyedService<ZabbixActiveAgent>("a");
        ZabbixActiveAgent b = provider.GetRequiredKeyedService<ZabbixActiveAgent>("b");

        Assert.NotSame(a.Items, b.Items);
    }

    [Fact]
    public async Task An_agent_s_key_rules_are_its_own_when_two_run_in_one_process()
    {
        await using var denying = new ZabbixPassiveAgent(Loopback("denying", "DenyKey=*"));
        await using var answering = new ZabbixPassiveAgent(Loopback("answering"));
        await denying.StartAsync(Ct);
        await answering.StartAsync(Ct);

        Assert.False((await denying.Items.AnswerAsync(ZabbixItemKey.Parse("agent.ping", null), TimeSpan.FromSeconds(3), Ct)).IsSupported);
        Assert.Equal("1", (await answering.Items.AnswerAsync(ZabbixItemKey.Parse("agent.ping", null), TimeSpan.FromSeconds(3), Ct)).Value);
    }

    private static ZabbixPassiveAgentOptions Loopback(string hostname, params string[] rules)
    {
        var options = new ZabbixPassiveAgentOptions { Hostname = hostname, Address = "127.0.0.1", Port = 0 };
        options.AllowedPeers.Add("127.0.0.1");
        foreach (string rule in rules) options.KeyRules.Add(rule);
        return options;
    }
}
