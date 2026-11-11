// This Source Code Form is subject to the terms of the Mozilla Public
// License, v. 2.0. Copyright (c) 2026 schroeder247. SPDX-License-Identifier: MPL-2.0
using System.Management.Automation;
using System.Text.Json;

namespace Community.Zabbix.PowerShell.Tests;

/// <summary>
/// The parameters that take one of Zabbix's grammars as text: <c>-Delay</c> an update interval, <c>-History</c>,
/// <c>-Trends</c> and <c>-Timeout</c> a length, <c>-TrapperHosts</c> the allowed hosts, <c>-InterfacePort</c> a port. A
/// script writes the text as Zabbix does, the SDK's typed value reads it, and the server gets it as written; text outside
/// the grammar is an invalid-argument error before any call. The sort, given as <c>-SortField</c> and <c>-SortOrder</c>,
/// is one direction per field on the wire.
/// </summary>
public sealed class TypedValueParameterTests
{
    [Fact]
    public async Task New_ZbxItem_sends_the_interval_the_lengths_and_the_allowed_hosts_as_written()
    {
        await using FakeZabbix server = await FakeZabbix.StartAsync();
        server.On("item.create", """{"itemids":["55"]}""");
        server.On("item.get", """[{"itemid":"55","hostid":"10084","name":"Trap","key_":"trap.key"}]""");
        using Shell shell = Shell.Open();
        shell.Connect(server);

        Run run = shell.Invoke("New-ZbxItem -HostId 10084 -Name 'Trap' -Type Trapper -Key 'trap.key' -ValueType Unsigned -Delay '1m;10s/1-5,09:00-18:00' -History '{$HISTORY}' -Trends 0 -Timeout '' -TrapperHosts '10.0.0.0/8, ::1,{HOST.CONN}' -Force");

        Assert.Empty(run.Errors);
        JsonElement request = Assert.Single(server.ParamsOf("item.create"));
        Assert.Equal("1m;10s/1-5,09:00-18:00", request.GetProperty("delay").GetString());
        Assert.Equal("{$HISTORY}", request.GetProperty("history").GetString());
        Assert.Equal("0", request.GetProperty("trends").GetString());
        Assert.Equal(string.Empty, request.GetProperty("timeout").GetString());
        Assert.Equal("10.0.0.0/8,::1,{HOST.CONN}", request.GetProperty("trapper_hosts").GetString());
    }

    [Theory]
    [InlineData("-Delay '5 minutes'", "isn't an interval Zabbix takes")]
    [InlineData("-History '90 days'", "isn't a length of time Zabbix takes")]
    [InlineData("-TrapperHosts '10.0.0.0/33'", "isn't an allowed host Zabbix takes")]
    public async Task Text_outside_the_grammar_is_an_invalid_argument_before_any_call(string parameter, string message)
    {
        await using FakeZabbix server = await FakeZabbix.StartAsync();
        server.On("item.create", """{"itemids":["55"]}""");
        using Shell shell = Shell.Open();
        shell.Connect(server);

        Run run = shell.Invoke($"New-ZbxItem -HostId 10084 -Name 'Trap' -Type Trapper -Key 'trap.key' -ValueType Unsigned {parameter} -Force");

        ErrorRecord error = Assert.Single(run.Errors);
        Assert.Equal(ErrorCategory.InvalidArgument, error.CategoryInfo.Category);
        Assert.Contains(message, error.Exception.Message, StringComparison.Ordinal);
        Assert.Empty(server.ParamsOf("item.create"));
    }

    [Fact]
    public async Task New_ZbxHost_InterfacePort_takes_a_macro()
    {
        await using FakeZabbix server = await FakeZabbix.StartAsync();
        server.On("host.create", """{"hostids":["10084"]}""");
        server.On("host.get", FakeZabbix.Hosts(("10084", "web-01")));
        using Shell shell = Shell.Open();
        shell.Connect(server);

        Run run = shell.Invoke("New-ZbxHost -Host web-01 -Group @('7') -InterfaceType Agent -InterfaceIp '127.0.0.1' -InterfacePort '{$AGENT_PORT}' -Confirm:$false");

        Assert.Empty(run.Errors);
        JsonElement iface = Assert.Single(Assert.Single(server.ParamsOf("host.create")).GetProperty("interfaces").EnumerateArray());
        Assert.Equal("{$AGENT_PORT}", iface.GetProperty("port").GetString());
    }

    [Fact]
    public async Task A_SortOrder_shorter_than_SortField_sends_the_rest_ascending()
    {
        await using FakeZabbix server = await FakeZabbix.StartAsync();
        server.On("host.get", FakeZabbix.Hosts(("10084", "web-01")));
        using Shell shell = Shell.Open();
        shell.Connect(server);

        Run run = shell.Invoke("Get-ZbxHost -SortField name, hostid -SortOrder Descending");

        Assert.Empty(run.Errors);
        JsonElement request = Assert.Single(server.ParamsOf("host.get"));
        Assert.Equal("""["name","hostid"]""", request.GetProperty("sortfield").GetRawText());
        Assert.Equal("""["DESC","ASC"]""", request.GetProperty("sortorder").GetRawText());
    }
}
