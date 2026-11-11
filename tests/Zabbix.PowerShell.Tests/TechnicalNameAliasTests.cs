// This Source Code Form is subject to the terms of the Mozilla Public
// License, v. 2.0. Copyright (c) 2026 schroeder247. SPDX-License-Identifier: MPL-2.0
using System.Text.Json;

namespace Community.Zabbix.PowerShell.Tests;

/// <summary>
/// The technical name and the display name: <c>host.create</c>/<c>template.create</c>/<c>hostprototype.create</c> carry
/// both a "host" wire field (the technical, unique name; <c>HostName</c> on every record) and a "name" wire field
/// (the visible name; <c>VisibleName</c>). The cmdlets take the technical name as <c>-Name</c> (alias <c>-Host</c>), the
/// name <c>Get-ZbxHost -Name</c> selects by, and the visible name as <c>-DisplayName</c>, on every object with both
/// (<c>cmdlets.objects.&lt;object&gt;.names</c>).
/// </summary>
public sealed class TechnicalNameAliasTests
{
    [Fact]
    public async Task New_ZbxHost_Name_is_the_shipped_alias_of_the_technical_Host_field()
    {
        // The usual fixture line: -Name is the technical host name, never the display name.
        await using FakeZabbix server = await FakeZabbix.StartAsync();
        server.On("host.create", """{"hostids":["10084"]}""");
        server.On("host.get", FakeZabbix.Hosts(("10084", "web-01")));
        using Shell shell = Shell.Open();
        shell.Connect(server);

        Run run = shell.Invoke("New-ZbxHost -Name web-01 -Group @('7') -InterfaceType Agent -InterfaceIP '127.0.0.1' -InterfacePort '10050' -Confirm:$false");

        Assert.Empty(run.Errors);
        JsonElement request = Assert.Single(server.ParamsOf("host.create"));
        Assert.Equal("web-01", request.GetProperty("host").GetString());
        Assert.False(request.TryGetProperty("name", out _));
    }

    [Fact]
    public async Task New_ZbxHost_DisplayName_still_sets_the_display_name_not_the_technical_name()
    {
        await using FakeZabbix server = await FakeZabbix.StartAsync();
        server.On("host.create", """{"hostids":["10084"]}""");
        server.On("host.get", FakeZabbix.Hosts(("10084", "web-01")));
        using Shell shell = Shell.Open();
        shell.Connect(server);

        Run run = shell.Invoke("New-ZbxHost -Host web-01 -Group @('7') -DisplayName 'Web One' -Confirm:$false");

        Assert.Empty(run.Errors);
        JsonElement request = Assert.Single(server.ParamsOf("host.create"));
        Assert.Equal("web-01", request.GetProperty("host").GetString());
        Assert.Equal("Web One", request.GetProperty("name").GetString());
    }

    [Fact]
    public async Task New_ZbxTemplate_Name_is_the_shipped_alias_of_the_technical_Host_field()
    {
        await using FakeZabbix server = await FakeZabbix.StartAsync();
        server.On("template.create", """{"templateids":["10250"]}""");
        server.On("template.get", """[{"templateid":"10250","host":"tmpl-01"}]""");
        using Shell shell = Shell.Open();
        shell.Connect(server);

        Run run = shell.Invoke("New-ZbxTemplate -Name tmpl-01 -Group @('1') -Confirm:$false");

        Assert.Empty(run.Errors);
        JsonElement request = Assert.Single(server.ParamsOf("template.create"));
        Assert.Equal("tmpl-01", request.GetProperty("host").GetString());
    }

    [Fact]
    public async Task Get_ZbxHost_Group_resolves_the_hand_written_cmdlet_s_own_reference_filter()
    {
        // Get-ZbxHost is hand-written (ZabbixCmdletEmitter.EmitGet skips "host"), so the generated reference-filter
        // parameters never reach it; it wires the same HasReferenceFilters/ApplyReferenceFiltersAsync hook by hand.
        await using FakeZabbix server = await FakeZabbix.StartAsync();
        server.On("host.get", FakeZabbix.Hosts(("10084", "web-01")));
        using Shell shell = Shell.Open();
        shell.Connect(server);

        Run run = shell.Invoke("Get-ZbxHost -Group @('7')");

        Assert.Empty(run.Errors);
        JsonElement request = Assert.Single(server.ParamsOf("host.get"));
        Assert.Equal("""["7"]""", request.GetProperty("groupids").GetRawText());
    }

    [Fact]
    public async Task Get_ZbxHostGroup_piped_into_Get_ZbxHost_binds_Group_by_its_own_GroupId_property()
    {
        // Get-ZbxHost's own -Group takes ValueFromPipelineByPropertyName, so the idiom Get-ZbxHostGroup | Get-ZbxHost
        // binds the piped ZabbixHostGroup's own GroupId property by name, not value.
        await using FakeZabbix server = await FakeZabbix.StartAsync();
        server.On("hostgroup.get", """[{"groupid":"7","name":"Web servers"}]""");
        server.On("host.get", FakeZabbix.Hosts(("10084", "web-01")));
        using Shell shell = Shell.Open();
        shell.Connect(server);

        Run run = shell.Invoke("Get-ZbxHostGroup | Get-ZbxHost");

        Assert.Empty(run.Errors);
        JsonElement request = Assert.Single(server.ParamsOf("host.get"));
        Assert.Equal("""["7"]""", request.GetProperty("groupids").GetRawText());
    }

    [Fact]
    public async Task Get_ZbxHost_Group_resolves_a_name_in_one_hostgroup_get()
    {
        await using FakeZabbix server = await FakeZabbix.StartAsync();
        server.On("hostgroup.get", """[{"groupid":"7","name":"Web servers"}]""");
        server.On("host.get", FakeZabbix.Hosts(("10084", "web-01")));
        using Shell shell = Shell.Open();
        shell.Connect(server);

        Run run = shell.Invoke("Get-ZbxHost -Group 'Web servers'");

        Assert.Empty(run.Errors);
        Assert.Equal(1, server.Calls.Count(c => c.Method == "hostgroup.get"));
        JsonElement request = Assert.Single(server.ParamsOf("host.get"));
        Assert.Equal("""["7"]""", request.GetProperty("groupids").GetRawText());
    }

    [Fact]
    public async Task Get_ZbxHost_with_no_Group_bound_makes_no_hostgroup_get_call()
    {
        await using FakeZabbix server = await FakeZabbix.StartAsync();
        server.On("host.get", FakeZabbix.Hosts(("10084", "web-01")));
        using Shell shell = Shell.Open();
        shell.Connect(server);

        Run run = shell.Invoke("Get-ZbxHost");

        Assert.Empty(run.Errors);
        Assert.DoesNotContain(server.Calls, c => c.Method == "hostgroup.get");
    }

    [Fact]
    public void New_ZbxHostPrototype_names_its_two_names_as_a_host_does()
    {
        using Shell shell = Shell.Open();

        Run run = shell.Invoke("$p = (Get-Command New-ZbxHostPrototype).Parameters; '{0} {1} {2} {3}' -f $p.ContainsKey('Name'), $p.ContainsKey('DisplayName'), $p.ContainsKey('HostName'), ($p['Name'].Aliases -join ',')");

        Assert.Empty(run.Errors);
        Assert.Equal("True True False Host", Assert.Single(run.Output).ToString());
    }
}
