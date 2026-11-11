// This Source Code Form is subject to the terms of the Mozilla Public
// License, v. 2.0. Copyright (c) 2026 schroeder247. SPDX-License-Identifier: MPL-2.0
using System.Management.Automation;
using System.Text.Json;

namespace Community.Zabbix.PowerShell.Tests;

/// <summary>
/// One <c>Get</c>/<c>New</c>/<c>Set</c>/<c>Remove</c> quartet and one nested-record builder, end to end through the
/// PowerShell SDK and a real HTTP server. <c>hostgroup</c> is picked because its shape is small (a name and
/// a uuid) and it has no other entity's id to resolve, so the test is about the generated cmdlets themselves, not a
/// second entity's resolution (that is <see cref="ErrorAndConfirmationTests"/> and <see cref="SelectionTests"/>).
/// </summary>
public sealed class GeneratedCrudCommandsTests
{
    [Fact]
    public async Task Get_ZbxHostGroup_lists_the_generated_entity()
    {
        await using FakeZabbix server = await FakeZabbix.StartAsync();
        server.On("hostgroup.get", """[{"groupid":"1","name":"Linux servers"}]""");
        using Shell shell = Shell.Open();
        shell.Connect(server);

        Run run = shell.Invoke("Get-ZbxHostGroup");

        Assert.Empty(run.Errors);
        dynamic group = Assert.Single(run.Output);
        Assert.Equal("Linux servers", group.Name);
        Assert.Equal("1", group.GroupId.ToString());
    }

    [Fact]
    public async Task New_ZbxHostGroup_creates_and_returns_the_entity()
    {
        // New-Zbx<Entity> reads the created object(s) back through one .get for the whole batch and
        // writes the typed entity, the same shape Get-ZbxHostGroup returns, not a bare id.
        await using FakeZabbix server = await FakeZabbix.StartAsync();
        server.On("hostgroup.create", """{"groupids":["5"]}""");
        server.On("hostgroup.get", """[{"groupid":"5","name":"Databases"}]""");
        using Shell shell = Shell.Open();
        shell.Connect(server);

        Run run = shell.Invoke("New-ZbxHostGroup -Name 'Databases'");

        Assert.Empty(run.Errors);
        dynamic created = Assert.Single(run.Output);
        Assert.Equal("5", created.GroupId.ToString());
        Assert.Equal("Databases", created.Name);
        JsonElement request = Assert.Single(server.ParamsOf("hostgroup.create"));
        Assert.Equal("Databases", request.GetProperty("name").GetString());
        // The post-create readback selects by hostgroup.get's own top-level groupids
        // option, not filter.groupid.
        JsonElement readBack = Assert.Single(server.ParamsOf("hostgroup.get"));
        Assert.Equal("""["5"]""", readBack.GetProperty("groupids").GetRawText());
        Assert.False(readBack.TryGetProperty("filter", out _));
    }

    [Fact]
    public async Task New_ZbxHostGroup_does_not_prompt_under_the_default_ConfirmPreference()
    {
        // New-/Set- carry ConfirmImpact.Medium and call ShouldProcess alone, so an
        // ordinary call needs neither -Force nor -Confirm:$false under the engine's default $ConfirmPreference (High).
        await using FakeZabbix server = await FakeZabbix.StartAsync();
        server.On("hostgroup.create", """{"groupids":["5"]}""");
        server.On("hostgroup.get", """[{"groupid":"5","name":"Databases"}]""");
        using Shell shell = Shell.Open();
        shell.Connect(server);

        Run run = shell.Invoke("New-ZbxHostGroup -Name 'Databases'");

        Assert.Empty(run.Errors);
        Assert.Single(server.ParamsOf("hostgroup.create"));
    }

    [Fact]
    public async Task New_ZbxHostGroup_WhatIf_creates_nothing()
    {
        using Shell shell = Shell.Open();
        await using FakeZabbix server = await FakeZabbix.StartAsync();
        shell.Connect(server);

        Run run = shell.Invoke("New-ZbxHostGroup -Name 'Databases' -WhatIf");

        Assert.Empty(run.Errors);
        Assert.Empty(server.ParamsOf("hostgroup.create"));
    }

    [Fact]
    public async Task Set_ZbxHostGroup_resolves_by_name_reads_back_and_sends_only_the_bound_field()
    {
        await using FakeZabbix server = await FakeZabbix.StartAsync();
        // -InputObject 'Databases' resolves the name through a case-insensitive search, and the post-update readback
        // selects by the top-level groupids option, not filter.groupid, so the routing below tells the two apart by
        // whether a "search" with a "name" key is present at all.
        server.On("hostgroup.get", parameters => parameters.TryGetProperty("search", out JsonElement filter) && filter.TryGetProperty("name", out _)
            ? """[{"groupid":"5","name":"Databases"}]"""
            : """[{"groupid":"5","name":"SQL servers"}]""");
        server.On("hostgroup.update", """{"groupids":["5"]}""");
        using Shell shell = Shell.Open();
        shell.Connect(server);

        Run run = shell.Invoke("Set-ZbxHostGroup -InputObject 'Databases' -Name 'SQL servers' -PassThru");

        Assert.Empty(run.Errors);
        dynamic updated = Assert.Single(run.Output);
        Assert.Equal("5", updated.GroupId.ToString());
        Assert.Equal("SQL servers", updated.Name);
        JsonElement request = Assert.Single(server.ParamsOf("hostgroup.update")).EnumerateArray().Single();
        Assert.Equal("5", request.GetProperty("groupid").GetString());
        Assert.Equal("SQL servers", request.GetProperty("name").GetString());
        Assert.False(request.TryGetProperty("uuid", out _), "uuid was not bound and must not be sent");
    }

    [Fact]
    public async Task Set_ZbxHostGroup_piped_a_read_object_with_server_only_fields_does_not_echo_them_back()
    {
        // BuildRequest(ulong) always starts a fresh *UpdateRequest from the cmdlet's own bound parameters (plus
        // -AdditionalProperty); a piped ZabbixHostGroup resolves to nothing but its id (ZabbixReference<T>), so a field the
        // read reply carried in its own AdditionalFields bag (a newer Zabbix field, or anything the description does
        // not model) never reaches the update the cmdlet sends.
        await using FakeZabbix server = await FakeZabbix.StartAsync();
        server.On("hostgroup.get", """[{"groupid":"5","name":"Databases","brand_new_in_9_0":"server-only"}]""");
        server.On("hostgroup.update", """{"groupids":["5"]}""");
        using Shell shell = Shell.Open();
        shell.Connect(server);

        Run run = shell.Invoke("Get-ZbxHostGroup | Set-ZbxHostGroup -Name 'SQL servers'");

        Assert.Empty(run.Errors);
        JsonElement request = Assert.Single(server.ParamsOf("hostgroup.update")).EnumerateArray().Single();
        Assert.Equal("5", request.GetProperty("groupid").GetString());
        Assert.Equal("SQL servers", request.GetProperty("name").GetString());
        Assert.False(request.TryGetProperty("brand_new_in_9_0", out _), "a field the read reply carried in its own AdditionalFields bag must not be echoed back");
    }

    [Fact]
    public async Task Remove_ZbxHostGroup_resolves_by_name_then_deletes()
    {
        await using FakeZabbix server = await FakeZabbix.StartAsync();
        server.On("hostgroup.get", """[{"groupid":"5","name":"Databases"}]""");
        server.On("hostgroup.delete", """{"groupids":["5"]}""");
        using Shell shell = Shell.Open();
        shell.Connect(server);

        Run run = shell.Invoke("Remove-ZbxHostGroup -InputObject 'Databases' -Confirm:$false");

        Assert.Empty(run.Errors);
        JsonElement ids = Assert.Single(server.ParamsOf("hostgroup.delete"));
        Assert.Equal("5", ids.EnumerateArray().Single().GetString());
    }

    [Fact]
    public async Task Remove_ZbxHostGroup_prompts_by_default_so_a_host_that_cannot_prompt_removes_nothing()
    {
        // The generated Remove- carries ConfirmImpact.High, at the engine's default $ConfirmPreference.
        await using FakeZabbix server = await FakeZabbix.StartAsync();
        server.On("hostgroup.delete", """{"groupids":["5"]}""");
        using Shell shell = Shell.Open();
        shell.Connect(server);

        Run run = shell.Invoke("Remove-ZbxHostGroup -Id 5");

        Assert.NotEmpty(run.Errors);
        Assert.Empty(server.ParamsOf("hostgroup.delete"));
    }

    [Fact]
    public async Task Remove_ZbxHostGroup_Force_skips_the_prompt()
    {
        // -Force is the documented way (Learn RD05) to delete from a script without a prompt.
        await using FakeZabbix server = await FakeZabbix.StartAsync();
        server.On("hostgroup.delete", """{"groupids":["5"]}""");
        using Shell shell = Shell.Open();
        shell.Connect(server);

        Run run = shell.Invoke("Remove-ZbxHostGroup -Id 5 -Force");

        Assert.Empty(run.Errors);
        Assert.Equal("5", Assert.Single(server.ParamsOf("hostgroup.delete")).EnumerateArray().Single().GetString());
    }

    [Fact]
    public async Task Remove_ZbxHostGroup_reports_the_one_bad_id_of_a_piped_batch_and_removes_the_rest()
    {
        // A *.delete is one transaction, so one id that does not exist fails the whole batch (on 7.0.31, 7.4.15
        // and 8.0.0: "No permissions to referred object or it does not exist!", nothing deleted).
        // RemoveZbxCmdletBase.FlushChunk retries such a batch one id at a time: the good ids go, the bad one is
        // its own error record.
        await using FakeZabbix server = await FakeZabbix.StartAsync();
        server.On("hostgroup.delete", ids => ids.EnumerateArray().Any(id => id.GetString() == "999")
            ? FakeZabbix.Error(-32500, "Application error.", "No permissions to referred object or it does not exist!")
            : $$"""{"groupids":{{ids.GetRawText()}}}""");
        using Shell shell = Shell.Open();
        shell.Connect(server);

        Run run = shell.Invoke("'5', '999', '6' | Remove-ZbxHostGroup -Force");

        ErrorRecord failure = Assert.Single(run.Errors);
        Assert.StartsWith("ZabbixApi.PermissionOrNotFound,", failure.FullyQualifiedErrorId, StringComparison.Ordinal);
        Assert.Equal("999", failure.TargetObject?.ToString());
        Assert.Equal(
            ["5,999,6", "5", "999", "6"],
            server.ParamsOf("hostgroup.delete").Select(ids => string.Join(',', ids.EnumerateArray().Select(id => id.GetString()))));
    }

    [Fact]
    public async Task Remove_ZbxHostGroup_does_not_retry_a_batch_the_server_refused_for_the_caller()
    {
        // A refusal that is not about one id (the token, the role) fails every retry the same way: one error, one call.
        await using FakeZabbix server = await FakeZabbix.StartAsync();
        server.On("hostgroup.delete", FakeZabbix.Error(-32500, "Application error.", "No permissions to call \"hostgroup.delete\"."));
        using Shell shell = Shell.Open();
        shell.Connect(server);

        Run run = shell.Invoke("'5', '6' | Remove-ZbxHostGroup -Force");

        Assert.Single(run.Errors);
        Assert.Single(server.ParamsOf("hostgroup.delete"));
    }

    [Fact]
    public async Task Set_ZbxHostGroup_without_PassThru_writes_nothing_and_reads_nothing_back()
    {
        // A Set- is a sink and writes nothing without -PassThru (Learn, "Support the PassThru Parameter").
        await using FakeZabbix server = await FakeZabbix.StartAsync();
        server.On("hostgroup.update", """{"groupids":["5"]}""");
        using Shell shell = Shell.Open();
        shell.Connect(server);

        Run run = shell.Invoke("Set-ZbxHostGroup -Id 5 -Name 'SQL servers'");

        Assert.Empty(run.Errors);
        Assert.Empty(run.Output);
        Assert.Single(server.ParamsOf("hostgroup.update"));
        Assert.Empty(server.ParamsOf("hostgroup.get"));
    }

    [Fact]
    public async Task Set_ZbxHostGroup_accepts_the_shipped_Id_spelling_of_InputObject()
    {
        await using FakeZabbix server = await FakeZabbix.StartAsync();
        server.On("hostgroup.update", """{"groupids":["5"]}""");
        server.On("hostgroup.get", """[{"groupid":"5","name":"SQL servers"}]""");
        using Shell shell = Shell.Open();
        shell.Connect(server);

        Run run = shell.Invoke("Set-ZbxHostGroup -Id 5 -Name 'SQL servers'");

        Assert.Empty(run.Errors);
        JsonElement request = Assert.Single(server.ParamsOf("hostgroup.update")).EnumerateArray().Single();
        Assert.Equal("5", request.GetProperty("groupid").GetString());
    }

    [Fact]
    public async Task Remove_ZbxHostGroup_accepts_the_shipped_Id_spelling_of_InputObject()
    {
        await using FakeZabbix server = await FakeZabbix.StartAsync();
        server.On("hostgroup.delete", """{"groupids":["5"]}""");
        using Shell shell = Shell.Open();
        shell.Connect(server);

        Run run = shell.Invoke("Remove-ZbxHostGroup -Id 5 -Confirm:$false");

        Assert.Empty(run.Errors);
        JsonElement ids = Assert.Single(server.ParamsOf("hostgroup.delete"));
        Assert.Equal("5", ids.EnumerateArray().Single().GetString());
    }

    [Fact]
    public async Task Remove_ZbxHostGroup_Id_also_takes_a_piped_object_like_InputObject_does()
    {
        // -Id is an alias of -InputObject (the same ZabbixReference<T> parameter), not a second, independent
        // parameter set: it shares InputObject's ValueFromPipeline binding, so a piped object resolves the same way
        // whichever spelling named it. (A generic object's "Id" property does not bind through it — ZabbixReference's
        // pipeline-by-value binding always runs its own argument transformation, which recognizes an id, a name, or
        // the entity's own object, not an arbitrary property-name match.)
        await using FakeZabbix server = await FakeZabbix.StartAsync();
        server.On("hostgroup.get", """[{"groupid":"5","name":"Databases"}]""");
        server.On("hostgroup.delete", """{"groupids":["5"]}""");
        using Shell shell = Shell.Open();
        shell.Connect(server);

        Run run = shell.Invoke("Get-ZbxHostGroup | Remove-ZbxHostGroup -Confirm:$false");

        Assert.Empty(run.Errors);
        JsonElement ids = Assert.Single(server.ParamsOf("hostgroup.delete"));
        Assert.Equal("5", ids.EnumerateArray().Single().GetString());
    }

    [Fact]
    public async Task New_ZbxHostGroup_Properties_wins_over_the_named_parameter()
    {
        await using FakeZabbix server = await FakeZabbix.StartAsync();
        server.On("hostgroup.create", """{"groupids":["5"]}""");
        server.On("hostgroup.get", """[{"groupid":"5","name":"Overridden"}]""");
        using Shell shell = Shell.Open();
        shell.Connect(server);

        Run run = shell.Invoke("New-ZbxHostGroup -Name 'Databases' -AdditionalProperty @{ Name = 'Overridden' } -Force");

        Assert.Empty(run.Errors);
        JsonElement request = Assert.Single(server.ParamsOf("hostgroup.create"));
        Assert.Equal("Overridden", request.GetProperty("name").GetString());
    }

    [Fact]
    public async Task New_ZbxHostGroup_Properties_sends_uuid_a_field_the_named_parameters_do_not_expose()
    {
        // ZabbixHostGroupCreateRequest models Uuid (CHostGroup.php accepts it for template-matching import), but no
        // generated New-ZbxHostGroup parameter exposes it: -AdditionalProperty is the only way to set it from the shell.
        await using FakeZabbix server = await FakeZabbix.StartAsync();
        server.On("hostgroup.create", """{"groupids":["5"]}""");
        server.On("hostgroup.get", """[{"groupid":"5","name":"g"}]""");
        using Shell shell = Shell.Open();
        shell.Connect(server);

        Run run = shell.Invoke("New-ZbxHostGroup -Name 'g' -AdditionalProperty @{ uuid = 'e5c4a1c4b1b74e2f9c6e5b1a2d3c4e5f' } -Force");

        Assert.Empty(run.Errors);
        JsonElement request = Assert.Single(server.ParamsOf("hostgroup.create"));
        Assert.Equal("e5c4a1c4b1b74e2f9c6e5b1a2d3c4e5f", request.GetProperty("uuid").GetString());
    }

    [Fact]
    public async Task New_ZbxHostGroup_Properties_with_a_key_matching_no_property_routes_it_through_AdditionalFields()
    {
        // "monitored_by" names no ZabbixHostGroupCreateRequest property (it is not a hostgroup field at all): the
        // hashtable key is not refused, it is sent verbatim through AdditionalFields, same as an SDK user on a
        // newer Zabbix would send a field this model does not know yet.
        await using FakeZabbix server = await FakeZabbix.StartAsync();
        server.On("hostgroup.create", """{"groupids":["5"]}""");
        server.On("hostgroup.get", """[{"groupid":"5","name":"Databases"}]""");
        using Shell shell = Shell.Open();
        shell.Connect(server);

        Run run = shell.Invoke("New-ZbxHostGroup -Name 'Databases' -AdditionalProperty @{ monitored_by = 1 } -Force");

        Assert.Empty(run.Errors);
        JsonElement request = Assert.Single(server.ParamsOf("hostgroup.create"));
        Assert.Equal("Databases", request.GetProperty("name").GetString());
        Assert.Equal(1, request.GetProperty("monitored_by").GetInt32());
    }

    [Fact]
    public async Task New_ZbxItem_a_plain_trapper_item_needs_no_type_specific_fields()
    {
        // MasterItemId/Url/SnmpOid are only required by the Zabbix API for specific -Type values (Dependent item,
        // HTTP agent, SNMP agent: CItemType::getCreateFieldRule, 8.0), so the request record does not declare them
        // required (the description reads the item types' rules as alternatives) and a trapper item sends none of them.
        await using FakeZabbix server = await FakeZabbix.StartAsync();
        server.On("item.create", """{"itemids":["55"]}""");
        server.On("item.get", """[{"itemid":"55","hostid":"10084","name":"Trap","key_":"trap.key"}]""");
        using Shell shell = Shell.Open();
        shell.Connect(server);

        Run run = shell.Invoke("New-ZbxItem -HostId 10084 -Name 'Trap' -Type Trapper -Key 'trap.key' -ValueType Unsigned -Force");

        Assert.Empty(run.Errors);
        dynamic created = Assert.Single(run.Output);
        Assert.Equal("55", created.ItemId.ToString());
        JsonElement request = Assert.Single(server.ParamsOf("item.create"));
        Assert.Equal("10084", request.GetProperty("hostid").GetString());
        Assert.Equal("Trap", request.GetProperty("name").GetString());
        Assert.Equal("trap.key", request.GetProperty("key_").GetString());
        Assert.False(request.TryGetProperty("master_itemid", out _));
        Assert.False(request.TryGetProperty("url", out _));
        Assert.False(request.TryGetProperty("snmp_oid", out _));
    }

    [Fact]
    public void ServiceStatusRuleType_members_are_the_shipped_modules_readable_names_not_the_wire_terse_defines()
    {
        // Golden: the description's own extraction turns ZBX_SERVICE_STATUS_RULE_TYPE_N_GE into "NGe" — a member
        // name that only decodes correctly against CService.php's wire encoding. schema/overlay.json's
        // "ZBX_SERVICE_STATUS_RULE_TYPE" entry overrides every member to descriptive names (the unreleased 26.x module's
        // ServiceStatusRuleType names, docs/adr/0001-repository-layout.md), so every caller gets a self-explanatory one.
        string[] expected =
        [
            "AtLeastNWithStatusOrAbove", "AtLeastPercentWithStatusOrAbove",
            "LessThanNWithStatusOrBelow", "LessThanPercentWithStatusOrBelow",
            "WeightAtLeastWithStatusOrAbove", "WeightAtLeastPercentWithStatusOrAbove",
            "WeightLessThanWithStatusOrBelow", "WeightLessThanPercentWithStatusOrBelow",
        ];

        Assert.Equal(expected, Enum.GetNames<ZabbixServiceStatusRuleType>());
    }

    [Fact]
    public void RoleType_members_are_the_shipped_modules_readable_names_not_ZabbixUser_ZabbixAdmin()
    {
        // Golden: USER_TYPE_ZABBIX_USER/USER_TYPE_ZABBIX_ADMIN extract to "ZabbixUser"/"ZabbixAdmin" by default (the
        // define's own prefix repeats the family name); schema/overlay.json's "USER_TYPE" entry drops the redundant
        // "Zabbix" prefix (RoleType.User/RoleType.Admin/RoleType.SuperAdmin). None is the type's default, which no define
        // names.
        Assert.Equal(["None", "User", "Admin", "SuperAdmin"], Enum.GetNames<ZabbixRoleType>());
    }

    [Fact]
    public void New_ZbxTag_builds_a_hashtable_any_owners_tag_record_casts_from()
    {
        using Shell shell = Shell.Open();

        Run run = shell.Invoke("New-ZbxTag -Tag 'env' -Value 'prod'");

        Assert.Empty(run.Errors);
        var tag = Assert.IsType<System.Collections.Hashtable>(Assert.Single(run.Output).BaseObject);
        Assert.Equal("env", tag["Tag"]);
        Assert.Equal("prod", tag["Value"]);
    }

    [Theory]
    [InlineData("New-ZbxActionCondition -ConditionType HostGroup -Value 7", "conditiontype", "$c.conditiontype")]
    [InlineData("New-ZbxLldMacroPath -LowLevelDiscoveryMacro '{#FS}' -Path '$.fs'", "lld_macro", "$c.lld_macro")]
    [InlineData("New-ZbxMaintenanceTimePeriod Onetime -Period 3600", "timeperiod_type", "$c.timeperiod_type")]
    [InlineData("New-ZbxMapShape -Type Rectangle -X 10 -Y 20 -Width 30 -Height 40", "x", "$c.X")]
    [InlineData("New-ZbxTag -Tag env -Value prod", "tag", "$c.Tag")]
    public void Nested_builders_key_their_hashtable_by_wire_name_and_resolve_members_case_insensitively(string build, string wireKey, string read)
    {
        // The builders key by the record's wire name (as New-ZbxNetworkDiscoveryCheck does), case-insensitively, so
        // $c.conditiontype finds the key the way PowerShell's own @{} would; a case-sensitive hashtable keyed by C#
        // member names would miss it.
        using Shell shell = Shell.Open();

        Run run = shell.Invoke("$c = " + build + "; $c.Keys -ccontains '" + wireKey + "'; $null -ne " + read);

        Assert.Empty(run.Errors);
        Assert.Equal(["True", "True"], run.Output.Select(o => o.ToString()));
    }

    [Fact]
    public async Task A_wire_keyed_action_condition_builds_the_action_create_filter()
    {
        await using FakeZabbix server = await FakeZabbix.StartAsync();
        server.On("action.create", """{"actionids":["5"]}""");
        server.On("action.get", """[{"actionid":"5","name":"next-a"}]""");
        using Shell shell = Shell.Open();
        shell.Connect(server);

        Run run = shell.Invoke("""
            $c = New-ZbxActionCondition -ConditionType HostGroup -Value 7
            $c.conditiontype
            New-ZbxAction -Name next-a -EventSource Triggers -Filter @{ evaltype = 0; conditions = @($c) } -Confirm:$false | Out-Null
            """);

        Assert.Empty(run.Errors);
        // The wire's number, not the CLR member name: a raw-API body built from "HostGroup" is refused by action.create.
        Assert.Equal("0", Assert.Single(run.Output).ToString());
        JsonElement condition = Assert.Single(Assert.Single(server.ParamsOf("action.create")).GetProperty("filter").GetProperty("conditions").EnumerateArray());
        Assert.Equal(0, condition.GetProperty("conditiontype").GetInt32());
        Assert.Equal("7", condition.GetProperty("value").GetString());
    }
}
