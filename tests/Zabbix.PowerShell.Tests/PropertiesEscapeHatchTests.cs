// This Source Code Form is subject to the terms of the Mozilla Public
// License, v. 2.0. Copyright (c) 2026 schroeder247. SPDX-License-Identifier: MPL-2.0
using System.Text.Json;

namespace Community.Zabbix.PowerShell.Tests;

/// <summary>
/// <c>-AdditionalProperty</c> supplying a raw wire field name for a field that also has a
/// friendlier named parameter (e.g. <c>ipmi_username</c> for <c>-IpmiUserName</c>), without using that named
/// parameter this call, must reach the wire on the modelled property — not be refused just because
/// <c>ZabbixHostCreateRequest</c> happens to model that wire field somewhere. <c>ZabbixPropertyOverrides.Apply</c>
/// matches a <c>-AdditionalProperty</c> key against a property by its wire name (<c>[JsonPropertyName]</c>), not only by
/// its C# name, so a snake_case key like <c>ipmi_username</c> maps onto <c>IpmiUserName</c> directly instead of
/// falling through to the <c>AdditionalFields</c> bag <see cref="ZabbixExtensionDataGuard"/> would then refuse.
/// </summary>
public sealed class PropertiesEscapeHatchTests
{
    [Fact]
    public async Task New_ZbxHost_Properties_ipmi_username_maps_onto_the_IpmiUsername_property_not_the_bag()
    {
        await using FakeZabbix server = await FakeZabbix.StartAsync();
        server.On("host.create", """{"hostids":["10084"]}""");
        server.On("host.get", FakeZabbix.Hosts(("10084", "web-01")));
        using Shell shell = Shell.Open();
        shell.Connect(server);

        Run run = shell.Invoke("New-ZbxHost -Host web-01 -Group @('7') -AdditionalProperty @{ ipmi_username = 'root' } -Confirm:$false");

        Assert.Empty(run.Errors);
        JsonElement request = Assert.Single(server.ParamsOf("host.create"));
        Assert.Equal("root", request.GetProperty("ipmi_username").GetString());
    }

    [Fact]
    public async Task New_ZbxTemplate_Properties_vendor_name_and_vendor_version_map_onto_their_properties()
    {
        await using FakeZabbix server = await FakeZabbix.StartAsync();
        server.On("template.create", """{"templateids":["10822"]}""");
        server.On("template.get", """[{"templateid":"10822","host":"tpl"}]""");
        using Shell shell = Shell.Open();
        shell.Connect(server);

        Run run = shell.Invoke("New-ZbxTemplate -Name tpl -Group 1 -AdditionalProperty @{ vendor_name = 'Acme'; vendor_version = '1.0' } -Confirm:$false");

        Assert.Empty(run.Errors);
        JsonElement request = Assert.Single(server.ParamsOf("template.create"));
        Assert.Equal("Acme", request.GetProperty("vendor_name").GetString());
        Assert.Equal("1.0", request.GetProperty("vendor_version").GetString());
    }

    [Fact]
    public async Task Properties_still_routes_a_genuinely_unmodelled_key_into_AdditionalFields()
    {
        await using FakeZabbix server = await FakeZabbix.StartAsync();
        server.On("host.create", """{"hostids":["10084"]}""");
        server.On("host.get", FakeZabbix.Hosts(("10084", "web-01")));
        using Shell shell = Shell.Open();
        shell.Connect(server);

        Run run = shell.Invoke("New-ZbxHost -Host web-01 -Group @('7') -AdditionalProperty @{ a_future_api_field = 'x' } -Confirm:$false");

        Assert.Empty(run.Errors);
        JsonElement request = Assert.Single(server.ParamsOf("host.create"));
        Assert.Equal("x", request.GetProperty("a_future_api_field").GetString());
    }

    [Fact]
    public void Apply_maps_a_snake_case_Properties_key_onto_the_matching_wire_property_directly()
    {
        var request = new ZabbixHostCreateRequest { HostName = "web-01", Groups = [] };

        ZabbixHostCreateRequest result = ZabbixPropertyOverrides.Apply(request, new System.Collections.Hashtable { ["ipmi_username"] = "root" });

        Assert.Equal("root", result.IpmiUserName);
        Assert.Null(result.AdditionalFields);
    }

    // A -AdditionalProperty value destined for a nested-record-LIST field (IReadOnlyList<TNested>) must not be handed to
    // LanguagePrimitives.ConvertTo AS that interface type, which throws ("Cannot convert the
    // ... value ... to type IReadOnlyList`1[...]") even though the same shape converts cleanly to the
    // concrete TNested[]; separately, ConvertTo's own hashtable-to-object matching is case-insensitive but does not
    // fold away a wire name's underscore ("timeperiod_type" does not bind TimePeriodType), so a -AdditionalProperty value's
    // wire-shaped keys need translating to the nested type's CLR names first. A New-ZbxMaintenance call
    // is the scenario: groups/timeperiods (nested-record lists), tags
    // (a second nested-record list, exercising the "more than one" case), and description (an ordinary scalar,
    // to confirm the -AdditionalProperty fast path for non-list fields is unaffected).

    [Fact]
    public void Apply_coerces_a_Properties_hashtable_array_onto_an_IReadOnlyList_of_nested_records()
    {
        var request = new ZabbixMaintenanceCreateRequest { Name = "m", ActiveSince = DateTimeOffset.UnixEpoch, ActiveTill = DateTimeOffset.UnixEpoch, TimePeriods = [] };
        var properties = new System.Collections.Hashtable
        {
            ["timeperiods"] = new object[] { new System.Collections.Hashtable { ["timeperiod_type"] = 0, ["period"] = 3600 } },
        };

        ZabbixMaintenanceCreateRequest result = ZabbixPropertyOverrides.Apply(request, properties);

        ZabbixMaintenanceTimePeriodRequest period = Assert.Single(result.TimePeriods);
        Assert.Equal(ZabbixDuration.Parse("3600"), period.Period);
        Assert.Equal(ZabbixTimePeriodType.OneTime, period.TimePeriodType);
        Assert.Null(result.AdditionalFields);
    }

    [Fact]
    public void Apply_coerces_multiple_nested_record_list_fields_and_a_plain_scalar_in_one_call()
    {
        var request = new ZabbixMaintenanceCreateRequest { Name = "m", ActiveSince = DateTimeOffset.UnixEpoch, ActiveTill = DateTimeOffset.UnixEpoch, TimePeriods = [] };
        var properties = new System.Collections.Hashtable
        {
            ["description"] = "scalar-field-check",
            ["groups"] = new object[] { new System.Collections.Hashtable { ["groupid"] = "7" } },
            ["timeperiods"] = new object[] { new System.Collections.Hashtable { ["timeperiod_type"] = 0, ["period"] = 3600 } },
            ["tags"] = new object[] { new System.Collections.Hashtable { ["tag"] = "env", ["value"] = "staging" } },
        };

        ZabbixMaintenanceCreateRequest result = ZabbixPropertyOverrides.Apply(request, properties);

        Assert.Equal("scalar-field-check", result.Description);
        Assert.Equal(7UL, Assert.Single(result.Groups!).GroupId);
        Assert.Equal(ZabbixDuration.Parse("3600"), Assert.Single(result.TimePeriods).Period);
        ZabbixMaintenanceTagRequest tag = Assert.Single(result.Tags!);
        Assert.Equal("env", tag.Tag);
        Assert.Equal("staging", tag.Value);
    }

    [Fact]
    public async Task New_ZbxMaintenance_Properties_sends_nested_timeperiods_groups_tags_and_a_scalar_on_the_wire()
    {
        await using FakeZabbix server = await FakeZabbix.StartAsync();
        server.On("maintenance.create", """{"maintenanceids":["3"]}""");
        server.On("maintenance.get", """[{"maintenanceid":"3","name":"maint-01"}]""");
        using Shell shell = Shell.Open();
        shell.Connect(server);

        Run run = shell.Invoke("""
            New-ZbxMaintenance -Name maint-01 -ActiveSince 1 -ActiveTill 2 -AdditionalProperty @{
                description = 'scalar-field-check'
                groups      = @(@{ groupid = '7' })
                timeperiods = @(@{ timeperiod_type = 0; period = 3600 })
                tags        = @(@{ tag = 'env'; value = 'staging' })
            } -Confirm:$false
            """);

        Assert.Empty(run.Errors);
        JsonElement request = Assert.Single(server.ParamsOf("maintenance.create"));
        Assert.Equal("scalar-field-check", request.GetProperty("description").GetString());
        Assert.Equal("7", Assert.Single(request.GetProperty("groups").EnumerateArray()).GetProperty("groupid").GetString());
        JsonElement period = Assert.Single(request.GetProperty("timeperiods").EnumerateArray());
        Assert.Equal("3600", period.GetProperty("period").GetString());
        JsonElement tag = Assert.Single(request.GetProperty("tags").EnumerateArray());
        Assert.Equal("env", tag.GetProperty("tag").GetString());
        Assert.Equal("staging", tag.GetProperty("value").GetString());
    }

    // Substituting the concrete array TYPE for the interface-typed list field only at the top level of a -AdditionalProperty
    // value is not enough for a TWO-level nest (a nested-record-list field itself carrying a nested-record-list
    // field, e.g. ZabbixDashboardPageRequest.Widgets inside ZabbixDashboardCreateRequest.Pages):
    // LanguagePrimitives.ConvertTo builds the outer array itself, and for each element it converts, PowerShell's own
    // hashtable-to-object binder assigns the inner "widgets" value straight onto the interface-typed property,
    // hitting the same "Cannot convert ... to IReadOnlyList" error. So ConvertValue converts bottom-up (recursively).
    [Fact]
    public void Apply_coerces_a_two_level_nested_record_list_a_list_field_inside_a_list_field()
    {
        var request = new ZabbixDashboardCreateRequest { Name = "d", Pages = [] };
        var properties = new System.Collections.Hashtable
        {
            ["pages"] = new object[]
            {
                new System.Collections.Hashtable
                {
                    ["widgets"] = new object[]
                    {
                        new System.Collections.Hashtable { ["type"] = "clock", ["view_mode"] = 0 },
                    },
                },
            },
        };

        ZabbixDashboardCreateRequest result = ZabbixPropertyOverrides.Apply(request, properties);

        ZabbixDashboardPageRequest page = Assert.Single(result.Pages);
        ZabbixDashboardPageWidgetRequest widget = Assert.Single(page.Widgets!);
        Assert.Equal("clock", widget.Type);
    }

    [Fact]
    public async Task New_ZbxDashboard_Properties_sends_a_two_level_nested_pages_widgets_list_on_the_wire()
    {
        await using FakeZabbix server = await FakeZabbix.StartAsync();
        server.On("dashboard.create", """{"dashboardids":["9"]}""");
        server.On("dashboard.get", """[{"dashboardid":"9","name":"dash-01"}]""");
        using Shell shell = Shell.Open();
        shell.Connect(server);

        Run run = shell.Invoke("""
            New-ZbxDashboard -Name dash-01 -AdditionalProperty @{
                pages = @(@{ widgets = @(@{ type = 'clock' }) })
            } -Confirm:$false
            """);

        Assert.Empty(run.Errors);
        JsonElement request = Assert.Single(server.ParamsOf("dashboard.create"));
        JsonElement page = Assert.Single(request.GetProperty("pages").EnumerateArray());
        JsonElement widget = Assert.Single(page.GetProperty("widgets").EnumerateArray());
        Assert.Equal("clock", widget.GetProperty("type").GetString());
    }

    [Fact]
    public async Task New_ZbxDashboard_Properties_sends_a_three_level_nested_pages_widgets_fields_list_on_the_wire()
    {
        // THREE levels of nested-record lists in one -AdditionalProperty
        // call (Pages -> Widgets -> Fields), each an IReadOnlyList<T> the JSON binder must populate without ever
        // handing LanguagePrimitives an interface type to convert to.
        await using FakeZabbix server = await FakeZabbix.StartAsync();
        server.On("dashboard.create", """{"dashboardids":["9"]}""");
        server.On("dashboard.get", """[{"dashboardid":"9","name":"dash-01"}]""");
        using Shell shell = Shell.Open();
        shell.Connect(server);

        Run run = shell.Invoke("""
            New-ZbxDashboard -Name dash-01 -AdditionalProperty @{
                pages = @(@{ widgets = @(@{ type = 'problems'; fields = @(@{ type = 1; name = 'x'; value = 'y' }) }) })
            } -Confirm:$false
            """);

        Assert.Empty(run.Errors);
        JsonElement request = Assert.Single(server.ParamsOf("dashboard.create"));
        JsonElement page = Assert.Single(request.GetProperty("pages").EnumerateArray());
        JsonElement widget = Assert.Single(page.GetProperty("widgets").EnumerateArray());
        Assert.Equal("problems", widget.GetProperty("type").GetString());
        JsonElement field = Assert.Single(widget.GetProperty("fields").EnumerateArray());
        Assert.Equal(1, field.GetProperty("type").GetInt32());
        Assert.Equal("x", field.GetProperty("name").GetString());
        Assert.Equal("y", field.GetProperty("value").GetString());
    }

    [Fact]
    public async Task New_ZbxHost_Properties_accepts_a_PSCustomObject_nested_inside_a_list_field()
    {
        // ZabbixJsonBinder/ZabbixJsonShape.FromShell must shape a [pscustomobject] the same way ConvertTo-Json
        // would (its own adapted properties, not its declared CLR type — PSCustomObject itself carries none) —
        // exercised where the binder actually has to walk INTO it: a nested-record-list field's own element.
        await using FakeZabbix server = await FakeZabbix.StartAsync();
        server.On("host.create", """{"hostids":["10084"]}""");
        server.On("host.get", FakeZabbix.Hosts(("10084", "web-01")));
        using Shell shell = Shell.Open();
        shell.Connect(server);

        Run run = shell.Invoke("""
            $iface = [pscustomobject]@{ type = 1; ip = '10.0.0.5'; dns = ''; port = '10050'; useip = 1; main = 1 }
            New-ZbxHost -Host web-01 -Group @('7') -AdditionalProperty @{ interfaces = @($iface) } -Confirm:$false
            """);

        Assert.Empty(run.Errors);
        JsonElement request = Assert.Single(server.ParamsOf("host.create"));
        JsonElement iface = Assert.Single(request.GetProperty("interfaces").EnumerateArray());
        Assert.Equal("10.0.0.5", iface.GetProperty("ip").GetString());
    }

    [Fact]
    public void Apply_refuses_two_Properties_keys_that_resolve_to_the_same_wire_field_with_different_values()
    {
        var request = new ZabbixHostCreateRequest { HostName = "web-01", Groups = [] };
        var properties = new System.Collections.Hashtable
        {
            ["ipmi_username"] = "root",
            ["IpmiUserName"] = "someone-else",
        };

        ArgumentException failure = Assert.Throws<ArgumentException>(() => ZabbixPropertyOverrides.Apply(request, properties));
        Assert.Contains("ipmi_username", failure.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void Apply_allows_two_Properties_keys_that_resolve_to_the_same_wire_field_with_the_same_value()
    {
        var request = new ZabbixHostCreateRequest { HostName = "web-01", Groups = [] };
        var properties = new System.Collections.Hashtable
        {
            ["ipmi_username"] = "root",
            ["IpmiUserName"] = "root",
        };

        ZabbixHostCreateRequest result = ZabbixPropertyOverrides.Apply(request, properties);

        Assert.Equal("root", result.IpmiUserName);
    }
}
