// This Source Code Form is subject to the terms of the Mozilla Public
// License, v. 2.0. Copyright (c) 2026 schroeder247. SPDX-License-Identifier: MPL-2.0
namespace Community.Zabbix.PowerShell.Tests;

/// <summary>
/// A change confirms once per resolved object, named by it (<c>host 'x' (1)</c>), with an action that names the kind of
/// change (<c>Remove host</c>, never a bare <c>Remove</c> or <c>3 user(s)</c>): what <c>-WhatIf</c> lists and
/// <c>-Confirm</c> asks. Read from the verbose line <c>ShouldProcess</c> writes when it proceeds.
/// </summary>
public sealed class ConfirmationTargetTests
{
    // One object named 'x' with id 1 for every entity's .get, and every id list a change answers.
    private static string Answer(string method, System.Text.Json.JsonElement _) => method.EndsWith(".get", StringComparison.Ordinal)
        ? """[{"hostid":"1","host":"x","name":"x","userid":"1","username":"x","proxyid":"1","itemid":"1","groupid":"1","scriptid":"1","templateid":"1","status":"0","tags":[]}]"""
        : """{"hostids":["1"],"eventids":["7"],"userids":["1"],"groupids":["1"],"taskids":["9"],"proxyids":["1"],"itemids":["1"]}""";

    [Theory]
    [InlineData("Remove-ZbxHost x", "Remove host", "host 'x' (1)")]
    [InlineData("Remove-ZbxHost 1", "Remove host", "host 1")]
    [InlineData("Set-ZbxHost x -Description d", "Set host", "host 'x' (1)")]
    [InlineData("Disable-ZbxHost x", "Disable host", "host 'x' (1)")]
    [InlineData("Unblock-ZbxUser x", "Unblock user", "user 'x' (1)")]
    [InlineData("Reset-ZbxUserTotp x", "Reset TOTP of user", "user 'x' (1)")]
    [InlineData("Invoke-ZbxUserProvision x", "Provision user", "user 'x' (1)")]
    [InlineData("Sync-ZbxProxyConfig x", "Sync configuration of proxy", "proxy 'x' (1)")]
    [InlineData("Invoke-ZbxCheckNow 1", "Check item now", "item 1")]
    [InlineData("Invoke-ZbxHostGroupPropagation x -Permissions", "Propagate to the subgroups of host group", "hostgroup 'x' (1)")]
    [InlineData("Invoke-ZbxTemplateGroupPropagation x -Permissions", "Propagate to the subgroups of template group", "templategroup 'x' (1)")]
    [InlineData("Add-ZbxTag -InputObject x -Name t -Value v", "Add tag t=v to host", "host 'x' (1)")]
    [InlineData("Remove-ZbxTag -InputObject x -Name t", "Remove tag t from host", "host 'x' (1)")]
    [InlineData("Invoke-ZbxScript x -Host x", "Run script 'x' (1)", "host 'x' (1)")]
    [InlineData("Invoke-ZbxScript x -EventId 7", "Run script 'x' (1)", "event 7")]
    [InlineData("Confirm-ZbxProblem 7 -Message m", "Acknowledge, add a message", "problem 7")]
    public async Task A_change_confirms_each_resolved_object_by_name(string command, string action, string target)
    {
        await using FakeZabbix server = await FakeZabbix.StartAsync();
        server.OnAny(Answer);
        using Shell shell = Shell.Open();
        shell.Connect(server);

        Run run = shell.Invoke(command + " -Confirm:$false -Verbose");

        Assert.Empty(run.Errors);
        Assert.Contains(run.Verbose, line => line.Contains($"\"{action}\" on target \"{target}\"", StringComparison.Ordinal));
    }

    [Theory]
    [InlineData("Unblock-ZbxUser no-such", "user.unblock")]
    [InlineData("Sync-ZbxProxyConfig no-such", "task.create")]
    [InlineData("Invoke-ZbxHostGroupPropagation no-such -Permissions", "hostgroup.propagate")]
    [InlineData("Add-ZbxTag -InputObject no-such -Name t", "host.update")]
    public async Task A_change_whose_objects_resolve_to_nothing_asks_nothing_and_calls_nothing(string command, string change)
    {
        await using FakeZabbix server = await FakeZabbix.StartAsync();
        server.OnAny((method, _) => method.EndsWith(".get", StringComparison.Ordinal) ? "[]" : "{}");
        using Shell shell = Shell.Open();
        shell.Connect(server);

        Run run = shell.Invoke(command + " -Verbose");

        Assert.Single(run.Errors);
        Assert.DoesNotContain(run.Verbose, line => line.Contains("on target", StringComparison.Ordinal));
        Assert.Empty(server.ParamsOf(change));
    }

    [Theory]
    [InlineData("Unblock-ZbxUser x -PassThru", typeof(ZabbixUser))]
    [InlineData("Reset-ZbxUserTotp x -PassThru", typeof(ZabbixUser))]
    [InlineData("Add-ZbxHostGroupMember x x -PassThru", typeof(ZabbixHost))]
    [InlineData("Remove-ZbxHostGroupMember x x -PassThru", typeof(ZabbixHost))]
    [InlineData("Add-ZbxHostTemplate x x -PassThru", typeof(ZabbixHost))]
    [InlineData("Remove-ZbxHostTemplate x x -PassThru", typeof(ZabbixHost))]
    public async Task A_sink_with_PassThru_writes_what_it_changed(string command, Type written)
    {
        await using FakeZabbix server = await FakeZabbix.StartAsync();
        server.OnAny(Answer);
        using Shell shell = Shell.Open();
        shell.Connect(server);

        Run run = shell.Invoke(command + " -Confirm:$false");

        Assert.Empty(run.Errors);
        Assert.IsType(written, Assert.Single(run.Output).BaseObject);
    }

    [Theory]
    [InlineData("Invoke-ZbxScript x -Host x -EventId 7", "AmbiguousParameterSet")]
    [InlineData("Watch-ZbxItem w -HostName x -Group g", "AmbiguousParameterSet")]
    public async Task Parameters_that_exclude_each_other_are_a_binding_error_before_anything_runs(string command, string error)
    {
        await using FakeZabbix server = await FakeZabbix.StartAsync();
        server.OnAny(Answer);
        using Shell shell = Shell.Open();
        shell.Connect(server);
        int before = server.Calls.Count;

        Run run = shell.Invoke(command + " -Confirm:$false");

        Assert.StartsWith(error + ",", Assert.Single(run.Errors).FullyQualifiedErrorId, StringComparison.Ordinal);
        Assert.Equal(before, server.Calls.Count);
    }
}
