// This Source Code Form is subject to the terms of the Mozilla Public
// License, v. 2.0. Copyright (c) 2026 schroeder247. SPDX-License-Identifier: MPL-2.0
using System.Management.Automation;
using System.Text.Json;

namespace Community.Zabbix.PowerShell.Tests;

/// <summary>
/// Which fields a generated <c>New-</c>/<c>Set-</c>/builder demands comes from
/// the description, and the description derives a conditional rule (an <c>API_MULTIPLE</c> arm, a method-dependent
/// <c>check_db_fields</c>) as conditional — never as always-required.
/// </summary>
public sealed class RequiredFieldGateTests
{
    [Fact]
    public async Task Set_ZbxItem_renames_an_item_without_demanding_value_type()
    {
        // item.update does not require value_type, so Set- does not either.
        await using FakeZabbix server = await FakeZabbix.StartAsync();
        server.On("item.update", """{"itemids":["5"]}""");
        server.On("item.get", """[{"itemid":"5","name":"renamed"}]""");
        using Shell shell = Shell.Open();
        shell.Connect(server);

        Run run = shell.Invoke("Set-ZbxItem -Id 5 -Name renamed -Confirm:$false");

        Assert.Empty(run.Errors);
        // Also: no master_itemid/url/snmp_oid placeholder — on an update it would change the item (master_itemid "0"
        // detaches a dependent item from its master).
        Assert.Equal("""[{"itemid":"5","name":"renamed"}]""", Assert.Single(server.ParamsOf("item.update")).GetRawText());
    }

    [Fact]
    public async Task New_ZbxItem_without_ValueType_is_still_refused_naming_value_type()
    {
        // item.create's value_type is API_REQUIRED on every API_MULTIPLE arm (dependent, calculated, else) — derived
        // from the description, not hand-listed.
        await using FakeZabbix server = await FakeZabbix.StartAsync();
        using Shell shell = Shell.Open();
        shell.Connect(server);

        Run run = shell.Invoke("New-ZbxItem -HostId 10084 -Name 'Trap' -Type Trapper -Key 'trap.key' -Force");

        ErrorRecord error = Assert.Single(run.Errors);
        Assert.Contains("value_type", error.ToString(), StringComparison.Ordinal);
        Assert.Empty(server.ParamsOf("item.create"));
    }

    [Fact]
    public async Task Set_ZbxHostInterface_changes_a_port_with_no_HostId()
    {
        // CHostInterface::checkInput($interfaces, 'update') requires only interfaceid (hostid/ip/dns/port/useip/main
        // are the create arm's check_db_fields).
        await using FakeZabbix server = await FakeZabbix.StartAsync();
        server.On("hostinterface.update", """{"interfaceids":["7"]}""");
        server.On("hostinterface.get", """[{"interfaceid":"7","hostid":"10084","port":"10051"}]""");
        using Shell shell = Shell.Open();
        shell.Connect(server);

        Run run = shell.Invoke("Set-ZbxHostInterface -Id 7 -Port 10051 -Confirm:$false");

        Assert.Empty(run.Errors);
        JsonElement sent = Assert.Single(Assert.Single(server.ParamsOf("hostinterface.update")).EnumerateArray());
        Assert.Equal(["interfaceid", "port"], sent.EnumerateObject().Select(p => p.Name).Order(StringComparer.Ordinal));
        Assert.Equal("10051", sent.GetProperty("port").GetString());
    }

    [Theory]
    [InlineData("discoveryrule", "New-ZbxDiscoveryRule")]
    [InlineData("discoveryruleprototype", "New-ZbxDiscoveryRulePrototype")]
    public void A_discovery_rules_MasterItemId_Url_and_SnmpOid_are_not_Mandatory(string _, string command)
    {
        using Shell shell = Shell.Open();

        Run run = shell.Invoke($"(Get-Command {command}).Parameters['MasterItemId','Url','SnmpOid'].Attributes | Where-Object {{ $_ -is [System.Management.Automation.ParameterAttribute] -and $_.Mandatory }}");

        Assert.Empty(run.Errors);
        Assert.Empty(run.Output);
    }

    [Fact]
    public async Task New_ZbxDiscoveryRule_of_a_trapper_type_needs_no_MasterItemId()
    {
        await using FakeZabbix server = await FakeZabbix.StartAsync();
        server.On("discoveryrule.create", """{"itemids":["9"]}""");
        server.On("discoveryrule.get", """[{"itemid":"9","name":"lld"}]""");
        using Shell shell = Shell.Open();
        shell.Connect(server);

        Run run = shell.Invoke("New-ZbxDiscoveryRule -HostId 10084 -Name lld -Key lld.key -Type Trapper -Force");

        Assert.Empty(run.Errors);
        // master_itemid is required for dependent rules only (CItemTypeFactory's per-type rules, which the description
        // reads as alternatives), so the create record does not declare it required and a trapper rule omits it.
        JsonElement request = Assert.Single(server.ParamsOf("discoveryrule.create"));
        Assert.False(request.TryGetProperty("master_itemid", out _));
    }

    [Theory]
    [InlineData("New-ZbxActionCondition")]
    [InlineData("New-ZbxLldFilterCondition")]
    [InlineData("New-ZbxCorrelationCondition")]
    public void A_filter_conditions_FormulaId_is_not_Mandatory(string command)
    {
        // formulaid is API_REQUIRED only in the evaltype = custom-expression arm (CAction.php,
        // CDiscoveryRule.php, CCorrelation.php).
        using Shell shell = Shell.Open();

        Run run = shell.Invoke($"(Get-Command {command}).Parameters['FormulaId'].Attributes | Where-Object {{ $_ -is [System.Management.Automation.ParameterAttribute] -and $_.Mandatory }}");

        Assert.Empty(run.Errors);
        Assert.Empty(run.Output);
    }

    [Fact]
    public void A_filter_condition_request_without_formulaid_binds_and_serializes_without_it()
    {
        using Shell shell = Shell.Open();

        Run run = shell.Invoke("""
            $c = [Community.Zabbix.ZabbixActionFilterConditionRequest]@{ conditiontype = 1; value = '10084' }
            [System.Text.Json.JsonSerializer]::Serialize($c, $c.GetType(), [Community.Zabbix.ZabbixJson]::Options)
            """);

        Assert.Empty(run.Errors);
        Assert.Equal("""{"conditiontype":1,"value":"10084"}""", Assert.Single(run.Output).BaseObject);
    }

    [Theory]
    [InlineData("NetworkDiscoveryRule", 18)]
    [InlineData("NetworkDiscoveryCheck", 19)]
    [InlineData("Proxy", 20)]
    [InlineData("HostName", 22)]
    [InlineData("EventType", 23)]
    [InlineData("HostMetadata", 24)]
    [InlineData("DiscoveredHostIP", 7)]
    [InlineData("ServiceName", 28)]
    public void ZabbixActionConditionType_carries_every_event_sources_condition_types(string name, int value)
    {
        // Every eventsource arm's conditiontype list is extracted, not only the first one (triggers).
        Assert.Equal(value, (int)Enum.Parse<ZabbixActionConditionType>(name));
    }

    [Fact]
    public async Task New_ZbxMaintenance_Hosts_and_Groups_send_the_hosts_and_groups_every_version_accepts()
    {
        // hostids/groupids were removed in 7.4 (8.0 answers 'unexpected parameter "hostids"'); the SDK refuses them on
        // 7.4+. The reference lists that take their place take ids too.
        await using FakeZabbix server = await FakeZabbix.StartAsync();
        server.On("maintenance.create", """{"maintenanceids":["3"]}""");
        server.On("maintenance.get", """[{"maintenanceid":"3","name":"m"}]""");
        using Shell shell = Shell.Open();
        shell.Connect(server);

        Run run = shell.Invoke("New-ZbxMaintenance -Name m -ActiveSince 1 -ActiveTill 2 -TimePeriod @(@{ timeperiod_type = 0; period = 3600 }) -Host @('10084') -Group @('7') -Confirm:$false");

        Assert.Empty(run.Errors);
        JsonElement request = Assert.Single(server.ParamsOf("maintenance.create"));
        Assert.Equal("""[{"hostid":"10084"}]""", request.GetProperty("hosts").GetRawText());
        Assert.Equal("""[{"groupid":"7"}]""", request.GetProperty("groups").GetRawText());
        Assert.False(request.TryGetProperty("hostids", out _));
        Assert.False(request.TryGetProperty("groupids", out _));
    }

    [Theory]
    [InlineData("Set-ZbxMaintenance", "HostReference", "HostIds")]
    [InlineData("Set-ZbxMaintenance", "Group", "GroupIds")]
    [InlineData("New-ZbxUserGroup", "User", "UserIds")]
    public void A_removed_raw_id_list_has_no_parameter_beside_the_reference_list_that_replaced_it(string command, string parameter, string removed)
    {
        using Shell shell = Shell.Open();

        Run run = shell.Invoke($"(Get-Command {command}).Parameters.ContainsKey('{parameter}'); [bool]((Get-Command {command}).Parameters.Values | Where-Object {{ $_.Name -eq '{removed}' -or $_.Aliases -contains '{removed}' }})");

        Assert.Empty(run.Errors);
        Assert.Equal(true, run.Output[0].BaseObject);
        Assert.Equal(false, run.Output[1].BaseObject);
    }
}
