// This Source Code Form is subject to the terms of the Mozilla Public
// License, v. 2.0. Copyright (c) 2026 schroeder247. SPDX-License-Identifier: MPL-2.0
using System.Management.Automation;
using Community.Zabbix.PowerShell.Commands;
using Microsoft.Extensions.Time.Testing;

namespace Community.Zabbix.PowerShell.Tests;

/// <summary>
/// Ctrl+C (<c>PowerShell.StopAsync</c>) at every await of a call. The stalled request is the
/// observable: the server sees it aborted, the pipeline ends with <see cref="PipelineStoppedException"/>, and the runspace
/// and its connection stay usable.
/// </summary>
public sealed class AdversarialCancellationTests
{
    private static readonly TimeSpan Failsafe = TimeSpan.FromSeconds(30);

    /// <summary>Waits for one named step of a scenario within <see cref="Failsafe"/>; a timeout names the step.</summary>
    private static async Task<T> Step<T>(Task<T> step, string what)
    {
        try { return await step.WaitAsync(Failsafe, TestContext.Current.CancellationToken); }
        catch (TimeoutException) { throw new TimeoutException($"waited {Failsafe} for {what}"); }
    }

    private static async Task Step(Task step, string what)
    {
        try { await step.WaitAsync(Failsafe, TestContext.Current.CancellationToken); }
        catch (TimeoutException) { throw new TimeoutException($"waited {Failsafe} for {what}"); }
    }

    // Well inside the resilience pipeline's 30 s total request timeout, the only other thing that ends a blocked dispose.
    private static readonly TimeSpan StopBound = TimeSpan.FromSeconds(10);

    /// <summary>
    /// Each row: the command, the method stalled, and the method answered just before it (whose answer arms the arrival
    /// signal, so the wait is for the stalled request itself and never races an earlier one), or <see langword="null"/>
    /// when the stalled method is the command's first request.
    /// </summary>
    public static TheoryData<string, string, string?> Awaits => new()
    {
        { "Get-ZbxHost", "host.get", null },
        { "Get-ZbxHost -Name web-01", "host.get", null },
        { "Get-ZbxHost -CountOutput", "host.get", null },
        { "Get-ZbxHost -Group 'Linux servers'", "hostgroup.get", null },
        { "Get-ZbxHost -Group 'Linux servers'", "host.get", "hostgroup.get" },
        { "Get-ZbxItem -Host web-01", "item.get", "host.get" },
        { "Remove-ZbxHost web-01 -Confirm:$false", "host.get", null },
        { "Remove-ZbxHost web-01 -Confirm:$false", "host.delete", "host.get" },
        { "Remove-ZbxHost 10084 -Confirm:$false", "host.delete", null },
        { "New-ZbxHostGroup -Name g", "hostgroup.create", null },
        { "New-ZbxHostGroup -Name g", "hostgroup.get", "hostgroup.create" },
        { "Set-ZbxHostGroup -InputObject g -Name h", "hostgroup.get", null },
        { "Set-ZbxHostGroup -InputObject 5 -Name h", "hostgroup.update", null },
        { "Set-ZbxHostGroup -InputObject 5 -Name h -PassThru", "hostgroup.get", "hostgroup.update" },
        { "Disable-ZbxHost web-01 -Force -Confirm:$false", "host.update", "host.get" },
        { "Invoke-ZbxApi -Method host.get -Body @{}", "host.get", null },
    };

    [Theory]
    [MemberData(nameof(Awaits))]
    public async Task Ctrl_c_at_any_await_aborts_that_request_at_the_server_and_stops_the_pipeline(string script, string stalled, string? before)
    {
        await using FakeZabbix server = await FakeZabbix.StartAsync();
        server.On("host.get", FakeZabbix.Hosts(("10084", "web-01"))).On("hostgroup.get", """[{"groupid":"5","name":"g"}]""")
              .On("host.delete", """{"hostids":["10084"]}""").On("hostgroup.create", """{"groupids":["5"]}""")
              .On("hostgroup.update", """{"groupids":["5"]}""").On("host.update", """{"hostids":["10084"]}""").On("item.get", "[]");
        using Shell shell = Shell.Open();
        shell.Connect(server);
        TaskCompletionSource<string> arrived = Arm(server, stalled, before);

        await using Call call = shell.Start(script);
        Assert.Equal(stalled, await Step(arrived.Task, $"the server to see {stalled}"));

        await Step(call.StopAsync(), "the pipeline to stop");

        // Each step names itself so a failure says which.
        await Step(server.Aborted.Task, "the server to see the request aborted");
        await Assert.ThrowsAsync<PipelineStoppedException>(() => Step(call.Running, "the pipeline's output to fault"));
        Assert.Equal(PSInvocationState.Stopped, call.InvocationStateInfo.State);
        server.Release(stalled);
        Run after = shell.Invoke("Get-ZbxApiVersion");
        Assert.Empty(after.Errors);
        Assert.Equal(new Version(7, 0, 31), Assert.Single(after.Output).BaseObject);
    }

    [Fact]
    public async Task Ctrl_c_during_Connect_ZbxServer_returns_the_prompt_without_waiting_for_the_version_probe()
    {
        // StopAsync never returns when the pipeline thread is in
        // ConnectZbxServerCommand.ProcessRecord's finally → ZabbixConnection.Dispose → Release → ServiceProvider.Dispose →
        // Polly ResiliencePipelineRegistry.Dispose, which blocks (sync-over-async) until the resilience pipeline's
        // in-flight execution ends: the apiinfo.version read a stopped Connect-ZbxServer can leave running. On the system clock that is the
        // pipeline's total request timeout; on this runspace's fake clock it is never. The last lease must not dispose
        // the container synchronously on the pipeline thread. (On a FakeTimeProvider clock the registry's dispose never
        // returns even after the request is answered, so this test runs on the system clock: the stop must return well
        // inside the pipeline's 30 s total timeout while the request is still stalled.)
        await using FakeZabbix server = await FakeZabbix.StartAsync();
        server.Stall("apiinfo.version");
        using Shell shell = Shell.Open();
        TaskCompletionSource<string> arrived = server.ExpectArrival();

        await using Call call = shell.Start($"Connect-ZbxServer -Uri '{server.Endpoint}' -ApiToken (ConvertTo-SecureString 't' -AsPlainText -Force)");
        await arrived.Task.WaitAsync(Failsafe, TestContext.Current.CancellationToken);
        try
        {
            await call.StopAsync().WaitAsync(StopBound, TestContext.Current.CancellationToken);

            await Assert.ThrowsAsync<PipelineStoppedException>(() => call.Running.WaitAsync(Failsafe, TestContext.Current.CancellationToken));
            Assert.Null(shell.Context.Current);
            Assert.Empty(shell.Context.Servers);
        }
        finally
        {
            server.Release("apiinfo.version");
        }
    }

    [Fact]
    public async Task A_connect_stopped_during_its_login_returns_without_waiting_for_the_login()
    {
        // The same blocked dispose, second path: the session login runs on its own lifetime (ZabbixSessionCredential, CancellationToken.None),
        // so Ctrl+C on Connect-ZbxServer's proof of the credential leaves it in flight; the connection the stopped command
        // disposes drops the last lease and disposes the container on the pipeline thread, blocking on the resilience
        // registry until the login ends.
        await using FakeZabbix server = await FakeZabbix.StartAsync();
        server.On("user.login", """{"userid":"1","username":"Admin","mfaid":0,"sessionid":"session-1"}""").Stall("user.login");
        using Shell shell = Shell.Open();
        TaskCompletionSource<string> arrived = server.ExpectArrival();
        await using Call call = shell.Start($"Connect-ZbxServer -Uri '{server.Endpoint}' -Credential ([pscredential]::new('Admin', (ConvertTo-SecureString 'zabbix' -AsPlainText -Force)))");
        Assert.Equal("apiinfo.version", await arrived.Task.WaitAsync(Failsafe, TestContext.Current.CancellationToken));
        arrived = server.ExpectArrival();
        Assert.Equal("user.login", await arrived.Task.WaitAsync(Failsafe, TestContext.Current.CancellationToken));
        try
        {
            await call.StopAsync().WaitAsync(StopBound, TestContext.Current.CancellationToken);

            await Assert.ThrowsAsync<PipelineStoppedException>(() => call.Running.WaitAsync(Failsafe, TestContext.Current.CancellationToken));
            Assert.Empty(shell.Context.Servers);
        }
        finally
        {
            server.Release("user.login");
        }
    }

    [Fact]
    public async Task On_a_fake_clock_a_connection_disposed_after_its_request_was_answered_finishes_disposing()
    {
        // In the Ctrl+C-during-Connect scenario on a FakeTimeProvider clock, the pipeline thread can stay in
        // Polly ResiliencePipelineRegistry.Dispose (ServiceProviderEngineScope.Dispose ← ZabbixConnection.Release) for good,
        // even after the stalled apiinfo.version request is released and answered; on the system clock the same dispose
        // returns once the request ends. A container composed by AddZabbixClient on an injected clock (the SDK's own testing
        // story) must not hang whoever disposes it; it also shows as a nondeterministic hang of a Watch-ZbxItem
        // + Disconnect-ZbxServer test on a fake clock (the watcher's first tick in flight at the disconnect).
        await using FakeZabbix server = await FakeZabbix.StartAsync();
        server.Stall("apiinfo.version");
        using Shell shell = Shell.Open();
        shell.Context.Time = new FakeTimeProvider();
        TaskCompletionSource<string> arrived = server.ExpectArrival();
        // Both disposals are bounded (Call, Shell): a pipeline thread still stuck in the dispose fails this test, naming it.
        await using Call call = shell.Start($"Connect-ZbxServer -Uri '{server.Endpoint}' -ApiToken (ConvertTo-SecureString 't' -AsPlainText -Force)");
        await arrived.Task.WaitAsync(Failsafe, TestContext.Current.CancellationToken);
        Task stopping = call.StopAsync();
        server.Release("apiinfo.version");

        await stopping.WaitAsync(Failsafe, TestContext.Current.CancellationToken);

        // The answer and the stop race, and either may win: what this holds is that the pipeline ends rather than hangs.
        try
        {
            await call.Running.WaitAsync(Failsafe, TestContext.Current.CancellationToken);
        }
        catch (PipelineStoppedException)
        {
            // The stop arrived first.
        }
    }

    [Fact]
    public async Task A_completion_past_its_deadline_cancels_its_fetch_at_the_server_and_offers_nothing()
    {
        // The completer's fetch owns its deadline: a completion that gives up returns at once, and the request it
        // started is cancelled at the wire, not left running against a stalled server until the HTTP timeout.
        await using FakeZabbix server = await FakeZabbix.StartAsync();
        using Shell shell = Shell.Open();
        shell.Connect(server);
        server.Stall("host.get");
        var clock = new FakeTimeProvider();
        shell.Context.Time = clock;
        TaskCompletionSource<string> arrived = server.ExpectArrival();

        Task<CommandCompletion> completing = Task.Run(() => shell.Complete("Get-ZbxHost -Name we"), TestContext.Current.CancellationToken);
        await arrived.Task.WaitAsync(Failsafe, TestContext.Current.CancellationToken);
        clock.Advance(ZabbixCompletion.Deadline);

        CommandCompletion completion = await completing.WaitAsync(Failsafe, TestContext.Current.CancellationToken);
        await server.Aborted.Task.WaitAsync(Failsafe, TestContext.Current.CancellationToken);
        Assert.Empty(completion.CompletionMatches);
    }

    [Fact]
    public async Task Ctrl_c_during_Connect_ZbxServer_aborts_the_version_probe_at_the_server()
    {
        // A stop must abort the apiinfo.version request at the server, not only stop the pipeline (the request would
        // otherwise run to the 30 s failsafe): if ZabbixClient.GetApiVersionAsync reads the version on its own lifetime
        // (CancellationToken.None, shared with other callers), only the caller's WAIT is cancelled, yet on Connect-ZbxServer nobody else can share
        // it (the connection is private to the call and is disposed on the stop), so the request keeps running against a
        // disposed container until the resilience pipeline's deadline.
        await using FakeZabbix server = await FakeZabbix.StartAsync();
        server.Stall("apiinfo.version");
        using Shell shell = Shell.Open();
        TaskCompletionSource<string> arrived = server.ExpectArrival();

        await using Call call = shell.Start($"Connect-ZbxServer -Uri '{server.Endpoint}' -ApiToken (ConvertTo-SecureString 't' -AsPlainText -Force)");
        await arrived.Task.WaitAsync(Failsafe, TestContext.Current.CancellationToken);
        await call.StopAsync().WaitAsync(Failsafe, TestContext.Current.CancellationToken);

        await server.Aborted.Task.WaitAsync(Failsafe, TestContext.Current.CancellationToken);
        await Assert.ThrowsAsync<PipelineStoppedException>(() => call.Running.WaitAsync(Failsafe, TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task Ctrl_c_while_a_watch_stream_waits_for_its_next_value_stops_the_read_and_leaves_the_watch_running()
    {
        await using FakeZabbix server = await FakeZabbix.StartAsync();
        server.On("item.get", "[]").On("host.get", FakeZabbix.Hosts(("10084", "web-01")));
        using Shell shell = Shell.Open();
        shell.Context.Time = new FakeTimeProvider();
        shell.Connect(server);
        Assert.Empty(shell.Invoke("Watch-ZbxItem probe web-01").Errors);

        await using Call call = shell.Start("Receive-ZbxWatch probe");
        await call.StopAsync().WaitAsync(Failsafe, TestContext.Current.CancellationToken);

        await Assert.ThrowsAsync<PipelineStoppedException>(() => call.Running.WaitAsync(Failsafe, TestContext.Current.CancellationToken));
        Run found = shell.Invoke("Get-ZbxWatch probe");
        Assert.Empty(found.Errors);
        Assert.IsType<ZabbixWatch>(Assert.Single(found.Output).BaseObject);
        shell.Invoke("Stop-ZbxWatch probe -Confirm:$false");
    }

    [Fact]
    public async Task Ctrl_c_during_a_trapper_send_stops_the_pipeline()
    {
        await using FakeZabbix api = await FakeZabbix.StartAsync();
        using var gate = new ManualResetEventSlim();
        var reached = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        await using FakeTrapper trapper = FakeTrapper.Start();
        trapper.On("sender data", _ =>
        {
            reached.TrySetResult();
            gate.Wait(TestContext.Current.CancellationToken);
            return FakeTrapper.Success(1, 0);
        });
        using Shell shell = Shell.Open();
        shell.Connect(api, $"-Trapper 127.0.0.1:{trapper.Port}");

        await using Call call = shell.Start("Send-ZbxData web-01 agent.ping 1 -Confirm:$false");
        await reached.Task.WaitAsync(Failsafe, TestContext.Current.CancellationToken);
        try
        {
            await call.StopAsync().WaitAsync(Failsafe, TestContext.Current.CancellationToken);

            await Assert.ThrowsAsync<PipelineStoppedException>(() => call.Running.WaitAsync(Failsafe, TestContext.Current.CancellationToken));
        }
        finally
        {
            gate.Set();
        }
    }

    // Arms the arrival signal for the stalled request: directly when it is the first request, else from inside the
    // previous request's own answer (which runs before the client can send the next request).
    internal static TaskCompletionSource<string> Arm(FakeZabbix server, string stalled, string? before)
    {
        if (before is null)
        {
            server.Stall(stalled);
            return server.ExpectArrival();
        }

        var armed = new TaskCompletionSource<string>(TaskCreationOptions.RunContinuationsAsynchronously);
        TaskCompletionSource<string>? signal = null;
        string answer = before switch
        {
            "host.get" => FakeZabbix.Hosts(("10084", "web-01")),
            "hostgroup.get" => """[{"groupid":"5","name":"g"}]""",
            "hostgroup.create" or "hostgroup.update" => """{"groupids":["5"]}""",
            _ => "[]",
        };
        server.On(before, parameters =>
        {
            if (signal is null)
            {
                server.Stall(stalled);
                signal = server.ExpectArrival();
                _ = ForwardAsync(signal.Task, armed);
            }
            return answer;
        });
        return armed;
    }

    private static async Task ForwardAsync(Task<string> from, TaskCompletionSource<string> to) => to.TrySetResult(await from);
}
