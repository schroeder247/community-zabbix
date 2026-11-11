// This Source Code Form is subject to the terms of the Mozilla Public
// License, v. 2.0. Copyright (c) 2026 schroeder247. SPDX-License-Identifier: MPL-2.0
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Text.Json;
using Community.Zabbix.PowerShell.Commands;
using Community.Zabbix.Protocol;

namespace Community.Zabbix.PowerShell.Tests;

/// <summary>The passive-proxy listener: <c>Start-/Get-/Stop-ZbxPassiveProxy</c>, <c>Get-ZbxPassiveProxyLog</c>, <c>Send-ZbxPassiveProxyHistory</c>.</summary>
public sealed class PassiveProxyCommandsTests
{
    [Fact]
    public void Start_ZbxPassiveProxy_listens_and_Get_ZbxPassiveProxyStatus_finds_it_by_name()
    {
        using Shell shell = Shell.Open();
        try
        {
            Run started = shell.Invoke("Start-ZbxPassiveProxy probe 127.0.0.1 -Version 7.4 -Port 0 -Confirm:$false -PassThru");
            Assert.Empty(started.Errors);
            var session = Assert.IsType<ZabbixPassiveProxySession>(Assert.Single(started.Output).BaseObject);
            Assert.Equal("probe", session.Name);
            Assert.NotNull(session.Listener);

            Run found = shell.Invoke("Get-ZbxPassiveProxyStatus probe");
            Assert.Same(session, Assert.Single(found.Output).BaseObject);
        }
        finally
        {
            shell.Invoke("Stop-ZbxPassiveProxy probe -Confirm:$false");
        }
    }

    [Fact]
    public void A_second_passive_proxy_of_the_same_name_is_refused_the_first_keeps_running()
    {
        using Shell shell = Shell.Open();
        try
        {
            shell.Invoke("Start-ZbxPassiveProxy probe 127.0.0.1 -Version 7.4 -Port 0 -Confirm:$false");

            Run again = shell.Invoke("Start-ZbxPassiveProxy probe 127.0.0.1 -Version 7.4 -Port 0 -Confirm:$false");

            Assert.Contains("ZabbixPassiveProxyNameInUse", Assert.Single(again.Errors).FullyQualifiedErrorId, StringComparison.Ordinal);
            Assert.Single(shell.Invoke("Get-ZbxPassiveProxyStatus").Output);
        }
        finally
        {
            shell.Invoke("Stop-ZbxPassiveProxy probe -Confirm:$false");
        }
    }

    [Fact]
    public void Stop_ZbxPassiveProxy_removes_the_session_so_Get_ZbxPassiveProxyStatus_no_longer_finds_it()
    {
        using Shell shell = Shell.Open();
        shell.Invoke("Start-ZbxPassiveProxy probe 127.0.0.1 -Version 7.4 -Port 0 -Confirm:$false");

        Run stopped = shell.Invoke("Stop-ZbxPassiveProxy probe -Confirm:$false");
        Assert.Empty(stopped.Errors);

        Run after = shell.Invoke("Get-ZbxPassiveProxyStatus probe");
        Assert.Contains("ZabbixPassiveProxyNotFound", Assert.Single(after.Errors).FullyQualifiedErrorId, StringComparison.Ordinal);
    }

    [Fact]
    public void Get_ZbxPassiveProxyStatus_of_an_unknown_name_is_one_non_terminating_error()
    {
        using Shell shell = Shell.Open();

        Run run = shell.Invoke("Get-ZbxPassiveProxyStatus nope");

        Assert.Empty(run.Output);
        Assert.Contains("ZabbixPassiveProxyNotFound", Assert.Single(run.Errors).FullyQualifiedErrorId, StringComparison.Ordinal);
    }

    [Fact]
    public void Send_ZbxPassiveProxyHistory_writes_into_the_session_s_store()
    {
        using Shell shell = Shell.Open();
        try
        {
            shell.Invoke("Start-ZbxPassiveProxy probe 127.0.0.1 -Version 7.4 -Port 0 -Confirm:$false");

            Run sent = shell.Invoke("Send-ZbxPassiveProxyHistory probe web-01 agent.ping 1 -Confirm:$false");

            Assert.Empty(sent.Errors);
        }
        finally
        {
            shell.Invoke("Stop-ZbxPassiveProxy probe -Confirm:$false");
        }
    }

    [Fact]
    public void Send_ZbxPassiveProxyHistory_of_an_unknown_session_is_one_non_terminating_error()
    {
        using Shell shell = Shell.Open();

        Run run = shell.Invoke("Send-ZbxPassiveProxyHistory nope web-01 agent.ping 1 -Confirm:$false");

        Assert.Contains("ZabbixPassiveProxyNotFound", Assert.Single(run.Errors).FullyQualifiedErrorId, StringComparison.Ordinal);
    }

    [Fact]
    public void Get_ZbxPassiveProxyLog_of_an_unknown_session_is_one_non_terminating_error()
    {
        using Shell shell = Shell.Open();

        Run run = shell.Invoke("Get-ZbxPassiveProxyLog nope");

        Assert.Empty(run.Output);
        Assert.Contains("ZabbixPassiveProxyNotFound", Assert.Single(run.Errors).FullyQualifiedErrorId, StringComparison.Ordinal);
    }

    // ----- Register-ZbxPassiveProxyConfigHandler / -DataProvider / -StatsProvider ---------------------------------------

    private static async Task<JsonElement> RequestAsync(int port, string request, string? send = null)
    {
        using var client = new TcpClient();
        await client.ConnectAsync(IPAddress.Loopback, port, TestContext.Current.CancellationToken);
        await using NetworkStream stream = client.GetStream();
        await ZabbixHeader.WriteAsync(stream, Encoding.UTF8.GetBytes(request), TestContext.Current.CancellationToken);
        JsonElement first = JsonDocument.Parse(await ZabbixHeader.ReadAsync(stream, cancellationToken: TestContext.Current.CancellationToken)).RootElement.Clone();
        if (send is null) return first;
        await ZabbixHeader.WriteAsync(stream, Encoding.UTF8.GetBytes(send), TestContext.Current.CancellationToken);
        return JsonDocument.Parse(await ZabbixHeader.ReadAsync(stream, cancellationToken: TestContext.Current.CancellationToken)).RootElement.Clone();
    }

    [Fact]
    public async Task Register_ZbxPassiveProxyConfigHandler_can_veto_a_pushed_configuration()
    {
        using Shell shell = Shell.Open();
        try
        {
            Run started = shell.Invoke("Start-ZbxPassiveProxy probe 127.0.0.1 -Version 7.4 -Port 0 -Confirm:$false -PassThru");
            var session = Assert.IsType<ZabbixPassiveProxySession>(Assert.Single(started.Output).BaseObject);
            int port = ((IPEndPoint)session.Listener!.LocalEndPoint!).Port;

            Run registered = shell.Invoke("""Register-ZbxPassiveProxyConfigHandler probe { 'not today' } -PassThru""");
            Assert.Empty(registered.Errors);

            JsonElement ack = await RequestAsync(port, """{"request":"proxy config"}""",
                """{"full_sync":1,"config_revision":1,"data":{"hosts":{"fields":["hostid","host","status"],"data":[]},"items":{"fields":["itemid","type","hostid","key_","status","trapper_hosts","allow_traps"],"data":[]}}}""");

            Assert.Equal("failed", ack.GetProperty("response").GetString());
            Assert.Equal("not today", ack.GetProperty("info").GetString());
        }
        finally
        {
            shell.Invoke("Stop-ZbxPassiveProxy probe -Confirm:$false");
        }
    }

    [Fact]
    public async Task Register_ZbxPassiveProxyDataProvider_s_page_is_pulled_by_the_server()
    {
        using Shell shell = Shell.Open();
        try
        {
            Run started = shell.Invoke("Start-ZbxPassiveProxy probe 127.0.0.1 -Version 7.4 -Port 0 -Confirm:$false -PassThru");
            var session = Assert.IsType<ZabbixPassiveProxySession>(Assert.Single(started.Output).BaseObject);
            int port = ((IPEndPoint)session.Listener!.LocalEndPoint!).Port;

            Run registered = shell.Invoke("""
                Register-ZbxPassiveProxyDataProvider probe { [Community.Zabbix.Proxy.ZabbixProxyDataPage]::new([Community.Zabbix.Proxy.ZabbixProxyHistoryItem[]]@([Community.Zabbix.Proxy.ZabbixProxyHistoryItem]::new(1, 42, [DateTimeOffset]::FromUnixTimeSeconds(1700000000), 'provided'))) } -PassThru
                """);
            Assert.Empty(registered.Errors);

            JsonElement page = await RequestAsync(port, """{"request":"proxy data"}""");

            Assert.Equal("provided", page.GetProperty("history data")[0].GetProperty("value").GetString());
        }
        finally
        {
            shell.Invoke("Stop-ZbxPassiveProxy probe -Confirm:$false");
        }
    }

    [Fact]
    public async Task Register_ZbxPassiveProxyStatsProvider_answers_zabbix_stats()
    {
        using Shell shell = Shell.Open();
        try
        {
            Run started = shell.Invoke("Start-ZbxPassiveProxy probe 127.0.0.1 -Version 7.4 -Port 0 -Confirm:$false -PassThru");
            var session = Assert.IsType<ZabbixPassiveProxySession>(Assert.Single(started.Output).BaseObject);
            int port = ((IPEndPoint)session.Listener!.LocalEndPoint!).Port;

            Run registered = shell.Invoke("""Register-ZbxPassiveProxyStatsProvider probe { @{ uptime = 123 } } -PassThru""");
            Assert.Empty(registered.Errors);

            JsonElement answer = await RequestAsync(port, """{"request":"zabbix.stats"}""");

            Assert.Equal("success", answer.GetProperty("response").GetString());
            Assert.Equal(123, answer.GetProperty("data").GetProperty("uptime").GetInt32());
        }
        finally
        {
            shell.Invoke("Stop-ZbxPassiveProxy probe -Confirm:$false");
        }
    }
}
