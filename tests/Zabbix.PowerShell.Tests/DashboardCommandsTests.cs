// This Source Code Form is subject to the terms of the Mozilla Public
// License, v. 2.0. Copyright (c) 2026 schroeder247. SPDX-License-Identifier: MPL-2.0
using System.Text.Json;
using Community.Zabbix.PowerShell.Commands;

namespace Community.Zabbix.PowerShell.Tests;

/// <summary>The dashboard and watch commands: <c>Publish-ZbxDashboard</c> and <c>Show-ZbxWatch</c>.</summary>
public sealed class DashboardCommandsTests
{
    private const string DashboardDocument = """{"name":"ops","pages":[{"widgets":[{"type":"graph","name":"cpu","x":0,"y":0,"width":6,"height":4}]}]}""";

    [Fact]
    public async Task Publish_ZbxDashboard_creates_when_no_dashboard_of_that_name_exists()
    {
        await using FakeZabbix server = await FakeZabbix.StartAsync();
        server.On("dashboard.get", "[]");
        server.On("dashboard.create", """{"dashboardids":["1"]}""");
        using Shell shell = Shell.Open();
        shell.Connect(server);

        Run run = shell.Invoke($$"""'{{DashboardDocument}}' | Publish-ZbxDashboard -Confirm:$false""");

        Assert.Empty(run.Errors);
        JsonElement created = Assert.Single(server.ParamsOf("dashboard.create"));
        Assert.Equal("ops", created.GetProperty("name").GetString());
        Assert.Empty(server.ParamsOf("dashboard.update"));
    }

    [Fact]
    public async Task Publish_ZbxDashboard_updates_a_same_named_dashboard_in_place()
    {
        await using FakeZabbix server = await FakeZabbix.StartAsync();
        server.On("dashboard.get", """[{"dashboardid":"9","name":"ops"}]""");
        server.On("dashboard.update", """{"dashboardids":["9"]}""");
        using Shell shell = Shell.Open();
        shell.Connect(server);

        Run run = shell.Invoke($$"""'{{DashboardDocument}}' | Publish-ZbxDashboard -Confirm:$false""");

        Assert.Empty(run.Errors);
        JsonElement updated = Assert.Single(server.ParamsOf("dashboard.update"));
        Assert.Equal("9", updated.GetProperty("dashboardid").GetString());
        Assert.Empty(server.ParamsOf("dashboard.create"));
    }

    [Fact]
    public async Task Publish_ZbxDashboard_resolves_the_graph_and_item_widget_shorthands_into_widget_fields()
    {
        // The template form names a graph (graph = '<name>', or @{ host; name }) or an item
        // (item = @{ host; key }); each is resolved to its id and becomes the widget's own field — graphid.0 of
        // ZBX_WIDGET_FIELD_TYPE_GRAPH (6), itemid.0 of ZBX_WIDGET_FIELD_TYPE_ITEM (4), defines.inc.php 7.0-8.0.
        await using FakeZabbix server = await FakeZabbix.StartAsync();
        server.On("dashboard.get", "[]");
        server.On("dashboard.create", """{"dashboardids":["1"]}""");
        server.On("graph.get", """[{"graphid":"42","name":"CPU utilization"}]""");
        server.On("host.get", FakeZabbix.Hosts(("10084", "web-01")));
        server.On("item.get", """[{"itemid":"501","key_":"system.cpu.load","hostid":"10084"}]""");
        using Shell shell = Shell.Open();
        shell.Connect(server);

        Run run = shell.Invoke("""
            @{ name = 'ops'; pages = @(@{ widgets = @(
                @{ type = 'graph'; x = 0; y = 0; width = 36; height = 5; graph = 'CPU utilization' }
                @{ type = 'item'; x = 36; y = 0; width = 12; height = 2; item = @{ host = 'web-01'; key = 'system.cpu.load' } }
                @{ type = 'clock'; x = 48; y = 0; width = 6; height = 2 }
            ) }) } | Publish-ZbxDashboard -Confirm:$false
            """);

        Assert.Empty(run.Errors);
        JsonElement[] widgets = [.. Assert.Single(server.ParamsOf("dashboard.create")).GetProperty("pages")[0].GetProperty("widgets").EnumerateArray()];
        Assert.Equal(3, widgets.Length);
        Assert.Equal("""[{"type":6,"name":"graphid.0","value":"42"}]""", widgets[0].GetProperty("fields").GetRawText());
        Assert.False(widgets[0].TryGetProperty("graph", out _));
        Assert.Equal("""[{"type":4,"name":"itemid.0","value":"501"}]""", widgets[1].GetProperty("fields").GetRawText());
        Assert.False(widgets[2].TryGetProperty("fields", out _));
        Assert.Equal("CPU utilization", Assert.Single(server.ParamsOf("graph.get")).GetProperty("filter").GetProperty("name").GetString());
        JsonElement itemQuery = Assert.Single(server.ParamsOf("item.get"));
        Assert.Equal("system.cpu.load", itemQuery.GetProperty("filter").GetProperty("key_").GetString());
    }

    [Fact]
    public async Task Publish_ZbxDashboard_refuses_a_graph_shorthand_naming_no_graph_and_creates_nothing()
    {
        await using FakeZabbix server = await FakeZabbix.StartAsync();
        server.On("dashboard.get", "[]");
        server.On("dashboard.create", """{"dashboardids":["1"]}""");
        server.On("graph.get", "[]");
        using Shell shell = Shell.Open();
        shell.Connect(server);

        Run run = shell.Invoke("""
            @{ name = 'ops'; pages = @(@{ widgets = @(@{ type = 'graph'; graph = 'nope' }) }) } | Publish-ZbxDashboard -Confirm:$false
            """);

        Assert.Contains(run.Errors, error => error.Exception.Message.Contains("nope", StringComparison.Ordinal));
        Assert.Empty(server.ParamsOf("dashboard.create"));
    }

    [Fact]
    public async Task Show_ZbxWatch_renders_the_watchs_current_snapshot_once_by_default()
    {
        await using FakeZabbix server = await FakeZabbix.StartAsync();
        server.On("item.get", "[]");
        using Shell shell = Shell.Open();
        shell.Connect(server);
        shell.Invoke("Watch-ZbxItem probe web-01");

        Run run = shell.Invoke("Show-ZbxWatch probe");

        Assert.Empty(run.Errors);
        string rendered = Assert.IsType<string>(Assert.Single(run.Output).BaseObject);
        Assert.Contains("probe", rendered, StringComparison.Ordinal);
        Assert.Contains("Host", rendered, StringComparison.Ordinal);
    }

    [Fact]
    public void Show_ZbxWatch_reports_an_unknown_watch()
    {
        using Shell shell = Shell.Open();

        Run run = shell.Invoke("Show-ZbxWatch ghost");

        var error = Assert.Single(run.Errors);
        Assert.Equal("ZabbixWatchNotFound", error.FullyQualifiedErrorId.Split(',')[0]);
    }

    [Fact]
    public void ZabbixAnsiRendering_Table_pads_columns_to_the_widest_cell()
    {
        string table = ZabbixAnsiRendering.Table(["A", "BB"], [["x", "y"]]);
        Assert.Contains("A ", table, StringComparison.Ordinal);
        Assert.Contains("x ", table, StringComparison.Ordinal);
    }
}
