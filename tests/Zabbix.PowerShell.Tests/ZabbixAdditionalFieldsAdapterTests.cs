// This Source Code Form is subject to the terms of the Mozilla Public
// License, v. 2.0. Copyright (c) 2026 schroeder247. SPDX-License-Identifier: MPL-2.0
namespace Community.Zabbix.PowerShell.Tests;

/// <summary>
/// <see cref="ZabbixAdditionalFieldsAdapter"/>, registered per role:read record through the generated
/// <c>Community.Zabbix.types.ps1xml</c> (<c>&lt;TypeAdapter&gt;</c>): a wire field a newer Zabbix added that the record does
/// not model lands in <c>AdditionalFields</c> (<c>ZabbixApiEmitter</c>'s own extension-data bag) as ALWAYS — this
/// exposes each of its keys as a first-class <see cref="System.Management.Automation.PSAdaptedProperty"/> beside
/// the record's own real properties, the same public seam <c>Get-CimInstance</c> uses
/// (<c>Microsoft.PowerShell.Cim.CimInstanceAdapter</c>) for its own dynamic WMI/CIM properties.
/// </summary>
public sealed class ZabbixAdditionalFieldsAdapterTests
{
    [Fact]
    public async Task An_unmodelled_wire_field_reads_as_a_first_class_property()
    {
        await using FakeZabbix server = await FakeZabbix.StartAsync();
        server.On("host.get", """[{"hostid":"10084","host":"web-01","name":"web-01","status":"0","newfield":"newvalue"}]""");
        using Shell shell = Shell.Open();
        shell.Connect(server);

        Run run = shell.Invoke("(Get-ZbxHost -Id 10084).newfield");

        Assert.Empty(run.Errors);
        Assert.Equal("newvalue", Assert.Single(run.Output).BaseObject);
    }

    [Fact]
    public async Task Get_Member_lists_the_unmodelled_field_beside_the_records_own_properties()
    {
        await using FakeZabbix server = await FakeZabbix.StartAsync();
        server.On("host.get", """[{"hostid":"10084","host":"web-01","name":"web-01","status":"0","newfield":"newvalue"}]""");
        using Shell shell = Shell.Open();
        shell.Connect(server);

        Run run = shell.Invoke("(Get-ZbxHost -Id 10084 | Get-Member -Name newfield).Name");

        // Listed by the name the records give their members (a field no record models: its words in PascalCase), and
        // still found by its wire key.
        Assert.Empty(run.Errors);
        Assert.Equal("Newfield", Assert.Single(run.Output).BaseObject);
    }

    [Fact]
    public async Task A_reply_with_no_unmodelled_field_carries_no_adapted_property()
    {
        await using FakeZabbix server = await FakeZabbix.StartAsync();
        server.On("host.get", FakeZabbix.Hosts(("10084", "web-01")));
        using Shell shell = Shell.Open();
        shell.Connect(server);

        Run run = shell.Invoke("(Get-ZbxHost -Id 10084 | Get-Member -Name newfield)");

        Assert.Empty(run.Errors);
        Assert.Empty(run.Output);
    }

    [Fact]
    public async Task Get_Member_lists_the_records_own_properties_as_well_as_the_unmodelled_ones()
    {
        await using FakeZabbix server = await FakeZabbix.StartAsync();
        server.On("host.get", """[{"hostid":"10084","host":"web-01","name":"web-01","status":"0","newfield":"newvalue"}]""");
        using Shell shell = Shell.Open();
        shell.Connect(server);

        Run run = shell.Invoke("Get-ZbxHost -Id 10084 | Get-Member -MemberType Properties | ForEach-Object Name");

        Assert.Empty(run.Errors);
        string[] names = [.. run.Output.Select(o => (string)o.BaseObject)];
        Assert.Contains("HostId", names);
        Assert.Contains("HostName", names);
        Assert.Contains("Status", names);
        Assert.Contains("Newfield", names);
    }

    [Fact]
    public async Task Format_List_star_shows_the_records_own_properties()
    {
        await using FakeZabbix server = await FakeZabbix.StartAsync();
        server.On("host.get", FakeZabbix.Hosts(("10084", "web-01")));
        using Shell shell = Shell.Open();
        shell.Connect(server);

        Run run = shell.Invoke("Get-ZbxHost -Id 10084 | Format-List -Property * | Out-String");

        Assert.Empty(run.Errors);
        string text = (string)Assert.Single(run.Output).BaseObject;
        Assert.Contains("HostId", text, StringComparison.Ordinal);
        Assert.Contains("10084", text, StringComparison.Ordinal);
    }

    [Fact]
    public async Task A_records_own_property_reads_its_typed_value_through_the_adapter()
    {
        await using FakeZabbix server = await FakeZabbix.StartAsync();
        server.On("host.get", FakeZabbix.Hosts(("10084", "web-01")));
        using Shell shell = Shell.Open();
        shell.Connect(server);

        Run run = shell.Invoke("$h = Get-ZbxHost -Id 10084; $h.PSObject.Properties['HostId'].TypeNameOfValue; $h.HostId");

        Assert.Empty(run.Errors);
        Assert.Equal(typeof(ulong?).FullName, run.Output[0].BaseObject);
        Assert.Equal(10084UL, run.Output[1].BaseObject);
    }

    // An action's filter is typed (ZabbixActionReadFilter): its wire keys still navigate
    // (PowerShell's member lookup is case-insensitive), and the condition type reads as its enum.
    [Fact]
    public async Task A_modelled_raw_json_property_is_navigable_by_wire_key()
    {
        await using FakeZabbix server = await FakeZabbix.StartAsync();
        server.On("action.get", """[{"actionid":"7","name":"next-adapter","eventsource":"0","status":"0","filter":{"evaltype":"0","formula":"","conditions":[{"conditiontype":"0","operator":"0","value":"2","formulaid":"A"}]}}]""");
        using Shell shell = Shell.Open();
        shell.Connect(server);

        Run run = shell.Invoke("(Get-ZbxAction -Id 7 -SelectFilter).Filter.conditions[0].conditiontype");

        Assert.Empty(run.Errors);
        Assert.Equal(ZabbixActionConditionType.HostGroup, Assert.Single(run.Output).BaseObject);
    }
}
