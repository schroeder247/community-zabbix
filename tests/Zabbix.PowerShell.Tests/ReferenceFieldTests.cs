// This Source Code Form is subject to the terms of the Mozilla Public
// License, v. 2.0. Copyright (c) 2026 schroeder247. SPDX-License-Identifier: MPL-2.0
using System.Text.Json;

namespace Community.Zabbix.PowerShell.Tests;

/// <summary>
/// A request field that names other objects (<c>-Template</c>, <c>-Group</c>) is sent whole or not at all (on
/// 7.0.31 and 8.0.0): the field REPLACES the object's list on the server, so a name that
/// resolves to nothing must stop the change, never send the part that resolved.
/// </summary>
public sealed class ReferenceFieldTests
{
    [Fact]
    public async Task Set_with_a_template_name_that_matches_nothing_changes_nothing()
    {
        // ZabbixReferenceNotFound is written and no host.update follows: templates [] would unlink every template.
        await using FakeZabbix server = await FakeZabbix.StartAsync();
        server.On("host.get", FakeZabbix.Hosts(("10084", "web-01"))).On("template.get", "[]").On("host.update", """{"hostids":["10084"]}""");
        using Shell shell = Shell.Open();
        shell.Connect(server);

        Run run = shell.Invoke("Set-ZbxHost web-01 -Template 'No such template' -Confirm:$false");

        Assert.StartsWith("ZabbixReferenceNotFound,", Assert.Single(run.Errors).FullyQualifiedErrorId, StringComparison.Ordinal);
        Assert.Contains("-Template", run.Errors[0].Exception.Message, StringComparison.Ordinal);
        Assert.Empty(server.ParamsOf("host.update"));
    }

    [Fact]
    public async Task Set_with_one_group_of_two_unresolved_changes_nothing()
    {
        // -Group 'Linux servers','No such group' must not silently set the host's groups to the one that resolved.
        await using FakeZabbix server = await FakeZabbix.StartAsync();
        server.On("host.get", FakeZabbix.Hosts(("10084", "web-01"))).On("hostgroup.get", """[{"groupid":"2","name":"Linux servers"}]""")
              .On("host.update", """{"hostids":["10084"]}""");
        using Shell shell = Shell.Open();
        shell.Connect(server);

        Run run = shell.Invoke("Set-ZbxHost web-01 -Group 'Linux servers','No such group' -Confirm:$false");

        Assert.Single(run.Errors);
        Assert.Equal(System.Management.Automation.ErrorCategory.ObjectNotFound, run.Errors[0].CategoryInfo.Category);
        Assert.Empty(server.ParamsOf("host.update"));
    }

    [Fact]
    public async Task New_with_a_group_name_that_matches_nothing_creates_nothing()
    {
        await using FakeZabbix server = await FakeZabbix.StartAsync();
        server.On("hostgroup.get", "[]").On("host.create", """{"hostids":["10090"]}""");
        using Shell shell = Shell.Open();
        shell.Connect(server);

        Run run = shell.Invoke("New-ZbxHost zbx-web -Group 'No such group' -Confirm:$false");

        Assert.StartsWith("ZabbixReferenceNotFound,", Assert.Single(run.Errors).FullyQualifiedErrorId, StringComparison.Ordinal);
        Assert.Empty(server.ParamsOf("host.create"));
    }

    [Fact]
    public async Task One_unresolved_record_of_a_pipeline_stops_only_that_records_change()
    {
        // Per record: the record whose -Group resolves goes ahead, the other one does not.
        await using FakeZabbix server = await FakeZabbix.StartAsync();
        server.On("hostgroup.get", parameters => parameters.GetRawText().Contains("Linux servers", StringComparison.Ordinal) ? """[{"groupid":"2","name":"Linux servers"}]""" : "[]")
              .On("host.update", """{"hostids":["1"]}""");
        using Shell shell = Shell.Open();
        shell.Connect(server);

        Run run = shell.Invoke("""
            [pscustomobject]@{ HostId = '1'; Group = 'Linux servers' }, [pscustomobject]@{ HostId = '2'; Group = 'No such group' } |
                Set-ZbxHost -Confirm:$false
            """);

        Assert.Single(run.Errors);
        JsonElement update = Assert.Single(server.ParamsOf("host.update"));
        JsonElement request = Assert.Single(update.EnumerateArray());
        Assert.Equal("1", request.GetProperty("hostid").GetString());
        Assert.Equal("2", request.GetProperty("groups").EnumerateArray().Single().GetProperty("groupid").GetString());
    }

    [Fact]
    public async Task Remove_ZbxUserMacro_resolves_a_macro_by_the_macro_itself()
    {
        // Resolution matches the macro, not the description (which would answer "no usermacro is named '{$APP.PORT}'").
        await using FakeZabbix server = await FakeZabbix.StartAsync();
        server.On("usermacro.get", """[{"hostmacroid":"6","macro":"{$APP.PORT}","description":"the port"}]""")
              .On("usermacro.delete", """{"hostmacroids":["6"]}""");
        using Shell shell = Shell.Open();
        shell.Connect(server);

        Run run = shell.Invoke("Remove-ZbxUserMacro '{$APP.PORT}' -Confirm:$false");

        Assert.Empty(run.Errors);
        JsonElement lookup = Assert.Single(server.ParamsOf("usermacro.get"));
        Assert.Equal("{$APP.PORT}", lookup.GetProperty("search").GetProperty("macro").EnumerateArray().Single().GetString());
        Assert.Equal("""["6"]""", Assert.Single(server.ParamsOf("usermacro.delete")).GetRawText());
    }

    [Fact]
    public async Task An_empty_list_given_is_sent_as_an_empty_list()
    {
        // Set-ZbxProxy -Host @() sends the empty list (it clears the proxy's hosts); it is not dropped as if unbound.
        await using FakeZabbix server = await FakeZabbix.StartAsync();
        server.On("proxy.update", """{"proxyids":["5"]}""");
        using Shell shell = Shell.Open();
        shell.Connect(server);

        Run run = shell.Invoke("Set-ZbxProxy 5 -Host @() -Confirm:$false");

        Assert.Empty(run.Errors);
        JsonElement request = Assert.Single(Assert.Single(server.ParamsOf("proxy.update")).EnumerateArray());
        Assert.Equal("[]", request.GetProperty("hosts").GetRawText());
    }
}
