// This Source Code Form is subject to the terms of the Mozilla Public
// License, v. 2.0. Copyright (c) 2026 schroeder247. SPDX-License-Identifier: MPL-2.0
using System.Text.Json;

namespace Community.Zabbix.PowerShell.Tests;

/// <summary>The history and getter commands: <c>Clear-ZbxHistory</c>, <c>Send-ZbxHistory</c>, <c>Get-ZbxAgentItem</c>.</summary>
public sealed class HistoryAndGetterCommandsTests
{
    [Fact]
    public async Task Clear_ZbxHistory_resolves_the_item_then_clears()
    {
        await using FakeZabbix server = await FakeZabbix.StartAsync();
        server.On("item.get", """[{"itemid":"501","key_":"agent.ping","name":"agent.ping"}]""");
        server.On("history.clear", """{"itemids":["501"]}""");
        using Shell shell = Shell.Open();
        shell.Connect(server);

        Run run = shell.Invoke("Clear-ZbxHistory agent.ping -Force -Confirm:$false");

        Assert.Empty(run.Errors);
        Assert.Equal("501", Assert.Single(Assert.Single(server.ParamsOf("history.clear")).EnumerateArray()).GetString());
    }

    [Fact]
    public async Task Send_ZbxHistory_pushes_a_value_at_its_own_clock()
    {
        await using FakeZabbix server = await FakeZabbix.StartAsync();
        server.On("history.push", "{}");
        using Shell shell = Shell.Open();
        shell.Connect(server);

        Run run = shell.Invoke("Send-ZbxHistory web-01 agent.ping 1 -Clock 1700000000 -Confirm:$false");

        Assert.Empty(run.Errors);
        JsonElement push = Assert.Single(Assert.Single(server.ParamsOf("history.push")).EnumerateArray());
        Assert.Equal("web-01", push.GetProperty("host").GetString());
        Assert.Equal(1700000000, push.GetProperty("clock").GetInt64());
    }

    [Fact]
    public async Task Get_ZbxAgentItem_queries_the_agent_directly_over_zabbix_get()
    {
        await using FakeTrapper agent = FakeTrapper.Start();
        agent.Otherwise = _ => "42";
        using Shell shell = Shell.Open();

        Run run = shell.Invoke($"Get-ZbxAgentItem 127.0.0.1:{agent.Port} agent.ping");

        Assert.Empty(run.Errors);
        var answer = Assert.IsType<ZabbixAgentAnswer>(Assert.Single(run.Output).BaseObject);
        Assert.Equal("42", answer.Value);
        Assert.True(answer.IsSupported);
    }

    [Fact]
    public async Task Get_ZbxAgentItem_Timeout_answers_when_bound()
    {
        // Not a timing assertion: the parameter binds and the call still succeeds carrying an explicit value.
        await using FakeTrapper agent = FakeTrapper.Start();
        agent.Otherwise = _ => "42";
        using Shell shell = Shell.Open();

        Run run = shell.Invoke($"Get-ZbxAgentItem 127.0.0.1:{agent.Port} agent.ping -Timeout 5");

        Assert.Empty(run.Errors);
        var answer = Assert.IsType<ZabbixAgentAnswer>(Assert.Single(run.Output).BaseObject);
        Assert.Equal("42", answer.Value);
    }

    [Theory]
    [InlineData("0")]
    [InlineData("0.5")]
    [InlineData("301")]
    [InlineData("'00:05:01'")]
    public void Get_ZbxAgentItem_Timeout_outside_1_to_300_seconds_is_a_binding_error(string timeout)
    {
        using Shell shell = Shell.Open();

        Run run = shell.Invoke($"Get-ZbxAgentItem 127.0.0.1 agent.ping -Timeout {timeout}");

        Assert.Contains("ParameterArgumentTransformationError", Assert.Single(run.Errors).FullyQualifiedErrorId, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("web-01", "web-01:10050")]
    [InlineData("web-01:10055", "web-01:10055")]
    [InlineData("192.0.2.7", "192.0.2.7:10050")]
    [InlineData("2001:db8::7", "[2001:db8::7]:10050")]
    [InlineData("[2001:db8::7]:10055", "[2001:db8::7]:10055")]
    [InlineData("[::1]", "[::1]:10050")]
    public void Get_ZbxAgentItem_Agent_takes_the_agent_port_when_the_address_names_none(string agent, string node)
    {
        // A portless entry on -Server takes the trapper's port; an agent listens on 10050.
        using Shell shell = Shell.Open();

        Run run = shell.Invoke("""
            $agent = (Get-Command Get-ZbxAgentItem).Parameters['Agent'].Attributes |
                Where-Object { $_ -is [System.Management.Automation.ArgumentTransformationAttribute] }
            $agent.Transform($ExecutionContext, $text)
            """, new Dictionary<string, object?> { ["text"] = agent });

        Assert.Empty(run.Errors);
        Assert.Equal(node, ZabbixEndPoint.Format((System.Net.DnsEndPoint)Assert.Single(run.Output).BaseObject));
    }

    [Fact]
    public async Task A_filter_value_given_once_where_the_request_takes_a_list_is_sent_as_a_one_element_list()
    {
        // -Filter @{ itemids = $item.ItemId } and sortorder = 'DESC' bind: the API takes either, and PowerShell promotes
        // a scalar for a T[] parameter, so refusing them ("could not be converted to IReadOnlyList`1[String]. Path:
        // $.itemids") would be wrong.
        await using FakeZabbix server = await FakeZabbix.StartAsync();
        server.On("history.get", "[]");
        using Shell shell = Shell.Open();
        shell.Connect(server);

        Run run = shell.Invoke("Get-ZbxHistory -Filter @{ itemids = '23'; history = 3; sortfield = 'clock'; sortorder = 'DESC' }");

        Assert.Empty(run.Errors);
        System.Text.Json.JsonElement sent = Assert.Single(server.ParamsOf("history.get"));
        Assert.Equal("""["23"]""", sent.GetProperty("itemids").GetRawText());
        Assert.Contains("DESC", sent.GetProperty("sortorder").GetRawText(), StringComparison.Ordinal);
    }

    [Fact]
    public async Task Get_ZbxHistory_reads_a_piped_items_value_type_within_a_window_and_a_limit()
    {
        // -From/-Till and -Limit work with -Id, and a piped item's value type picks its table: reading the unsigned table
        // for every item would bring a text item's values back empty.
        await using FakeZabbix server = await FakeZabbix.StartAsync();
        server.On("item.get", """[{"itemid":"23","hostid":"10084","key_":"app.status","value_type":"4"}]""").On("history.get", "[]");
        using Shell shell = Shell.Open();
        shell.Connect(server);

        Run run = shell.Invoke("Get-ZbxItem -Id 23 | Get-ZbxHistory -From 1790000000 -Till 1790003600 -Limit 5 -SortField clock -SortOrder Descending");

        Assert.Empty(run.Errors);
        System.Text.Json.JsonElement sent = Assert.Single(server.ParamsOf("history.get"));
        Assert.Equal("""["23"]""", sent.GetProperty("itemids").GetRawText());
        Assert.Equal(4, sent.GetProperty("history").GetInt32());
        Assert.Equal(1790000000, sent.GetProperty("time_from").GetInt64());
        Assert.Equal(1790003600, sent.GetProperty("time_till").GetInt64());
        Assert.Equal(5, sent.GetProperty("limit").GetInt32());
    }
}
