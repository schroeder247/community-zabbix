// This Source Code Form is subject to the terms of the Mozilla Public
// License, v. 2.0. Copyright (c) 2026 schroeder247. SPDX-License-Identifier: MPL-2.0
using System.Text.Json;
using Community.Zabbix.Proxy;

namespace Community.Zabbix.Tests;

/// <summary>
/// The configuration a proxy holds is the server's full sync merged with every delta after it, by id,
/// frozen per revision (<c>proxyconfigwrite.c</c>), shown on replies captured from 8.0.0rc1.
/// </summary>
public sealed class ProxyConfigTests
{
    private static ZabbixProxyConfig Merge(ZabbixProxyConfig held, string reply)
    {
        using JsonDocument document = JsonDocument.Parse(reply);
        return held.Merge(document.RootElement);
    }

    [Fact]
    public void A_delta_merges_by_id_and_every_host_not_in_it_keeps_its_items()
    {
        ZabbixProxyConfig full = Merge(ZabbixProxyConfig.Empty, ProxyConfigFixtures.Full80);

        ZabbixProxyConfig merged = Merge(full, ProxyConfigFixtures.Delta80);

        Assert.Equal(1795, merged.ConfigRevision);
        Assert.Equal(["zbx-proxy-h1", "zbx-proxy-h2"], merged.Hosts.Values.Select(host => host.HostName).Order(StringComparer.Ordinal));
        Assert.Equal(71029UL, merged.ItemsByKey[("zbx-proxy-h1", "zbx.trap")].ItemId);            // not in the delta, kept
        Assert.Equal(71042UL, merged.ItemsByKey[("zbx-proxy-h2", "zbx.trap2")].ItemId);
        Assert.Equal(1793, full.ConfigRevision);                                                 // the held revision is not changed
        Assert.Single(full.Items);
    }

    [Fact]
    public void A_full_sync_replaces_what_was_held()
    {
        ZabbixProxyConfig held = Merge(Merge(ZabbixProxyConfig.Empty, ProxyConfigFixtures.Full80), ProxyConfigFixtures.Delta80);

        ZabbixProxyConfig replaced = Merge(held, ProxyConfigFixtures.Full80);

        Assert.Equal(["zbx-proxy-h1"], replaced.Hosts.Values.Select(host => host.HostName));
        Assert.Single(replaced.Items);
    }

    [Fact]
    public void An_empty_reply_keeps_the_configuration_held()
    {
        ZabbixProxyConfig held = Merge(ZabbixProxyConfig.Empty, ProxyConfigFixtures.Full80);

        Assert.Same(held, Merge(held, "{}"));
    }

    [Fact]
    public void A_reply_with_only_a_revision_moves_the_revision_and_keeps_the_tables()
    {
        ZabbixProxyConfig held = Merge(ZabbixProxyConfig.Empty, ProxyConfigFixtures.Full80);

        ZabbixProxyConfig moved = Merge(held, """{"config_revision":1800}""");

        Assert.Equal(1800, moved.ConfigRevision);
        Assert.Equal(held.Items.Keys, moved.Items.Keys);
    }

    [Fact]
    public void A_host_in_a_delta_has_exactly_the_delta_s_items()
    {
        ZabbixProxyConfig held = Merge(ZabbixProxyConfig.Empty, """
            {"full_sync":1,"config_revision":1,"data":{"hosts":{"fields":["hostid","host","status"],"data":[[10,"web-01",0]]},
             "items":{"fields":["itemid","type","hostid","key_"],"data":[[100,2,10,"a"],[101,2,10,"b"]]}}}
            """);

        ZabbixProxyConfig merged = Merge(held, """
            {"config_revision":2,"data":{"hosts":{"fields":["hostid","host","status"],"data":[[10,"web-01",0]]},
             "items":{"fields":["itemid","type","hostid","key_"],"data":[[101,2,10,"b"]]}}}
            """);

        Assert.Equal([101L], merged.Items.Keys);                                                  // item 100 was deleted on the server
    }

    [Fact]
    public void Del_hostids_take_the_host_and_its_items_away()
    {
        ZabbixProxyConfig held = Merge(Merge(ZabbixProxyConfig.Empty, ProxyConfigFixtures.Full80), ProxyConfigFixtures.Delta80);

        // Shaped as proxyconfigread.c writes a delta (8.0:1322, 1440-1447, 1603): the data object, the removed hosts under
        // ZBX_PROTO_TAG_REMOVED_HOSTIDS, which is "del_hostids" (zbxjson.h 7.0:200, 8.0:204), then the revision.
        ZabbixProxyConfig removed = Merge(held, """{"data":{},"del_hostids":[10828],"config_revision":1796}""");

        Assert.Equal(["zbx-proxy-h2"], removed.Hosts.Values.Select(host => host.HostName));
        Assert.Equal([71042L], removed.Items.Keys);
    }

    [Fact]
    public void The_hostmap_revision_is_read_inside_proxy_group()
    {
        ZabbixProxyConfig merged = Merge(ZabbixProxyConfig.Empty, """{"config_revision":5,"hostmap_revision":99,"proxy_group":{"hostmap_revision":7,"failover_delay":"1m"}}""");

        Assert.Equal(7, merged.HostmapRevision);
    }

    [Fact]
    public void The_global_macros_arrive_whole_and_host_macros_per_host()
    {
        ZabbixProxyConfig merged = Merge(ZabbixProxyConfig.Empty, """
            {"full_sync":1,"config_revision":1,"data":{
             "globalmacro":{"fields":["globalmacroid","macro","value","type"],"data":[[3,"{$TRAPPER.ALLOWED_HOSTS}","127.0.0.1,::1",0]]},
             "hosts_templates":{"fields":["hosttemplateid","hostid","templateid","link_type"],"data":[[1,10,20,0]]},
             "hostmacro":{"fields":["hostmacroid","hostid","macro","value","type","automatic"],"data":[[1,20,"{$PEERS}","10.0.0.0/8",0,0]]}}}
            """);

        Assert.Equal("127.0.0.1,::1", merged.GlobalMacros["{$TRAPPER.ALLOWED_HOSTS}"]);
        Assert.Equal([20L], merged.Templates[10]);
        Assert.Equal("10.0.0.0/8", merged.HostMacros[20]["{$PEERS}"]);
    }

    [Fact]
    public void The_agent_timeout_is_read_from_7_0_s_config_table_and_7_4_s_settings_and_kept_by_a_delta_without_them()
    {
        // 7.0 sends the config table's timeout_zabbix_agent column; 7.4 and 8.0 a settings row per name, the value in
        // value_str; both carry the proxy's own override when it has one (proxyconfigread.c 7.0:385-453, 8.0:370-470).
        ZabbixProxyConfig seven = Merge(ZabbixProxyConfig.Empty, """
            {"full_sync":1,"config_revision":1,"data":{"config":{"fields":["configid","timeout_zabbix_agent","timeout_simple_check"],"data":[[1,"7s","3s"]]}}}
            """);
        ZabbixProxyConfig eight = Merge(ZabbixProxyConfig.Empty, """
            {"full_sync":1,"config_revision":1,"data":{"settings":{"fields":["name","type","value_str","value_int"],
             "data":[["timeout_simple_check",1,"3s",0],["timeout_zabbix_agent",1,"9s",0]]}}}
            """);

        ZabbixProxyConfig kept = Merge(eight, """{"config_revision":2,"data":{}}""");

        Assert.Equal(("", "7s", "9s", "9s"), (ZabbixProxyConfig.Empty.AgentTimeout, seven.AgentTimeout, eight.AgentTimeout, kept.AgentTimeout));
    }

    [Fact]
    public void A_row_that_does_not_match_its_fields_is_refused()
        => Assert.Throws<JsonException>(() => Merge(ZabbixProxyConfig.Empty, """{"config_revision":1,"data":{"hosts":{"fields":["hostid","host"],"data":[[10]]}}}"""));
}
