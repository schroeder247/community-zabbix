// This Source Code Form is subject to the terms of the Mozilla Public
// License, v. 2.0. Copyright (c) 2026 schroeder247. SPDX-License-Identifier: MPL-2.0
using System.Management.Automation;
using Community.Zabbix.PowerShell.Commands;
using Community.Zabbix.PowerShell.Diagnostics;
using Microsoft.Extensions.Logging;

namespace Community.Zabbix.PowerShell.Tests;

/// <summary>The active/passive agent sessions: <c>Start-/Get-/Stop-ZbxActiveAgent</c>, <c>Start-/Get-/Stop-ZbxPassiveAgent</c>, <c>Test-ZbxAgentItem</c>.</summary>
public sealed class AgentCommandsTests
{
    private static readonly TimeSpan Guard = TimeSpan.FromSeconds(30);

    private const string EmptyChecks = """{"response":"success","config_revision":1}""";
    private const string Success = """{"response":"success","info":"processed: 0; failed: 0; total: 0; seconds spent: 0.000100"}""";

    [Fact]
    public async Task Start_ZbxActiveAgent_starts_a_session_Get_ZbxActiveAgentStatus_finds_by_name()
    {
        FakeTrapper server = FakeTrapper.Start();
        server.On("active checks", EmptyChecks).On("agent data", Success);
        using Shell shell = Shell.Open();
        try
        {
            Run started = shell.Invoke($"Start-ZbxActiveAgent probe web-01 '127.0.0.1:{server.Port}' -Confirm:$false -PassThru");
            Assert.Empty(started.Errors);
            var session = Assert.IsType<ZabbixAgentSession>(Assert.Single(started.Output).BaseObject);
            Assert.Equal("probe", session.Name);
            Assert.Equal("active", session.Kind);

            Run found = shell.Invoke("Get-ZbxActiveAgentStatus probe");
            Assert.Empty(found.Errors);
            Assert.Same(session, Assert.Single(found.Output).BaseObject);

            Run listed = shell.Invoke("Get-ZbxActiveAgentStatus");
            Assert.Same(session, Assert.Single(listed.Output).BaseObject);
        }
        finally
        {
            shell.Invoke("Stop-ZbxActiveAgent probe -Confirm:$false");
            await server.DisposeAsync();
        }
    }

    [Fact]
    public async Task A_second_active_agent_of_the_same_name_is_refused_the_first_keeps_running()
    {
        FakeTrapper server = FakeTrapper.Start();
        server.On("active checks", EmptyChecks).On("agent data", Success);
        using Shell shell = Shell.Open();
        try
        {
            shell.Invoke($"Start-ZbxActiveAgent probe web-01 '127.0.0.1:{server.Port}' -Confirm:$false");

            Run again = shell.Invoke($"Start-ZbxActiveAgent probe web-02 '127.0.0.1:{server.Port}' -Confirm:$false");

            Assert.Contains("ZabbixAgentNameInUse", Assert.Single(again.Errors).FullyQualifiedErrorId, StringComparison.Ordinal);
            Assert.Single(shell.Invoke("Get-ZbxActiveAgentStatus").Output);
        }
        finally
        {
            shell.Invoke("Stop-ZbxActiveAgent probe -Confirm:$false");
            await server.DisposeAsync();
        }
    }

    [Fact]
    public async Task Stop_ZbxActiveAgent_removes_the_session_so_Get_ZbxActiveAgentStatus_no_longer_finds_it()
    {
        FakeTrapper server = FakeTrapper.Start();
        server.On("active checks", EmptyChecks).On("agent data", Success);
        using Shell shell = Shell.Open();
        try
        {
            shell.Invoke($"Start-ZbxActiveAgent probe web-01 '127.0.0.1:{server.Port}' -Confirm:$false");

            Run stopped = shell.Invoke("Stop-ZbxActiveAgent probe -Confirm:$false");
            Assert.Empty(stopped.Errors);

            Run after = shell.Invoke("Get-ZbxActiveAgentStatus probe");
            Assert.Contains("ZabbixAgentNotFound", Assert.Single(after.Errors).FullyQualifiedErrorId, StringComparison.Ordinal);
        }
        finally
        {
            await server.DisposeAsync();
        }
    }

    [Fact]
    public void Get_ZbxActiveAgentStatus_of_an_unknown_name_is_one_non_terminating_error()
    {
        using Shell shell = Shell.Open();

        Run run = shell.Invoke("Get-ZbxActiveAgentStatus nope");

        Assert.Empty(run.Output);
        Assert.Contains("ZabbixAgentNotFound", Assert.Single(run.Errors).FullyQualifiedErrorId, StringComparison.Ordinal);
    }

    [Fact]
    public void Stop_ZbxActiveAgent_of_an_unknown_name_is_one_non_terminating_error()
    {
        using Shell shell = Shell.Open();

        Run run = shell.Invoke("Stop-ZbxActiveAgent nope -Confirm:$false");

        Assert.Contains("ZabbixAgentNotFound", Assert.Single(run.Errors).FullyQualifiedErrorId, StringComparison.Ordinal);
    }

    [Fact]
    public void Start_ZbxPassiveAgent_listens_and_Get_ZbxPassiveAgentStatus_finds_it_by_name()
    {
        using Shell shell = Shell.Open();
        try
        {
            Run started = shell.Invoke("Start-ZbxPassiveAgent probe web-01 127.0.0.1 -Port 0 -Confirm:$false -PassThru");
            Assert.Empty(started.Errors);
            var session = Assert.IsType<ZabbixAgentSession>(Assert.Single(started.Output).BaseObject);
            Assert.Equal("passive", session.Kind);
            Assert.NotNull(session.Listener);

            Run found = shell.Invoke("Get-ZbxPassiveAgentStatus probe");
            Assert.Same(session, Assert.Single(found.Output).BaseObject);

            // The two kinds are kept apart: an active-only lookup does not find a passive session of the same name.
            Run wrongKind = shell.Invoke("Get-ZbxActiveAgentStatus probe");
            Assert.Contains("ZabbixAgentNotFound", Assert.Single(wrongKind.Errors).FullyQualifiedErrorId, StringComparison.Ordinal);
        }
        finally
        {
            shell.Invoke("Stop-ZbxPassiveAgent probe -Confirm:$false");
        }
    }

    [Fact]
    public void Start_ZbxPassiveAgent_defaults_AllowedPeer_to_loopback_only_fail_closed()
    {
        // Zabbix's own agent never wildcards Server= by default; -AllowedPeer must be the same: loopback only,
        // unless the caller names the real server(s) explicitly.
        using Shell shell = Shell.Open();
        try
        {
            Run started = shell.Invoke("Start-ZbxPassiveAgent probe web-01 -Port 0 -Confirm:$false -PassThru");

            Assert.Empty(started.Errors);
            var session = Assert.IsType<ZabbixAgentSession>(Assert.Single(started.Output).BaseObject);
            Assert.NotNull(session.Listener);
        }
        finally
        {
            shell.Invoke("Stop-ZbxPassiveAgent probe -Confirm:$false");
        }
    }

    [Fact]
    public void Start_ZbxPassiveAgent_takes_a_pre_shared_key()
    {
        using Shell shell = Shell.Open();
        try
        {
            Run started = shell.Invoke(
                "Start-ZbxPassiveAgent probe web-01 127.0.0.1 -Port 0 -PskIdentity psk1 " +
                "-Psk (ConvertTo-SecureString ('11' * 16) -AsPlainText -Force) -Confirm:$false -PassThru");

            Assert.Empty(started.Errors);
            Assert.IsType<ZabbixAgentSession>(Assert.Single(started.Output).BaseObject);
        }
        finally
        {
            shell.Invoke("Stop-ZbxPassiveAgent probe -Confirm:$false");
        }
    }

    [Fact]
    public void Test_ZbxAgentItem_answers_a_built_in_key()
    {
        using Shell shell = Shell.Open();

        Run run = shell.Invoke("Test-ZbxAgentItem agent.ping");

        Assert.Empty(run.Errors);
        Assert.Equal("1", Assert.Single(run.Output).Properties["Value"].Value);
    }

    [Fact]
    public void Test_ZbxAgentItem_of_an_unknown_key_answers_unsupported_and_writes_no_error()
    {
        // A Test- writes its finding, not an error: the agent's own answer to a key it has no handler for.
        using Shell shell = Shell.Open();

        Run run = shell.Invoke("Test-ZbxAgentItem nope.key");

        Assert.Empty(run.Errors);
        var answer = Assert.IsType<Zabbix.ZabbixAgentAnswer>(Assert.Single(run.Output).BaseObject);
        Assert.False(answer.IsSupported);
        Assert.Null(answer.Value);
    }

    [Fact]
    public void Test_ZbxAgentItem_of_a_key_that_does_not_parse_is_a_binding_error_for_that_key_alone()
    {
        using Shell shell = Shell.Open();

        Run run = shell.Invoke("'agent.ping', 'bad[', 'agent.version' | Test-ZbxAgentItem -Raw");

        Assert.Equal(ErrorCategory.InvalidArgument, Assert.Single(run.Errors).CategoryInfo.Category);
        Assert.Equal(2, run.Output.Count);
    }

    [Fact]
    public void Test_ZbxAgentItem_Raw_writes_the_bare_value_instead_of_the_whole_answer()
    {
        // -Raw skips the diagnostic ZabbixAgentAnswer wrapper.
        using Shell shell = Shell.Open();

        Run run = shell.Invoke("Test-ZbxAgentItem agent.ping -Raw");

        Assert.Empty(run.Errors);
        Assert.Equal("1", Assert.Single(run.Output).ToString());
    }

    [Fact]
    public void Register_ZbxAgentHandler_answers_through_Test_ZbxAgentItem_Session()
    {
        using Shell shell = Shell.Open();
        try
        {
            shell.Invoke("Start-ZbxPassiveAgent probe web-01 127.0.0.1 -Port 0 -Confirm:$false");

            Run registered = shell.Invoke("Register-ZbxAgentHandler probe custom.echo { param($Key) 'echoed' } -PassThru");
            Assert.Empty(registered.Errors);
            Assert.IsType<ZabbixAgentSession>(Assert.Single(registered.Output).BaseObject);

            Run answered = shell.Invoke("Test-ZbxAgentItem custom.echo -Session probe");
            Assert.Empty(answered.Errors);
            Assert.Equal("echoed", Assert.Single(answered.Output).Properties["Value"].Value);
        }
        finally
        {
            shell.Invoke("Stop-ZbxPassiveAgent probe -Confirm:$false");
        }
    }

    [Fact]
    public void Register_ZbxAgentHandler_accepts_a_binding_piped_from_New_ZbxAgentHandler()
    {
        using Shell shell = Shell.Open();
        try
        {
            shell.Invoke("Start-ZbxPassiveAgent probe web-01 127.0.0.1 -Port 0 -Confirm:$false");

            Run run = shell.Invoke("New-ZbxAgentHandler custom.echo { 'from-binding' } | Register-ZbxAgentHandler probe");
            Assert.Empty(run.Errors);

            Run answered = shell.Invoke("Test-ZbxAgentItem custom.echo -Session probe");
            Assert.Equal("from-binding", Assert.Single(answered.Output).Properties["Value"].Value);
        }
        finally
        {
            shell.Invoke("Stop-ZbxPassiveAgent probe -Confirm:$false");
        }
    }

    [Fact]
    public void A_handler_that_throws_answers_with_its_message_not_a_terminating_error()
    {
        using Shell shell = Shell.Open();
        try
        {
            shell.Invoke("Start-ZbxPassiveAgent probe web-01 127.0.0.1 -Port 0 -Confirm:$false");
            shell.Invoke("Register-ZbxAgentHandler probe custom.fail { throw 'boom' }");

            Run answered = shell.Invoke("Test-ZbxAgentItem custom.fail -Session probe");

            Assert.Empty(answered.Errors);
            var answer = Assert.Single(answered.Output);
            Assert.Null(answer.Properties["Value"].Value);
            Assert.Contains("boom", (string)answer.Properties["Error"].Value, StringComparison.Ordinal);
        }
        finally
        {
            shell.Invoke("Stop-ZbxPassiveAgent probe -Confirm:$false");
        }
    }

    [Fact]
    public void Test_ZbxAgentItem_Session_of_an_unregistered_key_answers_unsupported_not_a_terminating_error()
    {
        using Shell shell = Shell.Open();
        try
        {
            shell.Invoke("Start-ZbxPassiveAgent probe web-01 127.0.0.1 -Port 0 -Confirm:$false");

            Run answered = shell.Invoke("Test-ZbxAgentItem nope.key -Session probe");

            Assert.Empty(answered.Errors);
            var answer = Assert.Single(answered.Output);
            Assert.Null(answer.Properties["Value"].Value);
            Assert.Equal("This agent does not answer the key.", answer.Properties["Error"].Value);
        }
        finally
        {
            shell.Invoke("Stop-ZbxPassiveAgent probe -Confirm:$false");
        }
    }

    [Fact]
    public void Register_ZbxActiveAgentHandler_refuses_a_passive_session_of_the_same_name()
    {
        using Shell shell = Shell.Open();
        try
        {
            shell.Invoke("Start-ZbxPassiveAgent probe web-01 127.0.0.1 -Port 0 -Confirm:$false");

            Run run = shell.Invoke("Register-ZbxActiveAgentHandler probe custom.echo { 'x' }");

            Assert.Contains("ZabbixAgentNotFound", Assert.Single(run.Errors).FullyQualifiedErrorId, StringComparison.Ordinal);
        }
        finally
        {
            shell.Invoke("Stop-ZbxPassiveAgent probe -Confirm:$false");
        }
    }

    [Fact]
    public void Register_ZbxPassiveAgentHandler_accepts_the_matching_kind()
    {
        using Shell shell = Shell.Open();
        try
        {
            shell.Invoke("Start-ZbxPassiveAgent probe web-01 127.0.0.1 -Port 0 -Confirm:$false");

            Run run = shell.Invoke("Register-ZbxPassiveAgentHandler probe custom.echo { 'x' }");

            Assert.Empty(run.Errors);
        }
        finally
        {
            shell.Invoke("Stop-ZbxPassiveAgent probe -Confirm:$false");
        }
    }

    // The session's agent is the SDK's ZabbixPassiveAgent, whose Items is live: a
    // scriptblock registered after the start is answered on the wire from the next request on.
    [Fact]
    public async Task A_handler_registered_on_a_running_passive_agent_is_answered_on_the_wire()
    {
        using Shell shell = Shell.Open();
        try
        {
            Run started = shell.Invoke("Start-ZbxPassiveAgent probe web-01 127.0.0.1 -Port 0 -Confirm:$false -PassThru");
            var session = Assert.IsType<ZabbixAgentSession>(Assert.Single(started.Output).BaseObject);
            var bound = (System.Net.IPEndPoint)session.Listener!.LocalEndPoint!;
            using var getter = new ZabbixGetter(new ZabbixServerNode("127.0.0.1", bound.Port));          // it listens on every address

            Assert.Empty(shell.Invoke("Register-ZbxPassiveAgentHandler probe custom.echo { 'x' }").Errors);

            Assert.Equal("x", (await getter.GetAsync("custom.echo", TestContext.Current.CancellationToken)).Value);
            Assert.Equal("web-01", (await getter.GetAsync("agent.hostname", TestContext.Current.CancellationToken)).Value);
        }
        finally
        {
            shell.Invoke("Stop-ZbxPassiveAgent probe -Confirm:$false");
        }
    }

    [Fact]
    public void Register_ZbxAgentHandler_of_an_unknown_session_is_one_non_terminating_error()
    {
        using Shell shell = Shell.Open();

        Run run = shell.Invoke("Register-ZbxAgentHandler nope custom.echo { 'x' }");

        Assert.Contains("ZabbixAgentNotFound", Assert.Single(run.Errors).FullyQualifiedErrorId, StringComparison.Ordinal);
    }

    [Fact]
    public void Get_ZbxActiveAgentLog_of_an_unknown_name_is_one_non_terminating_error()
    {
        using Shell shell = Shell.Open();

        Run run = shell.Invoke("Get-ZbxActiveAgentLog nope");

        Assert.Empty(run.Output);
        Assert.Contains("ZabbixAgentNotFound", Assert.Single(run.Errors).FullyQualifiedErrorId, StringComparison.Ordinal);
    }

    [Fact]
    public void Get_ZbxPassiveAgentLog_of_an_unknown_name_is_one_non_terminating_error()
    {
        using Shell shell = Shell.Open();

        Run run = shell.Invoke("Get-ZbxPassiveAgentLog nope");

        Assert.Empty(run.Output);
        Assert.Contains("ZabbixAgentNotFound", Assert.Single(run.Errors).FullyQualifiedErrorId, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Get_ZbxActiveAgentLog_returns_the_session_s_own_ring_buffer_after_a_refresh()
    {
        FakeTrapper server = FakeTrapper.Start();
        // A reply that actually changes the held revision with a (possibly empty) "data" array: AgentLog.ActiveChecksReceived
        // (3200) logs only when response.Data is not null — a bare EmptyChecks reply (no "data") logs nothing.
        server.On("active checks", """{"response":"success","config_revision":2,"data":[]}""").On("agent data", Success);
        using Shell shell = Shell.Open();
        try
        {
            shell.Invoke($"Start-ZbxActiveAgent probe web-01 '127.0.0.1:{server.Port}' -Confirm:$false -RefreshInterval 00:00:00.05");

            // The session's own loop runs off any command's pipeline: poll until its log line lands (bounded by Guard).
            DateTime deadline = DateTime.UtcNow + Guard;
            Run log;
            do
            {
                log = shell.Invoke("Get-ZbxActiveAgentLog probe");
            } while (log.Output.Count == 0 && DateTime.UtcNow < deadline);

            Assert.Empty(log.Errors);
            Assert.NotEmpty(log.Output);
        }
        finally
        {
            shell.Invoke("Stop-ZbxActiveAgent probe -Confirm:$false");
            await server.DisposeAsync();
        }
    }

    [Fact]
    public void Get_ZbxPassiveAgentLog_reads_and_Clear_ZbxPassiveAgentLog_empties_a_running_sessions_log()
    {
        // Get- reads (-Tail, -Level) and changes nothing; Clear- empties.
        using Shell shell = Shell.Open();
        try
        {
            shell.Invoke("Start-ZbxPassiveAgent probe web-01 127.0.0.1 -Port 0 -Confirm:$false");
            ZabbixAgentSession session = Assert.Single(shell.Context.Sessions.OfType<ZabbixAgentSession>());
            session.Logs.CreateLogger("probe").LogWarning("one");
            session.Logs.CreateLogger("probe").LogWarning("two");

            Run read = shell.Invoke("Get-ZbxPassiveAgentLog probe -Tail 1 -Level Warning");
            Run again = shell.Invoke("Get-ZbxPassiveAgentLog probe");
            Run cleared = shell.Invoke("Clear-ZbxPassiveAgentLog probe -Confirm:$false; Get-ZbxPassiveAgentLog probe");

            Assert.Empty(read.Errors);
            Assert.Equal("two", ((ZabbixSessionLogEntry)Assert.Single(read.Output).BaseObject).Message);
            Assert.True(again.Output.Count >= 2);
            Assert.Empty(cleared.Errors);
            Assert.Empty(cleared.Output);
        }
        finally
        {
            shell.Invoke("Stop-ZbxPassiveAgent probe -Confirm:$false");
        }
    }
}
