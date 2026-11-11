// This Source Code Form is subject to the terms of the Mozilla Public
// License, v. 2.0. Copyright (c) 2026 schroeder247. SPDX-License-Identifier: MPL-2.0
extern alias ZabbixGenerator;

using ZabbixGenerator::Community.Zabbix.Generator;

namespace Community.Zabbix.Tests;

/// <summary>
/// The cmdlet emitter reads what it does differently per object from the description, not from tables of its own: the
/// object an id refers to and the owner that changes it (<c>references</c>), a field required only under a condition
/// (<c>requiredWhen</c>), and the reviewed PowerShell shape (<c>cmdlets</c>: objects skipped or with a hand-written
/// <c>Get-</c>, enum fields taken as switches, get options taken as switches or enum arrays, nested-record builders, the visible and
/// technical name fields, and the property a field takes where its own name would hide a <c>PSCmdlet</c> member).
/// </summary>
public sealed class CmdletShapeTests
{
    private const string Description = """
        {
         "versions": ["7.0", "8.0"],
         "enums": [
          {"name": "WidgetClosing", "family": "WIDGET_CLOSING", "members": [{"name": "NotAllowed", "value": 0}, {"name": "Allowed", "value": 1}]},
          {"name": "WidgetSize", "family": "WIDGET_SIZE", "members": [{"name": "Small", "value": 0}, {"name": "Large", "value": 1}]}
         ],
         "types": [
          {"name": "HostGroup", "role": "read", "object": "hostgroup", "fields": [{"name": "GroupId", "wire": "groupid", "kind": "id"}, {"name": "Name", "wire": "name", "kind": "string"}]},
          {"name": "HostGroupGetParameters", "role": "get", "object": "hostgroup", "method": "hostgroup.get", "getParameters": true, "fields": [{"name": "GroupIds", "wire": "groupids", "kind": "list:id"}]},
          {"name": "TemplateGroup", "role": "read", "object": "templategroup", "fields": [{"name": "GroupId", "wire": "groupid", "kind": "id"}, {"name": "Name", "wire": "name", "kind": "string"}]},
          {"name": "TemplateGroupGetParameters", "role": "get", "object": "templategroup", "method": "templategroup.get", "getParameters": true, "fields": [{"name": "GroupIds", "wire": "groupids", "kind": "list:id"}]},
          {"name": "Template", "role": "read", "object": "template", "fields": [{"name": "TemplateId", "wire": "templateid", "kind": "id"}, {"name": "Name", "wire": "name", "kind": "string"}]},
          {"name": "TemplateGetParameters", "role": "get", "object": "template", "method": "template.get", "getParameters": true, "fields": [
            {"name": "TemplateIds", "wire": "templateids", "kind": "list:id"}, {"name": "GroupIds", "wire": "groupids", "kind": "list:id"}]},
          {"name": "TemplateCreateRequest", "role": "request", "object": "template", "method": "template.create", "fields": [
            {"name": "Name", "wire": "name", "kind": "string", "required": true}, {"name": "Groups", "wire": "groups", "kind": "list:object:TemplateGroupRequest"}]},
          {"name": "TemplateGroupRequest", "role": "nested-request", "fields": [{"name": "GroupId", "wire": "groupid", "kind": "id"}]},
          {"name": "Widget", "role": "read", "object": "widget", "fields": [{"name": "WidgetId", "wire": "widgetid", "kind": "id"}, {"name": "Name", "wire": "name", "kind": "string"}]},
          {"name": "WidgetGetParameters", "role": "get", "object": "widget", "method": "widget.get", "getParameters": true, "fields": [
            {"name": "WidgetIds", "wire": "widgetids", "kind": "list:id"}, {"name": "GroupIds", "wire": "groupids", "kind": "list:id"},
            {"name": "TemplateIds", "wire": "templateids", "kind": "list:id"}, {"name": "Editable", "wire": "editable", "kind": "apibool"},
            {"name": "Sizes", "wire": "sizes", "kind": "list:enum:WidgetSize"}]},
          {"name": "WidgetCreateRequest", "role": "request", "object": "widget", "method": "widget.create", "fields": [
            {"name": "Name", "wire": "name", "kind": "string", "required": true}, {"name": "Url", "wire": "url", "kind": "string", "required": true},
            {"name": "Closing", "wire": "closing", "kind": "enum:WidgetClosing"}, {"name": "Groups", "wire": "groups", "kind": "list:object:WidgetGroupRequest"}]},
          {"name": "WidgetGroupRequest", "role": "nested-request", "fields": [{"name": "GroupId", "wire": "groupid", "kind": "id"}]},
          {"name": "Gadget", "role": "read", "object": "gadget", "fields": [{"name": "GadgetId", "wire": "gadgetid", "kind": "id"}]},
          {"name": "GadgetGetParameters", "role": "get", "object": "gadget", "method": "gadget.get", "getParameters": true, "fields": [{"name": "GadgetIds", "wire": "gadgetids", "kind": "list:id"}]}
         ],
         "services": [
          {"object": "hostgroup", "client": "ZabbixHostGroupClient", "property": "HostGroups", "model": "HostGroup", "methods": [
            {"api": "hostgroup.get", "name": "GetAsync", "shape": "get-list", "parameters": "object:HostGroupGetParameters", "result": "list:object:HostGroup"}]},
          {"object": "templategroup", "client": "ZabbixTemplateGroupClient", "property": "TemplateGroups", "model": "TemplateGroup", "methods": [
            {"api": "templategroup.get", "name": "GetAsync", "shape": "get-list", "parameters": "object:TemplateGroupGetParameters", "result": "list:object:TemplateGroup"}]},
          {"object": "template", "client": "ZabbixTemplateClient", "property": "Templates", "model": "Template", "methods": [
            {"api": "template.get", "name": "GetAsync", "shape": "get-list", "parameters": "object:TemplateGetParameters", "result": "list:object:Template"},
            {"api": "template.create", "name": "CreateAsync", "shape": "call", "parameters": "list:object:TemplateCreateRequest", "result": "ids:templateids"}]},
          {"object": "widget", "client": "ZabbixWidgetClient", "property": "Widgets", "model": "Widget", "requiredWhen": {"url": "Closing is Allowed"}, "methods": [
            {"api": "widget.get", "name": "GetAsync", "shape": "get-list", "parameters": "object:WidgetGetParameters", "result": "list:object:Widget"},
            {"api": "widget.create", "name": "CreateAsync", "shape": "call", "parameters": "list:object:WidgetCreateRequest", "result": "ids:widgetids"}]},
          {"object": "gadget", "client": "ZabbixGadgetClient", "property": "Gadgets", "model": "Gadget", "methods": [
            {"api": "gadget.get", "name": "GetAsync", "shape": "get-list", "parameters": "object:GadgetGetParameters", "result": "list:object:Gadget"}]}
         ],
         "references": {"ids": {"groupid": "hostgroup", "templateid": "template"}, "owners": {"template": {"groupid": "templategroup"}}},
         "cmdlets": {
          "objects": {
           "gadget": {"skip": "a hand-written command covers it"},
           "hostgroup": {"handWrittenGet": "Get-ZbxHostGroup is hand-written"},
           "widget": {"switches": {"closing": ["Allowed", "NotAllowed"]}}
          },
          "builders": [
           {"concept": "WidgetGroup", "record": "WidgetGroupRequest", "positional": ["groupid"], "noun": "widget group", "use": "the -Group of New-ZbxWidget", "what": "A group the widget is in."},
           {"concept": "Missing", "record": "NoSuchRequest", "noun": "missing", "use": "nothing", "what": "Nothing."}
          ],
          "getSwitches": {"editable": {"parameter": "Editable", "summary": "Only objects the current user can edit."}},
          "getLists": {"sizes": {"parameter": "Size", "summary": "Only widgets of these sizes."}}
         }
        }
        """;

    private static readonly EmitResult s_emitted = new ZabbixCmdletEmitter(ApiDescription.Read(Description)).Emit(TestContext.Current.CancellationToken);

    private static string File(string name) => s_emitted.Files.Single(file => file.Name == name).Code;

    [Fact]
    public void A_group_id_under_a_template_refers_to_template_groups_and_elsewhere_to_host_groups()
    {
        Assert.Contains("ZabbixReference<global::Community.Zabbix.ZabbixTemplateGroup>[]? Group { get; set; }", File("TemplateCommands.g.cs"), StringComparison.Ordinal);
        Assert.Contains("ZabbixReference<global::Community.Zabbix.ZabbixTemplateGroup>[]? Group ", File("TemplateCommands.g.cs"), StringComparison.Ordinal);
        Assert.Contains("ZabbixReference<global::Community.Zabbix.ZabbixHostGroup>[]? Group { get; set; }", File("WidgetCommands.g.cs"), StringComparison.Ordinal);
        Assert.Contains("ZabbixReference<global::Community.Zabbix.ZabbixHostGroup>[]? Group ", File("WidgetCommands.g.cs"), StringComparison.Ordinal);
        Assert.Contains("ZabbixReference<global::Community.Zabbix.ZabbixTemplate>[]? Template ", File("WidgetCommands.g.cs"), StringComparison.Ordinal);
    }

    [Fact]
    public void A_field_required_under_a_condition_is_optional_and_its_help_says_when()
    {
        string widget = File("WidgetCommands.g.cs");

        Assert.Contains("Required when Closing is Allowed.", widget, StringComparison.Ordinal);
        Assert.Contains("'name' (-Name) is required", widget, StringComparison.Ordinal);
        Assert.DoesNotContain("'url' (-Url) is required", widget, StringComparison.Ordinal);
    }

    [Fact]
    public void A_skipped_object_and_a_hand_written_get_are_left_out_without_a_diagnostic()
    {
        Assert.DoesNotContain(s_emitted.Files, file => file.Name == "GadgetCommands.g.cs");
        Assert.DoesNotContain(s_emitted.Files, file => file.Code.Contains("class GetHostGroupCommand", StringComparison.Ordinal));
        Assert.Contains("class GetTemplateCommand", File("TemplateCommands.g.cs"), StringComparison.Ordinal);
        Assert.DoesNotContain(s_emitted.Problems, problem => problem.Record is "gadget" or "hostgroup");
    }

    [Fact]
    public void An_enum_field_and_a_get_option_the_description_names_are_switches()
    {
        string widget = File("WidgetCommands.g.cs");

        Assert.Contains("SwitchParameter Closing { get; set; }", widget, StringComparison.Ordinal);
        Assert.Contains("Closing.IsPresent ? global::Community.Zabbix.ZabbixWidgetClosing.Allowed : global::Community.Zabbix.ZabbixWidgetClosing.NotAllowed", widget, StringComparison.Ordinal);
        Assert.Contains("SwitchParameter Editable { get; set; }", widget, StringComparison.Ordinal);
        Assert.DoesNotContain("Editable", File("TemplateCommands.g.cs"), StringComparison.Ordinal);
    }

    [Fact]
    public void A_get_list_the_description_names_is_an_enum_array_where_the_get_carries_it()
    {
        string widget = File("WidgetCommands.g.cs");

        Assert.Contains("public global::Community.Zabbix.ZabbixWidgetSize[]? Size { get; set; }", widget, StringComparison.Ordinal);
        Assert.Contains("if (Size is { Length: > 0 }) parameters = parameters with { Sizes = Size };", widget, StringComparison.Ordinal);
        Assert.Contains("Only widgets of these sizes. Wire field sizes.", widget, StringComparison.Ordinal);
        Assert.DoesNotContain(" Size ", File("TemplateCommands.g.cs"), StringComparison.Ordinal);
    }

    [Fact]
    public void A_builder_is_emitted_from_its_record_and_one_whose_record_is_not_described_is_reported()
    {
        string builders = File("ZabbixNestedBuilders.g.cs");

        Assert.Contains("class NewWidgetGroupCommand", builders, StringComparison.Ordinal);
        Assert.Contains("Position = 0", builders, StringComparison.Ordinal);
        Assert.DoesNotContain("NewMissingCommand", builders, StringComparison.Ordinal);
        EmitProblem missing = Assert.Single(s_emitted.Problems);
        Assert.Equal((ZabbixDiagnostics.NotGenerated, "Missing"), (missing.Descriptor, missing.Record));
    }

    private const string NamesDescription = """
        {
         "versions": ["7.0", "8.0"],
         "enums": [],
         "types": [
          {"name": "Server", "role": "read", "object": "server", "fields": [{"name": "ServerId", "wire": "serverid", "kind": "id"}, {"name": "Name", "wire": "name", "kind": "string"}]},
          {"name": "ServerGetParameters", "role": "get", "object": "server", "method": "server.get", "getParameters": true, "fields": [{"name": "ServerIds", "wire": "serverids", "kind": "list:id"}]},
          {"name": "ServerCreateRequest", "role": "request", "object": "server", "method": "server.create", "fields": [
            {"name": "Host", "wire": "host", "kind": "string", "required": true}, {"name": "Name", "wire": "name", "kind": "string"}]},
          {"name": "Probe", "role": "read", "object": "probe", "fields": [{"name": "ProbeId", "wire": "probeid", "kind": "id"}, {"name": "Name", "wire": "name", "kind": "string"}]},
          {"name": "ProbeGetParameters", "role": "get", "object": "probe", "method": "probe.get", "getParameters": true, "fields": [{"name": "ProbeIds", "wire": "probeids", "kind": "list:id"}]},
          {"name": "ProbeCreateRequest", "role": "request", "object": "probe", "method": "probe.create", "fields": [
            {"name": "Name", "wire": "name", "kind": "string", "required": true}, {"name": "Host", "wire": "host", "kind": "string"}]}
         ],
         "services": [
          {"object": "server", "client": "ZabbixServerClient", "property": "Servers", "model": "Server", "methods": [
            {"api": "server.get", "name": "GetAsync", "shape": "get-list", "parameters": "object:ServerGetParameters", "result": "list:object:Server"},
            {"api": "server.create", "name": "CreateAsync", "shape": "call", "parameters": "list:object:ServerCreateRequest", "result": "ids:serverids"}]},
          {"object": "probe", "client": "ZabbixProbeClient", "property": "Probes", "model": "Probe", "methods": [
            {"api": "probe.get", "name": "GetAsync", "shape": "get-list", "parameters": "object:ProbeGetParameters", "result": "list:object:Probe"},
            {"api": "probe.create", "name": "CreateAsync", "shape": "call", "parameters": "list:object:ProbeCreateRequest", "result": "ids:probeids"}]}
         ],
         "cmdlets": {
          "objects": {
           "server": {"names": {"display": "name", "technical": "host"}, "unhide": {"host": "HostName"}}
          }
         }
        }
        """;

    [Fact]
    public void The_name_fields_and_the_unhidden_property_come_from_the_description_and_the_rule_names_the_rest()
    {
        EmitResult emitted = new ZabbixCmdletEmitter(ApiDescription.Read(NamesDescription)).Emit(TestContext.Current.CancellationToken);
        string server = emitted.Files.Single(file => file.Name == "ServerCommands.g.cs").Code;
        string probe = emitted.Files.Single(file => file.Name == "ProbeCommands.g.cs").Code;

        Assert.Contains("[global::System.Management.Automation.Alias(\"Host\")]\n    public string? Name { get; set; }", server, StringComparison.Ordinal);
        Assert.Contains("public string? DisplayName { get; set; }", server, StringComparison.Ordinal);
        Assert.Contains("The server requires -Name;", server, StringComparison.Ordinal);
        Assert.Contains("[global::System.Management.Automation.Alias(\"Host\")]\n    public string? HostValue { get; set; }", probe, StringComparison.Ordinal);
        Assert.Contains("public string? Name { get; set; }", probe, StringComparison.Ordinal);
        Assert.DoesNotContain("DisplayName", probe, StringComparison.Ordinal);
        Assert.Empty(emitted.Problems);
    }
}
