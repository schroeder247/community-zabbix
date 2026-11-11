// This Source Code Form is subject to the terms of the Mozilla Public
// License, v. 2.0. Copyright (c) 2026 schroeder247. SPDX-License-Identifier: MPL-2.0
using System.Net;
using System.Text.Json;
using Community.Zabbix.Proxy;
using Community.Zabbix.Storage;

namespace Community.Zabbix.Tests;

/// <summary>
/// A proxy admits a trapped value by Zabbix's own rules (<c>sender_item_validator</c>,
/// <c>agent_item_validator</c>): the item's type, its <c>trapper_hosts</c> with macros resolved, the literal key, and,
/// per protocol version, what an empty allowed-hosts list means.
/// </summary>
public sealed class ProxyAdmissionTests
{
    private static readonly ZabbixComponentVersion V70 = new(7, 0, 31);
    private static readonly ZabbixComponentVersion V80 = new(8, 0, 0);

    private static ZabbixProxyConfig Config(string items, string macros = "")
    {
        string reply = """
            {"full_sync":1,"config_revision":1,"data":{
             "hosts":{"fields":["hostid","host","status","name"],"data":[[10,"web-01",0,"Web 01"],[11,"off-01",1,"Off 01"]]},
             "items":{"fields":["itemid","type","hostid","key_","status","trapper_hosts","allow_traps"],"data":[
            """ + items + "]}" + macros + "}}";
        using JsonDocument document = JsonDocument.Parse(reply);
        return ZabbixProxyConfig.Empty.Merge(document.RootElement);
    }

    private static ValueTask<ZabbixProxyAdmission> RuleAsync(ZabbixProxyConfig config, ZabbixComponentVersion? protocol = null, ZabbixPeerResolverCallback? resolve = null)
        => ZabbixProxyAdmission.BuildAsync(config, protocol ?? V70, resolve ?? ((_, _) => ValueTask.FromResult<IReadOnlyList<IPAddress>>([])), TestContext.Current.CancellationToken);

    private static ZabbixValue Value(string key, string host = "web-01") => new(host, key, "1", DateTimeOffset.UnixEpoch);

    [Theory]
    [InlineData(2, 0, null)]
    [InlineData(19, 1, null)]
    [InlineData(19, 0, "trapping is not enabled")]
    [InlineData(0, 0, "item type \"0\" cannot be used with traps")]
    [InlineData(7, 0, "item type \"7\" cannot be used with traps")]
    public async Task Sender_data_is_taken_by_a_trapper_item_or_an_http_agent_item_that_allows_traps(int type, int allowTraps, string? refusal)
    {
        ZabbixProxyAdmission rule = await RuleAsync(Config($"[100,{type},10,\"k\",0,\"\",{allowTraps}]"));

        Assert.Equal(refusal, rule.Refusal(Value("k"), "sender data", IPAddress.Loopback));
    }

    [Fact]
    public async Task Agent_data_is_taken_only_by_an_active_agent_item()
    {
        ZabbixProxyAdmission rule = await RuleAsync(Config("""[100,7,10,"active",0,"",0],[101,2,10,"trap",0,"",0]"""));

        Assert.Null(rule.Refusal(Value("active"), "agent data", IPAddress.Loopback));
        Assert.NotNull(rule.Refusal(Value("trap"), "agent data", IPAddress.Loopback));
    }

    [Theory]
    [InlineData("7.0", true)]
    [InlineData("8.0", false)]
    public async Task An_empty_trapper_hosts_admits_everyone_on_7_x_and_no_one_from_8_0(string protocol, bool admitted)
    {
        ZabbixProxyAdmission rule = await RuleAsync(Config("""[100,2,10,"k",0,"",0]"""), protocol == "7.0" ? V70 : V80);

        Assert.Equal(admitted, rule.Refusal(Value("k"), "sender data", IPAddress.Parse("192.0.2.7")) is null);
    }

    [Fact]
    public async Task The_8_0_default_resolves_through_the_global_macro_and_a_mapped_peer_matches_its_ipv4_form()
    {
        using JsonDocument document = JsonDocument.Parse(ProxyConfigFixtures.Full80);
        ZabbixProxyConfig config = ZabbixProxyConfig.Empty.Merge(document.RootElement);
        ZabbixProxyAdmission rule = await RuleAsync(config, V80);
        ZabbixValue value = Value("zbx.trap", "zbx-proxy-h1");

        Assert.Null(rule.Refusal(value, "sender data", IPAddress.Loopback));
        Assert.Null(rule.Refusal(value, "sender data", IPAddress.Parse("::ffff:127.0.0.1")));
        Assert.Null(rule.Refusal(value, "sender data", IPAddress.IPv6Loopback));
        Assert.StartsWith("connection from \"192.0.2.7\" rejected", rule.Refusal(value, "sender data", IPAddress.Parse("192.0.2.7")), StringComparison.Ordinal);
    }

    [Fact]
    public async Task A_host_macro_is_found_before_its_template_s_and_the_template_s_before_the_global()
    {
        const string Macros = """
            ,"globalmacro":{"fields":["globalmacroid","macro","value","type"],"data":[[1,"{$A}","203.0.113.1",0],[2,"{$B}","203.0.113.2",0],[3,"{$C}","203.0.113.3",0]]},
            "hosts_templates":{"fields":["hosttemplateid","hostid","templateid","link_type"],"data":[[1,10,20,0]]},
            "hostmacro":{"fields":["hostmacroid","hostid","macro","value","type","automatic"],"data":[[1,10,"{$A}","198.51.100.1",0,0],[2,20,"{$A}","198.51.100.9",0,0],[3,20,"{$B}","198.51.100.2",0,0]]}
            """;
        ZabbixProxyAdmission rule = await RuleAsync(Config("""[100,2,10,"a",0,"{$A}",0],[101,2,10,"b",0,"{$B}",0],[102,2,10,"c",0,"{$C}",0]""", Macros));

        Assert.Null(rule.Refusal(Value("a"), "sender data", IPAddress.Parse("198.51.100.1")));    // the host's
        Assert.Null(rule.Refusal(Value("b"), "sender data", IPAddress.Parse("198.51.100.2")));    // the template's
        Assert.Null(rule.Refusal(Value("c"), "sender data", IPAddress.Parse("203.0.113.3")));     // the global
        Assert.NotNull(rule.Refusal(Value("a"), "sender data", IPAddress.Parse("198.51.100.9")));
    }

    [Fact]
    public async Task Cidr_blocks_and_dns_names_in_trapper_hosts_are_honoured()
    {
        ZabbixPeerResolverCallback resolve = (name, _) => ValueTask.FromResult<IReadOnlyList<IPAddress>>(name == "collector.example" ? [IPAddress.Parse("192.0.2.50")] : []);
        ZabbixProxyAdmission rule = await RuleAsync(Config("""[100,2,10,"k",0,"10.0.0.0/8,collector.example",0]"""), resolve: resolve);

        Assert.Null(rule.Refusal(Value("k"), "sender data", IPAddress.Parse("10.20.30.40")));
        Assert.Null(rule.Refusal(Value("k"), "sender data", IPAddress.Parse("192.0.2.50")));
        Assert.NotNull(rule.Refusal(Value("k"), "sender data", IPAddress.Parse("192.0.2.51")));
    }

    [Fact]
    public async Task The_key_and_the_host_are_matched_literally()
    {
        ZabbixProxyAdmission rule = await RuleAsync(Config("""[100,2,10,"cpu",0,"",0]"""));

        Assert.Null(rule.Refusal(Value("cpu"), "sender data", IPAddress.Loopback));
        Assert.NotNull(rule.Refusal(Value("CPU"), "sender data", IPAddress.Loopback));
        Assert.NotNull(rule.Refusal(Value("cpu", "WEB-01"), "sender data", IPAddress.Loopback));
    }

    [Fact]
    public async Task A_disabled_item_or_an_unmonitored_host_is_refused()
    {
        ZabbixProxyAdmission rule = await RuleAsync(Config("""[100,2,10,"off",1,"",0],[101,2,11,"k",0,"",0]"""));

        Assert.Equal("the item is disabled", rule.Refusal(Value("off"), "sender data", IPAddress.Loopback));
        Assert.Equal("the host is not monitored", rule.Refusal(Value("k", "off-01"), "sender data", IPAddress.Loopback));
        Assert.False(rule.Knows(Value("off")));
    }

    [Fact]
    public async Task The_intake_writes_what_the_rule_admits_and_reports_each_refusal()
    {
        ZabbixProxyAdmission rule = await RuleAsync(Config("""[100,2,10,"k",0,"192.0.2.1",0]"""));
        await using var store = new ZabbixMemoryStore<ZabbixValue>();
        var refused = new List<string>();
        System.Threading.Channels.ChannelWriter<ZabbixValue> fromAllowed = rule.For(store, "sender data", IPAddress.Parse("192.0.2.1"), (_, reason) => refused.Add(reason));
        System.Threading.Channels.ChannelWriter<ZabbixValue> fromOther = rule.For(store, "sender data", IPAddress.Parse("192.0.2.2"), (_, reason) => refused.Add(reason));

        Assert.True(fromAllowed.TryWrite(Value("k")));
        Assert.False(fromOther.TryWrite(Value("k")));

        Assert.Equal(1, store.Count);
        Assert.Single(refused);
    }
}
