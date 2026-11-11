// This Source Code Form is subject to the terms of the Mozilla Public
// License, v. 2.0. Copyright (c) 2026 schroeder247. SPDX-License-Identifier: MPL-2.0
using System.Text.Json;
using Community.Zabbix.Hosting;
using Community.Zabbix.Proxy;

namespace Community.Zabbix.Tests;

/// <summary>
/// One id type across the packages: the API writes an id as a decimal string, the framed protocol as a number, and an id
/// read from either joins one read from the other with no conversion.
/// </summary>
public sealed class ZabbixIdTests
{
    [Theory]
    [InlineData("""{"hostid":"10084"}""")]
    [InlineData("""{"hostid":10084}""")]
    public void The_API_record_reads_either_JSON_form_and_writes_the_string(string json)
    {
        ZabbixHost host = JsonSerializer.Deserialize<ZabbixHost>(json, ZabbixJson.Options)!;

        Assert.Equal(10084UL, host.HostId);
        Assert.Contains("\"hostid\":\"10084\"", JsonSerializer.Serialize(host, ZabbixJson.Options), StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("""{"hostid":-1}""")]
    [InlineData("""{"hostid":1.5}""")]
    [InlineData("""{"hostid":"x"}""")]
    [InlineData("""{"hostid":true}""")]
    public void A_token_that_is_not_an_id_is_a_JSON_error(string json)
        => Assert.Throws<JsonException>(() => JsonSerializer.Deserialize<ZabbixHost>(json, ZabbixJson.Options));

    [Fact]
    public void The_framed_protocol_writes_an_id_as_a_number()
    {
        var row = new ZabbixProxyHistoryItem(1, 73184UL, new ZabbixClock(1_700_000_000, 0), "1");

        string json = JsonSerializer.Serialize(row, ZabbixProxyJson.Default.ZabbixProxyHistoryItem);

        Assert.Contains("\"itemid\":73184", json, StringComparison.Ordinal);
    }

    [Fact]
    public void An_active_check_assignment_reads_the_id_the_server_writes()
    {
        ZabbixActiveChecksResponse response = JsonSerializer.Deserialize("""{"response":"success","data":[{"key":"agent.ping","itemid":42237,"delay":"1m","lastlogsize":0,"mtime":0}]}""", ZabbixActiveAgentJson.Default.ZabbixActiveChecksResponse)!;

        Assert.Equal(42237UL, Assert.Single(response.Data!).ItemId);
    }

    [Fact]
    public void A_filter_takes_ids_as_the_API_compares_them()
    {
        ZabbixFieldValue one = 10084UL;
        ZabbixFieldValue many = new ulong[] { 1, 2 };

        Assert.Equal("10084", one.Element.GetString());
        Assert.Equal("""["1","2"]""", many.Element.GetRawText());
        Assert.Equal("""["5"]""", new ZabbixHostGetFilter { HostId = [5] }["hostid"].Element.GetRawText());
    }
}
