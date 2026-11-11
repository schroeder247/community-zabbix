// This Source Code Form is subject to the terms of the Mozilla Public
// License, v. 2.0. Copyright (c) 2026 schroeder247. SPDX-License-Identifier: MPL-2.0
using System.Text.Json;
using Community.Zabbix.PowerShell.Commands;
using Community.Zabbix.Proxy;

namespace Community.Zabbix.PowerShell.Tests;

/// <summary>The active-proxy client: <c>Start-/Get-/Stop-ZbxActiveProxy</c>, <c>Get-ZbxActiveProxyLog</c>, <c>Receive-ZbxProxyConfig</c>, <c>Send-ZbxProxyData</c>, and the history/data constructors.</summary>
public sealed class ProxyClientCommandsTests
{
    private static readonly TimeSpan Guard = TimeSpan.FromSeconds(30);

    [Fact]
    public async Task Start_ZbxActiveProxy_starts_a_session_Get_ZbxActiveProxyStatus_finds_by_name()
    {
        FakeTrapper server = FakeTrapper.Start();
        server.On("proxy config", "{}");
        TaskCompletionSource<string> arrived = server.ExpectArrival();
        using Shell shell = Shell.Open();
        try
        {
            Run started = shell.Invoke($"Start-ZbxActiveProxy probe zbx-proxy '127.0.0.1:{server.Port}' -Confirm:$false -PassThru");
            Assert.Empty(started.Errors);
            var session = Assert.IsType<ZabbixActiveProxySession>(Assert.Single(started.Output).BaseObject);
            Assert.Equal("probe", session.Name);
            Assert.Equal("zbx-proxy", session.ProxyName);

            Assert.Contains("\"request\":\"proxy config\"", await arrived.Task.WaitAsync(Guard, TestContext.Current.CancellationToken), StringComparison.Ordinal);

            Run found = shell.Invoke("Get-ZbxActiveProxyStatus probe");
            Assert.Same(session, Assert.Single(found.Output).BaseObject);
        }
        finally
        {
            shell.Invoke("Stop-ZbxActiveProxy probe -Confirm:$false");
            await server.DisposeAsync();
        }
    }

    [Fact]
    public async Task Set_ZbxActiveProxy_retunes_the_running_sessions_refresh_interval()
    {
        FakeTrapper server = FakeTrapper.Start();
        server.On("proxy config", "{}");
        using Shell shell = Shell.Open();
        try
        {
            Run started = shell.Invoke($"Start-ZbxActiveProxy probe zbx-proxy '127.0.0.1:{server.Port}' -RefreshInterval 00:01:00 -Confirm:$false -PassThru");
            var session = Assert.IsType<ZabbixActiveProxySession>(Assert.Single(started.Output).BaseObject);
            Assert.Equal(TimeSpan.FromMinutes(1), session.RefreshInterval);

            Run retuned = shell.Invoke("Set-ZbxActiveProxy probe 5 -Confirm:$false -PassThru");

            Assert.Empty(retuned.Errors);
            Assert.Same(session, Assert.Single(retuned.Output).BaseObject);
            Assert.Equal(TimeSpan.FromSeconds(5), session.RefreshInterval);
        }
        finally
        {
            shell.Invoke("Stop-ZbxActiveProxy probe -Confirm:$false");
            await server.DisposeAsync();
        }
    }

    [Fact]
    public void Set_ZbxActiveProxy_of_an_unknown_name_is_one_non_terminating_error()
    {
        using Shell shell = Shell.Open();

        Run run = shell.Invoke("Set-ZbxActiveProxy nope 5 -Confirm:$false");

        Assert.Contains("ZabbixActiveProxyNotFound", Assert.Single(run.Errors).FullyQualifiedErrorId, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Stop_ZbxActiveProxy_removes_the_session_so_Get_ZbxActiveProxyStatus_no_longer_finds_it()
    {
        FakeTrapper server = FakeTrapper.Start();
        server.On("proxy config", "{}");
        using Shell shell = Shell.Open();
        try
        {
            shell.Invoke($"Start-ZbxActiveProxy probe zbx-proxy '127.0.0.1:{server.Port}' -Confirm:$false");

            Run stopped = shell.Invoke("Stop-ZbxActiveProxy probe -Confirm:$false");
            Assert.Empty(stopped.Errors);

            Run after = shell.Invoke("Get-ZbxActiveProxyStatus probe");
            Assert.Contains("ZabbixActiveProxyNotFound", Assert.Single(after.Errors).FullyQualifiedErrorId, StringComparison.Ordinal);
        }
        finally
        {
            await server.DisposeAsync();
        }
    }

    [Fact]
    public void Get_ZbxActiveProxyStatus_of_an_unknown_name_is_one_non_terminating_error()
    {
        using Shell shell = Shell.Open();

        Run run = shell.Invoke("Get-ZbxActiveProxyStatus nope");

        Assert.Empty(run.Output);
        Assert.Contains("ZabbixActiveProxyNotFound", Assert.Single(run.Errors).FullyQualifiedErrorId, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Get_ZbxActiveProxyLog_returns_the_session_s_ring_buffer_after_a_pull()
    {
        FakeTrapper server = FakeTrapper.Start();
        // A reply that actually changes the held configuration (a bare "{}" merges to nothing and logs nothing): the
        // uplink logs ProxyConfigReceived only when the merge produced something different from what was held.
        server.On("proxy config", """{"data":{"hosts":[{"hostid":1,"host":"h1","name":"h1","status":0}]}}""");
        TaskCompletionSource<string> arrived = server.ExpectArrival();
        using Shell shell = Shell.Open();
        try
        {
            shell.Invoke($"Start-ZbxActiveProxy probe zbx-proxy '127.0.0.1:{server.Port}' -Confirm:$false");
            await arrived.Task.WaitAsync(Guard, TestContext.Current.CancellationToken);

            // The session's own loop runs off any command's pipeline: poll until its log line lands (bounded by Guard).
            DateTime deadline = DateTime.UtcNow + Guard;
            Run log;
            do
            {
                log = shell.Invoke("Get-ZbxActiveProxyLog probe");
            } while (log.Output.Count == 0 && DateTime.UtcNow < deadline);

            Assert.Empty(log.Errors);
            Assert.NotEmpty(log.Output);
        }
        finally
        {
            shell.Invoke("Stop-ZbxActiveProxy probe -Confirm:$false");
            await server.DisposeAsync();
        }
    }

    [Fact]
    public async Task Receive_ZbxProxyConfig_pulls_the_configuration_once_with_no_session()
    {
        FakeTrapper server = FakeTrapper.Start();
        server.On("proxy config", "{}");
        using Shell shell = Shell.Open();
        try
        {
            Run run = shell.Invoke($"Receive-ZbxProxyConfig zbx-proxy '127.0.0.1:{server.Port}'");

            Assert.Empty(run.Errors);
            var config = Assert.IsType<ZabbixProxyConfig>(Assert.Single(run.Output).BaseObject);
            Assert.Equal(0, config.ConfigRevision);
            Assert.Empty(shell.Context.Sessions.All);                       // no session: one call, no bookkeeping left behind
        }
        finally
        {
            await server.DisposeAsync();
        }
    }

    [Fact]
    public async Task Send_ZbxProxyData_pushes_history_and_returns_the_server_s_answer()
    {
        FakeTrapper server = FakeTrapper.Start();
        server.On("proxy data", """{"response":"success","info":"processed: 1; failed: 0; total: 1; seconds spent: 0.000100"}""");
        using Shell shell = Shell.Open();
        try
        {
            Run run = shell.Invoke($"""
                $item = New-ZbxHistoryValue -ItemId 42 -Value '7'
                Send-ZbxProxyData zbx-proxy '127.0.0.1:{server.Port}' $item
                """);

            Assert.Empty(run.Errors);
            var response = Assert.IsType<ZabbixProxyResponse>(Assert.Single(run.Output).BaseObject);
            Assert.True(response.IsSuccess);
            Assert.Single(server.FramesOf("proxy data"));
        }
        finally
        {
            await server.DisposeAsync();
        }
    }

    [Fact]
    public async Task Send_ZbxProxyData_Version_announces_that_version()
    {
        // A fixed 7.4 announcement is refused by 7.0.31, "current proxy version is not supported by server".
        FakeTrapper server = FakeTrapper.Start();
        server.On("proxy data", """{"response":"success","info":"processed: 1; failed: 0; total: 1; seconds spent: 0.000100"}""");
        using Shell shell = Shell.Open();
        try
        {
            Run run = shell.Invoke($"""
                New-ZbxHistoryValue -ItemId 42 -Value '7' | Send-ZbxProxyData zbx-proxy '127.0.0.1:{server.Port}' -Version 7.0.0
                """);

            Assert.Empty(run.Errors);
            Assert.Equal("7.0.0", Assert.Single(server.FramesOf("proxy data")).GetProperty("version").GetString());
        }
        finally
        {
            await server.DisposeAsync();
        }
    }

    [Fact]
    public async Task Send_ZbxProxyData_WhatIf_sends_nothing()
    {
        // It writes values into the server's history, so it takes -WhatIf like Send-ZbxData.
        FakeTrapper server = FakeTrapper.Start();
        server.On("proxy data", """{"response":"success"}""");
        using Shell shell = Shell.Open();
        try
        {
            Run run = shell.Invoke($"""
                $item = New-ZbxHistoryValue -ItemId 42 -Value '7'
                Send-ZbxProxyData zbx-proxy '127.0.0.1:{server.Port}' $item -WhatIf
                """);

            Assert.Empty(run.Errors);
            Assert.Empty(run.Output);
            Assert.Empty(server.FramesOf("proxy data"));
        }
        finally
        {
            await server.DisposeAsync();
        }
    }

    [Fact]
    public void New_ZbxHistoryValue_builds_a_history_item_with_the_given_id_and_value()
    {
        using Shell shell = Shell.Open();

        Run run = shell.Invoke("New-ZbxHistoryValue -ItemId 100 -Value '42' -Clock 1000 -Ns 500 -Id 1");

        var item = Assert.IsType<ZabbixProxyHistoryItem>(Assert.Single(run.Output).BaseObject);
        Assert.Equal((100UL, "42", DateTimeOffset.FromUnixTimeSeconds(1000).AddTicks(5), 1L), (item.ItemId, item.Value, item.Clock, item.Sequence));
    }

    [Fact]
    public void New_ZbxHistoryDataEntry_auto_assigns_a_sequence_id_when_omitted()
    {
        using Shell shell = Shell.Open();

        Run run = shell.Invoke("1..3 | ForEach-Object { New-ZbxHistoryDataEntry -ItemId 1 -Value \"$_\" }");

        var items = run.Output.Select(o => (ZabbixProxyHistoryItem)o.BaseObject).ToArray();
        Assert.Equal(3, items.Length);
        Assert.Equal(3, items.Select(i => i.Sequence).Distinct().Count());  // each entry gets its own sequence
    }

    [Fact]
    public void New_ZbxProxyData_builds_the_request_with_the_given_history()
    {
        using Shell shell = Shell.Open();

        Run run = shell.Invoke("""
            $item = New-ZbxHistoryValue -ItemId 1 -Value 'x' -Clock 1 -Ns 0 -Id 1
            New-ZbxProxyData zbx-proxy session123 @($item) -ProxyDelay 5
            """);

        var request = Assert.IsType<ZabbixProxyDataRequest>(Assert.Single(run.Output).BaseObject);
        Assert.Equal("zbx-proxy", request.Host);
        Assert.Equal("session123", request.Session);
        Assert.Equal(5, request.ProxyDelay);
        Assert.Single(request.History!);
    }

    [Fact]
    public async Task Register_ZbxActiveProxyDataProvider_s_page_reaches_the_server_on_the_next_refresh_tick()
    {
        FakeTrapper server = FakeTrapper.Start();
        server.On("proxy config", "{}");
        server.On("proxy data", """{"response":"success"}""");
        TaskCompletionSource<string> arrived = server.ExpectArrival();
        using Shell shell = Shell.Open();
        try
        {
            shell.Invoke($"Start-ZbxActiveProxy probe zbx-proxy '127.0.0.1:{server.Port}' -RefreshInterval 00:00:00.050 -Confirm:$false");
            await arrived.Task.WaitAsync(Guard, TestContext.Current.CancellationToken);

            Run registered = shell.Invoke("""
                Register-ZbxActiveProxyDataProvider probe { [Community.Zabbix.Proxy.ZabbixProxyDataPage]::new([Community.Zabbix.Proxy.ZabbixProxyHistoryItem[]]@([Community.Zabbix.Proxy.ZabbixProxyHistoryItem]::new(1, 42, [DateTimeOffset]::FromUnixTimeSeconds(1700000000), 'provided'))) } -PassThru
                """);
            Assert.Empty(registered.Errors);

            JsonElement frame = await WaitForAsync(server, "proxy data", static frame => frame.TryGetProperty("history data", out _));
            Assert.Equal("provided", frame.GetProperty("history data")[0].GetProperty("value").GetString());
        }
        finally
        {
            shell.Invoke("Stop-ZbxActiveProxy probe -Confirm:$false");
            await server.DisposeAsync();
        }
    }

    [Fact]
    public async Task Register_ZbxActiveProxyTaskHandler_runs_a_task_from_the_ack_and_uploads_its_result()
    {
        FakeTrapper server = FakeTrapper.Start();
        server.On("proxy config", "{}");
        int replies = 0;
        server.On("proxy data", _ => Interlocked.Increment(ref replies) == 1
            ? """{"response":"success","tasks":[{"type":2,"clock":9,"ttl":0,"command_type":0,"command":"ls","execute_on":1,"parent_taskid":3}]}"""
            : """{"response":"success"}""");
        using Shell shell = Shell.Open();
        try
        {
            shell.Invoke($"Start-ZbxActiveProxy probe zbx-proxy '127.0.0.1:{server.Port}' -RefreshInterval 00:00:00.050 -Confirm:$false");
            // The handler first: the provider's page is what makes the session upload, and the first upload's answer
            // carries the task; a handler registered after it races the task.
            Run registered = shell.Invoke("Register-ZbxActiveProxyTaskHandler probe { param($task) 'handled: ' + $task.Command } -PassThru");
            Assert.Empty(registered.Errors);
            shell.Invoke("Register-ZbxActiveProxyDataProvider probe { [Community.Zabbix.Proxy.ZabbixProxyDataPage]::new([Community.Zabbix.Proxy.ZabbixProxyHistoryItem[]]@([Community.Zabbix.Proxy.ZabbixProxyHistoryItem]::new(1, 42, [DateTimeOffset]::FromUnixTimeSeconds(1700000000), 'kick'))) }");

            JsonElement frame = await WaitForAsync(server, "proxy data", static frame => frame.TryGetProperty("tasks", out _));
            Assert.Equal("handled: ls", frame.GetProperty("tasks")[0].GetProperty("info").GetString());
            Assert.Equal(3, frame.GetProperty("tasks")[0].GetProperty("parent_taskid").GetInt64());
        }
        finally
        {
            shell.Invoke("Stop-ZbxActiveProxy probe -Confirm:$false");
            await server.DisposeAsync();
        }
    }

    /// <summary>
    /// Waits for a <paramref name="request"/> frame satisfying <paramref name="matches"/> without polling: arms
    /// <see cref="FakeTrapper.ExpectArrival"/> (any later arrival resolves it, and <c>Frames.Enqueue</c> always
    /// happens-before the resulting <c>TrySetResult</c> in <see cref="FakeTrapper"/>, so a frame that lands between
    /// arming and the scan is still caught by the scan) before scanning the frames already recorded, so no arrival
    /// can be missed between the scan and the wait. Bounded as a whole, not per arrival: a proxy that keeps sending
    /// frames that never match would otherwise re-arm the wait for ever.
    /// </summary>
    private static async Task<JsonElement> WaitForAsync(FakeTrapper server, string request, Func<JsonElement, bool> matches)
    {
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(TestContext.Current.CancellationToken);
        deadline.CancelAfter(Guard);
        while (true)
        {
            TaskCompletionSource<string> next = server.ExpectArrival();
            foreach (JsonElement frame in server.FramesOf(request))
                if (matches(frame)) return frame;
            try
            {
                await next.Task.WaitAsync(deadline.Token);
            }
            catch (OperationCanceledException) when (deadline.IsCancellationRequested && !TestContext.Current.CancellationToken.IsCancellationRequested)
            {
                throw new TimeoutException($"no '{request}' frame matched within {Guard}");
            }
        }
    }
}
