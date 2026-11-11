// This Source Code Form is subject to the terms of the Mozilla Public
// License, v. 2.0. Copyright (c) 2026 schroeder247. SPDX-License-Identifier: MPL-2.0
using System.Collections.Concurrent;
using System.Management.Automation;
using System.Text.Json;

namespace Community.Zabbix.PowerShell.Tests;

/// <summary>
/// <c>Show-ZbxPage</c>: the piped object's kind picks the page, the connected server's version picks the route,
/// <c>-PassThru</c> writes the address, and the browser hand-off goes through the context's seam (no test opens one).
/// </summary>
public sealed class FrontendPageCommandsTests
{
    private static async Task<(FakeZabbix Server, Shell Shell, ConcurrentQueue<Uri> Opened)> ConnectAsync(string version = "7.0.31", string? refuse = null)
    {
        FakeZabbix server = await FakeZabbix.StartAsync();
        server.On("apiinfo.version", $"\"{version}\"");
        Shell shell = Shell.Open();
        var opened = new ConcurrentQueue<Uri>();
        shell.Context.OpenPage = page =>
        {
            opened.Enqueue(page);
            return refuse;
        };
        shell.Connect(server);
        return (server, shell, opened);
    }

    [Theory]
    [InlineData("7.0.31", "zabbix.php?action=host.edit&hostid=10084")]
    [InlineData("7.4.15", "zabbix.php?action=popup&popup=host.edit&hostid=10084")]
    [InlineData("8.0.0", "zabbix.php?action=popup&popup=host.edit&hostid=10084")]
    public async Task A_piped_host_with_PassThru_writes_its_versions_page_and_opens_nothing(string version, string page)
    {
        (FakeZabbix server, Shell shell, ConcurrentQueue<Uri> opened) = await ConnectAsync(version);
        await using FakeZabbix _ = server;
        using Shell __ = shell;
        server.On("host.get", FakeZabbix.Hosts(("10084", "web-01")));

        Run run = shell.Invoke("Get-ZbxHost | Show-ZbxPage -PassThru");

        Assert.Empty(run.Errors);
        Uri written = Assert.IsType<Uri>(Assert.Single(run.Output).BaseObject);
        Assert.Equal(new Uri(server.BaseAddress, page), written);
        Assert.Empty(opened);
    }

    [Fact]
    public async Task Without_PassThru_the_page_goes_to_the_browser_and_nothing_is_written()
    {
        (FakeZabbix server, Shell shell, ConcurrentQueue<Uri> opened) = await ConnectAsync("7.4.15");
        await using FakeZabbix _ = server;
        using Shell __ = shell;
        server.On("host.get", FakeZabbix.Hosts(("10084", "web-01")));

        Run run = shell.Invoke("Get-ZbxHost | Show-ZbxPage -View Latest");

        Assert.Empty(run.Errors);
        Assert.Empty(run.Output);
        Assert.Equal([new Uri(server.BaseAddress, "zabbix.php?action=latest.view&hostids%5B%5D=10084&filter_set=1")], opened);
    }

    [Fact]
    public async Task Where_no_browser_opens_it_warns_and_writes_the_address()
    {
        (FakeZabbix server, Shell shell, ConcurrentQueue<Uri> _) = await ConnectAsync("8.0.0", refuse: "no display");
        await using FakeZabbix __ = server;
        using Shell ___ = shell;
        server.On("dashboard.get", """[{"dashboardid":"1","name":"Global view"}]""");

        Run run = shell.Invoke("Get-ZbxDashboard | Show-ZbxPage");

        Assert.Empty(run.Errors);
        Assert.Equal(new Uri(server.BaseAddress, "zabbix.php?action=dashboard.view&dashboardid=1"), Assert.Single(run.Output).BaseObject);
        Assert.Contains("no display", Assert.Single(run.Warnings), StringComparison.Ordinal);
    }

    [Fact]
    public async Task An_item_read_without_its_host_is_read_again_so_a_templates_item_opens_in_the_template_context()
    {
        (FakeZabbix server, Shell shell, ConcurrentQueue<Uri> _) = await ConnectAsync("7.4.15");
        await using FakeZabbix __ = server;
        using Shell ___ = shell;
        server.On("item.get", parameters => parameters.TryGetProperty("selectHosts", out _)
            ? """[{"itemid":"42240","hostid":"10001","name":"CPU load","hosts":[{"hostid":"10001","status":"3"}]}]"""
            : """[{"itemid":"42240","hostid":"10001","name":"CPU load","key_":"system.cpu.load"}]""");

        Run run = shell.Invoke("Get-ZbxItem | Show-ZbxPage -PassThru");

        Assert.Empty(run.Errors);
        Assert.Equal(new Uri(server.BaseAddress, "zabbix.php?action=popup&popup=item.edit&context=template&itemid=42240"), Assert.Single(run.Output).BaseObject);
        JsonElement reread = server.ParamsOf("item.get").Last();
        Assert.Equal("42240", reread.GetProperty("itemids")[0].GetString());
    }

    [Fact]
    public async Task Kind_and_Id_read_the_object_by_id_then_link_it()
    {
        (FakeZabbix server, Shell shell, ConcurrentQueue<Uri> _) = await ConnectAsync("7.0.31");
        await using FakeZabbix __ = server;
        using Shell ___ = shell;
        server.On("template.get", """[{"templateid":"10001","host":"Linux by Zabbix agent","name":"Linux by Zabbix agent"}]""");
        server.On("action.get", """[{"actionid":"3","name":"Report problems","eventsource":"0"}]""");

        Run run = shell.Invoke("Show-ZbxPage -Kind Template -Id 10001 -PassThru; '3' | Show-ZbxPage -Kind Action -PassThru");

        Assert.Empty(run.Errors);
        Assert.Equal(
            [
                new Uri(server.BaseAddress, "zabbix.php?action=template.list&filter_set=1&filter_name=Linux%20by%20Zabbix%20agent"),
                new Uri(server.BaseAddress, "zabbix.php?action=action.list&eventsource=0&filter_set=1&filter_name=Report%20problems"),
            ],
            run.Output.Select(output => (Uri)output.BaseObject));
        Assert.Equal("10001", server.ParamsOf("template.get").Single().GetProperty("templateids")[0].GetString());
    }

    [Fact]
    public async Task An_id_that_names_nothing_is_an_object_not_found_error()
    {
        (FakeZabbix server, Shell shell, ConcurrentQueue<Uri> _) = await ConnectAsync();
        await using FakeZabbix __ = server;
        using Shell ___ = shell;
        server.On("host.get", "[]");

        Run run = shell.Invoke("Show-ZbxPage -Kind Host -Id 99 -PassThru");

        Assert.Empty(run.Output);
        ErrorRecord error = Assert.Single(run.Errors);
        Assert.StartsWith("ZabbixPageNotFound", error.FullyQualifiedErrorId, StringComparison.Ordinal);
        Assert.Equal(ErrorCategory.ObjectNotFound, error.CategoryInfo.Category);
    }

    [Fact]
    public async Task A_view_the_kind_has_not_and_an_object_with_no_page_are_errors_the_rest_still_runs()
    {
        (FakeZabbix server, Shell shell, ConcurrentQueue<Uri> _) = await ConnectAsync("7.4.15");
        await using FakeZabbix __ = server;
        using Shell ___ = shell;
        server.On("dashboard.get", """[{"dashboardid":"1","name":"Global view"}]""");
        server.On("host.get", FakeZabbix.Hosts(("10084", "web-01")));

        Run refused = shell.Invoke("Get-ZbxDashboard | Show-ZbxPage -View Edit -PassThru");
        Run unknown = shell.Invoke("@('not a record', (Get-ZbxHost)) | Show-ZbxPage -PassThru");

        Assert.Empty(refused.Output);
        Assert.StartsWith("ZabbixPageRefused", Assert.Single(refused.Errors).FullyQualifiedErrorId, StringComparison.Ordinal);
        Assert.StartsWith("ZabbixPageUnknown", Assert.Single(unknown.Errors).FullyQualifiedErrorId, StringComparison.Ordinal);
        Assert.Equal(new Uri(server.BaseAddress, "zabbix.php?action=popup&popup=host.edit&hostid=10084"), Assert.Single(unknown.Output).BaseObject);
    }

    [Fact]
    public async Task Connect_ZbxServer_FrontendUri_moves_the_pages_and_refuses_a_base_that_is_not_http()
    {
        await using FakeZabbix server = await FakeZabbix.StartAsync();
        server.On("host.get", FakeZabbix.Hosts(("10084", "web-01")));
        using Shell shell = Shell.Open();
        Run connected = shell.Connect(server, "-FrontendUri 'https://ui.zbx.example/monitoring'");

        Run run = shell.Invoke("Get-ZbxHost | Show-ZbxPage -PassThru");
        Run refused = shell.Invoke($"Connect-ZbxServer -Uri '{server.Endpoint}' -Name other -FrontendUri 'ftp://ui.zbx.example/'");

        Assert.Empty(connected.Errors);
        Assert.Equal(new Uri("https://ui.zbx.example/monitoring/zabbix.php?action=host.edit&hostid=10084"), Assert.Single(run.Output).BaseObject);
        Assert.Contains("FrontendUri", Assert.Single(refused.Errors).Exception.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task With_Kind_the_input_is_ids_and_a_name_is_refused()
    {
        (FakeZabbix server, Shell shell, ConcurrentQueue<Uri> _) = await ConnectAsync("8.0.0");
        await using FakeZabbix __ = server;
        using Shell ___ = shell;
        server.On("map.get", """[{"sysmapid":"3","name":"Local network"}]""");

        Run run = shell.Invoke("Show-ZbxPage -Kind Map -Id 3 -PassThru; 'Local network' | Show-ZbxPage -Kind Map -PassThru");

        Assert.Equal(new Uri(server.BaseAddress, "zabbix.php?action=map.view&sysmapid=3"), Assert.Single(run.Output).BaseObject);
        Assert.StartsWith("ZabbixPageNotAnId", Assert.Single(run.Errors).FullyQualifiedErrorId, StringComparison.Ordinal);
    }

    [Fact]
    public void The_command_declares_a_Uri_and_one_parameter_set_the_pipeline_binds()
    {
        CmdletInfo info = new("Show-ZbxPage", typeof(Commands.ShowZbxPageCommand));

        Assert.Equal(typeof(Uri), Assert.Single(info.OutputType).Type);
        CommandParameterInfo input = Assert.Single(Assert.Single(info.ParameterSets).Parameters, parameter => parameter.Name == "InputObject");
        Assert.True(input.ValueFromPipeline);
        Assert.Contains("Id", input.Aliases);
    }
}
