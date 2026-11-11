// This Source Code Form is subject to the terms of the Mozilla Public
// License, v. 2.0. Copyright (c) 2026 schroeder247. SPDX-License-Identifier: MPL-2.0
using System.Text.Json;

namespace Community.Zabbix.PowerShell.Tests;

/// <summary>
/// Every generated <c>Get-Zbx&lt;Entity&gt;</c> (<c>hostgroup</c> here: small, it
/// carries <c>SelectHosts</c>/<c>Editable</c>, and it is also the pick of
/// <see cref="GeneratedCrudCommandsTests"/>) and the hand-written <c>Get-ZbxHost</c> carry the <c>-Select*</c>
/// switches, <c>-SortField</c>/<c>-SortOrder</c>, <c>-Search</c>/<c>-SearchWildcardsEnabled</c>, and
/// <c>-CountOutput</c>/<c>-Count</c> — all folding onto the exact same request object <c>-Filter</c> builds
/// (<see cref="ZabbixGetCmdlet{TEntity,TParameters}.ApplyEntityFilters"/>).
/// </summary>
public sealed class GetSelectionOptionsTests
{
    [Fact]
    public async Task Get_ZbxHostGroup_SelectHosts_sends_selectHosts_extend()
    {
        await using FakeZabbix server = await FakeZabbix.StartAsync();
        server.On("hostgroup.get", """[{"groupid":"1","name":"Linux servers"}]""");
        using Shell shell = Shell.Open();
        shell.Connect(server);

        Run run = shell.Invoke("Get-ZbxHostGroup -SelectHosts");

        Assert.Empty(run.Errors);
        JsonElement request = Assert.Single(server.ParamsOf("hostgroup.get"));
        Assert.Equal("extend", request.GetProperty("selectHosts").GetString());
    }

    [Fact]
    public async Task Get_ZbxHostGroup_SortField_and_SortOrder_compose_with_Name()
    {
        await using FakeZabbix server = await FakeZabbix.StartAsync();
        server.On("hostgroup.get", """[{"groupid":"1","name":"Linux servers"}]""");
        using Shell shell = Shell.Open();
        shell.Connect(server);

        Run run = shell.Invoke("Get-ZbxHostGroup -SortField name -SortOrder Descending");

        Assert.Empty(run.Errors);
        JsonElement request = Assert.Single(server.ParamsOf("hostgroup.get"));
        Assert.Equal("""["name"]""", request.GetProperty("sortfield").GetRawText());
        Assert.Equal("""["DESC"]""", request.GetProperty("sortorder").GetRawText());
    }

    [Fact]
    public async Task Get_ZbxHostGroup_Search_hashtable_and_SearchWildcardsEnabled_send_the_wire_search_object()
    {
        await using FakeZabbix server = await FakeZabbix.StartAsync();
        server.On("hostgroup.get", """[{"groupid":"1","name":"Web servers"}]""");
        using Shell shell = Shell.Open();
        shell.Connect(server);

        Run run = shell.Invoke("Get-ZbxHostGroup -Search @{ name = 'web*' } -SearchWildcardsEnabled");

        Assert.Empty(run.Errors);
        JsonElement request = Assert.Single(server.ParamsOf("hostgroup.get"));
        Assert.Equal("web*", request.GetProperty("search").GetProperty("name").GetString());
        Assert.True(request.GetProperty("searchWildcardsEnabled").GetBoolean());
    }

    [Fact]
    public async Task Get_ZbxHostGroup_CountOutput_calls_hostgroup_get_with_countOutput_and_writes_the_count()
    {
        await using FakeZabbix server = await FakeZabbix.StartAsync();
        server.On("hostgroup.get", "\"3\"");
        using Shell shell = Shell.Open();
        shell.Connect(server);

        Run run = shell.Invoke("Get-ZbxHostGroup -CountOutput");

        Assert.Empty(run.Errors);
        long count = (long)((System.Management.Automation.PSObject)Assert.Single(run.Output)).BaseObject;
        Assert.Equal(3, count);
        JsonElement request = Assert.Single(server.ParamsOf("hostgroup.get"));
        Assert.True(request.GetProperty("countOutput").GetBoolean());
    }

    [Fact]
    public async Task Get_ZbxHostGroup_Count_is_the_shipped_alias_of_CountOutput()
    {
        await using FakeZabbix server = await FakeZabbix.StartAsync();
        server.On("hostgroup.get", "\"5\"");
        using Shell shell = Shell.Open();
        shell.Connect(server);

        Run run = shell.Invoke("Get-ZbxHostGroup -Count");

        Assert.Empty(run.Errors);
        long count = (long)((System.Management.Automation.PSObject)Assert.Single(run.Output)).BaseObject;
        Assert.Equal(5, count);
    }

    [Fact]
    public async Task Get_ZbxHostGroup_Editable_sends_editable_true()
    {
        await using FakeZabbix server = await FakeZabbix.StartAsync();
        server.On("hostgroup.get", """[{"groupid":"1","name":"Linux servers"}]""");
        using Shell shell = Shell.Open();
        shell.Connect(server);

        Run run = shell.Invoke("Get-ZbxHostGroup -Editable");

        Assert.Empty(run.Errors);
        JsonElement request = Assert.Single(server.ParamsOf("hostgroup.get"));
        Assert.True(request.GetProperty("editable").GetBoolean());
    }

    // ----- the hand-written Get-ZbxHost gets the same set by hand -----------------------------------------------

    [Fact]
    public async Task Get_ZbxHost_SelectInterfaces_sends_selectInterfaces_extend()
    {
        await using FakeZabbix server = await FakeZabbix.StartAsync();
        server.On("host.get", FakeZabbix.Hosts(("10084", "web-01")));
        using Shell shell = Shell.Open();
        shell.Connect(server);

        Run run = shell.Invoke("Get-ZbxHost -SelectInterfaces");

        Assert.Empty(run.Errors);
        JsonElement request = Assert.Single(server.ParamsOf("host.get"));
        Assert.Equal("extend", request.GetProperty("selectInterfaces").GetString());
    }

    [Fact]
    public async Task Get_ZbxHost_SortField_and_SortOrder_send_the_wire_arrays()
    {
        await using FakeZabbix server = await FakeZabbix.StartAsync();
        server.On("host.get", FakeZabbix.Hosts(("10084", "web-01")));
        using Shell shell = Shell.Open();
        shell.Connect(server);

        Run run = shell.Invoke("Get-ZbxHost -SortField name -SortOrder Ascending");

        Assert.Empty(run.Errors);
        JsonElement request = Assert.Single(server.ParamsOf("host.get"));
        Assert.Equal("""["name"]""", request.GetProperty("sortfield").GetRawText());
        Assert.Equal("""["ASC"]""", request.GetProperty("sortorder").GetRawText());
    }

    [Fact]
    public async Task Get_ZbxHost_Search_and_SearchWildcardsEnabled_send_the_wire_search_object()
    {
        await using FakeZabbix server = await FakeZabbix.StartAsync();
        server.On("host.get", FakeZabbix.Hosts(("10084", "web-01")));
        using Shell shell = Shell.Open();
        shell.Connect(server);

        Run run = shell.Invoke("Get-ZbxHost -Search @{ host = 'web*' } -SearchWildcardsEnabled");

        Assert.Empty(run.Errors);
        JsonElement request = Assert.Single(server.ParamsOf("host.get"));
        Assert.Equal("web*", request.GetProperty("search").GetProperty("host").GetString());
        Assert.True(request.GetProperty("searchWildcardsEnabled").GetBoolean());
    }

    [Fact]
    public async Task Get_ZbxHost_CountOutput_calls_host_get_with_countOutput_and_writes_the_count()
    {
        await using FakeZabbix server = await FakeZabbix.StartAsync();
        server.On("host.get", "\"7\"");
        using Shell shell = Shell.Open();
        shell.Connect(server);

        Run run = shell.Invoke("Get-ZbxHost -CountOutput");

        Assert.Empty(run.Errors);
        long count = (long)((System.Management.Automation.PSObject)Assert.Single(run.Output)).BaseObject;
        Assert.Equal(7, count);
        JsonElement request = Assert.Single(server.ParamsOf("host.get"));
        Assert.True(request.GetProperty("countOutput").GetBoolean());
    }

    [Fact]
    public async Task Get_ZbxProblem_Recent_sends_recent_true()
    {
        // The -Recent switch (problem.get's "recent" apibool), emitted the
        // same way -Editable/-Global are (ZabbixCmdletEmitter.EmitGet).
        await using FakeZabbix server = await FakeZabbix.StartAsync();
        server.On("problem.get", "[]");
        using Shell shell = Shell.Open();
        shell.Connect(server);

        Run run = shell.Invoke("Get-ZbxProblem -Recent");

        Assert.Empty(run.Errors);
        JsonElement request = Assert.Single(server.ParamsOf("problem.get"));
        Assert.True(request.GetProperty("recent").GetBoolean());
    }

    // -Filter is TParameters-typed, so a bare hashtable would reach PowerShell's own built-in hashtable-to-object
    // binder, which is case-insensitive but underscore-blind. ZabbixFilterAttribute<T> (ZabbixPropertyOverrides.cs)
    // routes a Hashtable -Filter argument through ZabbixPropertyOverrides.Apply itself, so a wire-named key like
    // with_hosts binds too.

    [Fact]
    public async Task Get_ZbxHostGroup_Filter_with_hosts_wire_name_binds_the_generated_entitys_WithHosts_property()
    {
        await using FakeZabbix server = await FakeZabbix.StartAsync();
        server.On("hostgroup.get", """[{"groupid":"1","name":"Linux servers"}]""");
        using Shell shell = Shell.Open();
        shell.Connect(server);

        Run run = shell.Invoke("Get-ZbxHostGroup -Filter @{ with_hosts = $true }");

        Assert.Empty(run.Errors);
        JsonElement request = Assert.Single(server.ParamsOf("hostgroup.get"));
        Assert.True(request.GetProperty("with_hosts").GetBoolean());
    }

    [Fact]
    public async Task Get_ZbxHost_Filter_ipmi_authtype_a_host_field_goes_into_the_apis_filter_object()
    {
        // A top-level "ipmi_authtype" is not a host.get parameter at all: the server ignores it and returns every host.
        // ipmi_authtype is a HOST field, so the meaning is filter.ipmi_authtype.
        await using FakeZabbix server = await FakeZabbix.StartAsync();
        server.On("host.get", FakeZabbix.Hosts(("10084", "web-01")));
        using Shell shell = Shell.Open();
        shell.Connect(server);

        Run run = shell.Invoke("Get-ZbxHost -Filter @{ ipmi_authtype = 2 }");

        Assert.Empty(run.Errors);
        JsonElement request = Assert.Single(server.ParamsOf("host.get"));
        Assert.Equal("2", request.GetProperty("filter").GetProperty("ipmi_authtype").ToString());
        Assert.False(request.TryGetProperty("ipmi_authtype", out _));
    }

    [Fact]
    public async Task Get_ZbxHost_Filter_host_sends_filter_host_not_a_dropped_key()
    {
        await using FakeZabbix server = await FakeZabbix.StartAsync();
        server.On("host.get", FakeZabbix.Hosts(("10084", "web-01")));
        using Shell shell = Shell.Open();
        shell.Connect(server);

        Run run = shell.Invoke("Get-ZbxHost -Filter @{ host = 'web01-x' }");

        Assert.Empty(run.Errors);
        JsonElement request = Assert.Single(server.ParamsOf("host.get"));
        Assert.Equal("""{"host":"web01-x"}""", request.GetProperty("filter").GetRawText());
    }

    [Fact]
    public async Task Get_ZbxCorrelation_Filter_name_reaches_filter_although_the_read_record_is_not_named_after_the_stem()
    {
        await using FakeZabbix server = await FakeZabbix.StartAsync();
        server.On("correlation.get", "[]");
        using Shell shell = Shell.Open();
        shell.Connect(server);

        Run run = shell.Invoke("Get-ZbxCorrelation -Filter @{ name = 'c1' }");

        Assert.Empty(run.Errors);
        Assert.Equal("""{"name":"c1"}""", Assert.Single(server.ParamsOf("correlation.get")).GetProperty("filter").GetRawText());
    }

    [Fact]
    public async Task Get_ZbxHost_Filter_merges_entity_field_keys_with_an_explicit_filter_entry()
    {
        await using FakeZabbix server = await FakeZabbix.StartAsync();
        server.On("host.get", FakeZabbix.Hosts(("10084", "web-01")));
        using Shell shell = Shell.Open();
        shell.Connect(server);

        Run run = shell.Invoke("Get-ZbxHost -Filter @{ host = 'web01-x'; filter = @{ status = '0' }; limit = 5 }");

        Assert.Empty(run.Errors);
        JsonElement request = Assert.Single(server.ParamsOf("host.get"));
        Assert.Equal("web01-x", request.GetProperty("filter").GetProperty("host").GetString());
        Assert.Equal("0", request.GetProperty("filter").GetProperty("status").ToString());
        Assert.Equal(5, request.GetProperty("limit").GetInt32());
    }

    [Fact]
    public async Task Get_ZbxHost_Filter_with_a_key_neither_the_parameters_nor_the_host_know_is_an_error_naming_the_key()
    {
        await using FakeZabbix server = await FakeZabbix.StartAsync();
        server.On("host.get", FakeZabbix.Hosts(("10084", "web-01")));
        using Shell shell = Shell.Open();
        shell.Connect(server);

        Run run = shell.Invoke("Get-ZbxHost -Filter @{ nosuchkey = 'x' }");

        System.Management.Automation.ErrorRecord error = Assert.Single(run.Errors);
        Assert.Contains("nosuchkey", error.ToString(), StringComparison.Ordinal);
        Assert.Empty(server.ParamsOf("host.get"));
    }

    [Fact]
    public async Task Get_ZbxHostGroup_Filter_with_an_already_typed_object_still_works_unchanged()
    {
        // ZabbixFilterAttribute<T> only intercepts a genuine Hashtable argument — an already-typed TParameters
        // value (the typed calling convention) must still pass through untouched.
        await using FakeZabbix server = await FakeZabbix.StartAsync();
        server.On("hostgroup.get", """[{"groupid":"1","name":"Linux servers"}]""");
        using Shell shell = Shell.Open();
        shell.Connect(server);

        Run run = shell.Invoke("""
            $typed = New-Object Community.Zabbix.ZabbixHostGroupGetParameters
            $typed.WithHosts = $true
            Get-ZbxHostGroup -Filter $typed
            """);

        Assert.Empty(run.Errors);
        JsonElement request = Assert.Single(server.ParamsOf("hostgroup.get"));
        Assert.True(request.GetProperty("with_hosts").GetBoolean());
    }

    [Fact]
    public async Task A_service_status_rules_Type_reads_back_as_the_rule_type_enum()
    {
        // The read record's status_rules.type is the same enum the request takes (ZabbixServiceStatusRuleType), not an
        // int: overlay.json's fieldEnums "service.type" names the same family for both.
        await using FakeZabbix server = await FakeZabbix.StartAsync();
        server.On("service.get", """[{"serviceid":"3","name":"next-s","status_rules":[{"type":"0","limit_value":"1","limit_status":"2","new_status":"3"}]}]""");
        using Shell shell = Shell.Open();
        shell.Connect(server);

        Run run = shell.Invoke("(Get-ZbxService -Id 3 -SelectStatusRules).StatusRules[0].Type");

        Assert.Empty(run.Errors);
        Assert.Equal(ZabbixServiceStatusRuleType.AtLeastNWithStatusOrAbove, Assert.Single(run.Output).BaseObject);
    }

    [Fact]
    public void Get_ZbxHost_offers_every_current_select_option_host_get_takes()
    {
        // The hand-written Get-ZbxHost offers every select option, -SelectDiscoveryData/-SelectDiscoveryRules included,
        // as every generated Get- does from its parameters record. Deprecated options (Obsolete) are the generated cmdlets' only.
        string[] options = [.. typeof(ZabbixHostGetParameters).GetProperties()
            .Where(p => p.Name.StartsWith("Select", StringComparison.Ordinal) && !p.IsDefined(typeof(ObsoleteAttribute), inherit: false)
                && !p.IsDefined(typeof(ZabbixUnsupportedAttribute), inherit: false))
            .Select(p => p.Name)];
        string[] parameters = [.. typeof(Community.Zabbix.PowerShell.Commands.GetZbxHostCommand).GetProperties().Select(p => p.Name)];

        Assert.Empty(options.Except(parameters));
    }
}
