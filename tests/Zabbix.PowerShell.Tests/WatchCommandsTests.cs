// This Source Code Form is subject to the terms of the Mozilla Public
// License, v. 2.0. Copyright (c) 2026 schroeder247. SPDX-License-Identifier: MPL-2.0
using Community.Zabbix.PowerShell.Commands;

namespace Community.Zabbix.PowerShell.Tests;

/// <summary>The tree watch, kept as a session: <c>Watch-ZbxItem</c>, <c>Get-ZbxWatch</c>, <c>Receive-ZbxWatch</c>, <c>Stop-ZbxWatch</c>.</summary>
public sealed class WatchCommandsTests
{
    [Fact]
    public async Task Watch_ZbxItem_starts_a_session_that_Get_ZbxWatch_finds_by_name()
    {
        await using FakeZabbix server = await FakeZabbix.StartAsync();
        server.On("item.get", "[]").On("host.get", FakeZabbix.Hosts(("10084", "web-01"), ("10085", "web-02")));
        using Shell shell = Shell.Open();
        shell.Connect(server);

        Run started = shell.Invoke("Watch-ZbxItem probe web-01 -PassThru");
        Assert.Empty(started.Errors);
        var watch = Assert.IsType<ZabbixWatch>(Assert.Single(started.Output).BaseObject);
        Assert.Equal("probe", watch.Name);

        Run found = shell.Invoke("Get-ZbxWatch probe");
        Assert.Empty(found.Errors);
        Assert.Same(watch, Assert.Single(found.Output).BaseObject);

        Run listed = shell.Invoke("Get-ZbxWatch");
        Assert.Same(watch, Assert.Single(listed.Output).BaseObject);
    }

    [Fact]
    public async Task Watch_ZbxItem_WhatIf_starts_no_session()
    {
        // It starts a background session, as Start-Zbx* does, so -WhatIf describes it and keeps nothing.
        await using FakeZabbix server = await FakeZabbix.StartAsync();
        server.On("item.get", "[]").On("host.get", FakeZabbix.Hosts(("10084", "web-01")));
        using Shell shell = Shell.Open();
        shell.Connect(server);

        Run run = shell.Invoke("Watch-ZbxItem probe web-01 -WhatIf -PassThru; @(Get-ZbxWatch).Count");

        Assert.Empty(run.Errors);
        Assert.Equal(0, Assert.Single(run.Output).BaseObject);
    }

    [Fact]
    public async Task A_second_watch_of_the_same_name_is_refused_the_first_keeps_running()
    {
        await using FakeZabbix server = await FakeZabbix.StartAsync();
        server.On("item.get", "[]").On("host.get", FakeZabbix.Hosts(("10084", "web-01"), ("10085", "web-02")));
        using Shell shell = Shell.Open();
        shell.Connect(server);
        shell.Invoke("Watch-ZbxItem probe web-01");

        Run again = shell.Invoke("Watch-ZbxItem probe web-02");

        Assert.Empty(again.Output);
        Assert.Contains("ZabbixWatchNameInUse", Assert.Single(again.Errors).FullyQualifiedErrorId, StringComparison.Ordinal);
        Assert.Single(shell.Invoke("Get-ZbxWatch").Output);
    }

    [Fact]
    public void Get_ZbxWatch_of_an_unknown_name_is_one_non_terminating_error()
    {
        using Shell shell = Shell.Open();

        Run run = shell.Invoke("Get-ZbxWatch nope");

        Assert.Empty(run.Output);
        Assert.Equal("ZabbixWatchNotFound,Community.Zabbix.PowerShell.Commands.GetZbxWatchCommand", Assert.Single(run.Errors).FullyQualifiedErrorId);
    }

    [Fact]
    public void Receive_ZbxWatch_of_an_unknown_name_is_one_non_terminating_error()
    {
        using Shell shell = Shell.Open();

        Run run = shell.Invoke("Receive-ZbxWatch nope");

        Assert.Empty(run.Output);
        Assert.Equal("ZabbixWatchNotFound,Community.Zabbix.PowerShell.Commands.ReceiveZbxWatchCommand", Assert.Single(run.Errors).FullyQualifiedErrorId);
    }

    [Fact]
    public async Task Stop_ZbxWatch_removes_the_session_so_Get_ZbxWatch_no_longer_finds_it()
    {
        // host.get must be stubbed here (not just item.get): ZabbixWatcher's first tick runs from the constructor,
        // before Watch-ZbxItem even returns, and ZabbixNode.GetItemsAsync — finding item.get empty — falls back to
        // ZabbixNode.HostExistsAsync (host.get) to tell "the host has no items" from "the host does not exist". With
        // no host.get stub that lookup itself answers "Method not found", which ZabbixWatcher.TickAsync logs as a
        // tick failure; ZabbixPowerShellLoggerProvider queues an Error logged off any command's pipeline for the
        // *next* command on this connection to flush, so the exact command that ends up wearing that stray error
        // depends on how the background tick races the rest of this test (it can land on
        // Stop-ZbxWatch's own Errors and fail the assertion below). Stubbing host.get with a real answer removes the
        // tick failure entirely, so there is nothing left to race.
        await using FakeZabbix server = await FakeZabbix.StartAsync();
        server.On("item.get", "[]").On("host.get", FakeZabbix.Hosts(("10084", "web-01"), ("10085", "web-02")));
        using Shell shell = Shell.Open();
        shell.Connect(server);
        shell.Invoke("Watch-ZbxItem probe web-01");

        Run stopped = shell.Invoke("Stop-ZbxWatch probe -Confirm:$false");
        Assert.Empty(stopped.Errors);

        Run after = shell.Invoke("Get-ZbxWatch probe");
        Assert.Contains("ZabbixWatchNotFound", Assert.Single(after.Errors).FullyQualifiedErrorId, StringComparison.Ordinal);
    }

    [Fact]
    public void Stop_ZbxWatch_of_an_unknown_name_is_one_non_terminating_error()
    {
        using Shell shell = Shell.Open();

        Run run = shell.Invoke("Stop-ZbxWatch nope -Confirm:$false");

        Assert.Equal("ZabbixWatchNotFound,Community.Zabbix.PowerShell.Commands.StopZbxWatchCommand", Assert.Single(run.Errors).FullyQualifiedErrorId);
    }

    [Fact]
    public async Task Closing_the_runspace_stops_every_running_watch()
    {
        await using FakeZabbix server = await FakeZabbix.StartAsync();
        server.On("item.get", "[]").On("host.get", FakeZabbix.Hosts(("10084", "web-01"), ("10085", "web-02")));
        Shell shell = Shell.Open();
        shell.Connect(server);
        shell.Invoke("Watch-ZbxItem probe web-01");
        ZabbixSessions sessions = shell.Context.Sessions;
        Assert.Single(sessions.All);

        shell.Dispose();                                              // closing the runspace closes the context, which closes every session

        Assert.Empty(sessions.All);
    }

    [Fact]
    public async Task Watch_ZbxItem_Key_without_a_host_is_refused_not_ignored()
    {
        // -Key is never dropped to watch every host's items: it is in the host's parameter set, so binding
        // refuses it without -HostName (terminating, as every binding error is).
        await using FakeZabbix server = await FakeZabbix.StartAsync();
        using Shell shell = Shell.Open();
        shell.Connect(server);

        Run run = shell.Invoke("Watch-ZbxItem probe -Key agent.ping");

        Assert.StartsWith("MissingMandatoryParameter,", Assert.Single(run.Errors).FullyQualifiedErrorId, StringComparison.Ordinal);
        Assert.Empty(shell.Invoke("Get-ZbxWatch").Output);
    }
}
