// This Source Code Form is subject to the terms of the Mozilla Public
// License, v. 2.0. Copyright (c) 2026 schroeder247. SPDX-License-Identifier: MPL-2.0
using System.Management.Automation;
using Community.Zabbix.PowerShell.Sessions;

namespace Community.Zabbix.PowerShell.Tests;

/// <summary>
/// The one primitive every background-loop scriptblock bridge shares (discovery sources, agent handlers): a
/// dedicated runspace opened once, invoked serially. The handlers that call it are called concurrently (a passive agent
/// answers each connection on its own), so the serialization is an awaited queue: the concurrency tests hold the runspace
/// with a script blocked on an event the test owns, and every wait is on a signal, never on a duration.
/// </summary>
public sealed class ZabbixScriptRunspaceTests
{
    private static readonly TimeSpan Failsafe = TimeSpan.FromSeconds(60);

    // Holds the runspace until the test sets $release, after signalling $held: the caller knows the next calls queue.
    private const string Hold = "param($held, $release) $null = $held.TrySetResult(); $null = $release.Wait(); $script:order = [Collections.Generic.List[string]]::new(); $script:order.Add('holder')";

    [Fact]
    public async Task Invoke_runs_the_script_and_returns_its_output()
    {
        var runspace = new ZabbixScriptRunspace();
        try
        {
            PSDataCollection<PSObject> output = await runspace.InvokeAsync("param($x) $x + 1", [41], CancellationToken.None);

            Assert.Equal(42, output[0].BaseObject);
        }
        finally
        {
            await runspace.DisposeAsync();
        }
    }

    [Fact]
    public async Task A_handler_runs_with_the_module_imported()
    {
        // A handler can call the module's own commands: without the module imported, Get-ZbxHost would answer "The term
        // 'Get-ZbxHost' is not recognized".
        var runspace = new ZabbixScriptRunspace();
        try
        {
            PSDataCollection<PSObject> output = await runspace.InvokeAsync("(Get-Command Get-ZbxHost).Source", [], CancellationToken.None);

            Assert.Equal("Community.Zabbix", output[0].BaseObject);
        }
        finally
        {
            await runspace.DisposeAsync();
        }
    }

    [Fact]
    public async Task Script_scope_state_persists_across_calls_because_it_is_one_runspace_not_a_pool()
    {
        var runspace = new ZabbixScriptRunspace();
        try
        {
            await runspace.InvokeAsync("if (-not (Test-Path variable:script:count)) { $script:count = 0 }; $script:count++", null, CancellationToken.None);

            PSDataCollection<PSObject> second = await runspace.InvokeAsync("$script:count++; $script:count", null, CancellationToken.None);

            Assert.Equal(2, second[0].BaseObject);
        }
        finally
        {
            await runspace.DisposeAsync();
        }
    }

    [Fact]
    public async Task An_already_cancelled_token_throws_before_the_script_runs()
    {
        var runspace = new ZabbixScriptRunspace();
        try
        {
            using var cancelled = new CancellationTokenSource();
            await cancelled.CancelAsync();

            await Assert.ThrowsAnyAsync<OperationCanceledException>(() => runspace.InvokeAsync("$script:ran = $true", null, cancelled.Token));
            PSDataCollection<PSObject> check = await runspace.InvokeAsync("Test-Path variable:script:ran", null, CancellationToken.None);
            Assert.Equal(false, check[0].BaseObject);
        }
        finally
        {
            await runspace.DisposeAsync();
        }
    }

    [Fact]
    public async Task A_terminating_error_bubbles_out_of_Invoke_carrying_its_message()
    {
        var runspace = new ZabbixScriptRunspace();
        try
        {
            Exception failure = await Assert.ThrowsAnyAsync<Exception>(() => runspace.InvokeAsync("throw 'boom'", null, CancellationToken.None));

            Assert.Contains("boom", failure.Message, StringComparison.Ordinal);
        }
        finally
        {
            await runspace.DisposeAsync();
        }
    }

    [Fact]
    public async Task A_non_terminating_error_left_on_the_error_stream_throws_carrying_its_message()
    {
        var runspace = new ZabbixScriptRunspace();
        try
        {
            ZabbixScriptRunspaceException failure = await Assert.ThrowsAsync<ZabbixScriptRunspaceException>(
                () => runspace.InvokeAsync("Write-Error 'boom'", null, CancellationToken.None));

            Assert.Contains("boom", failure.Message, StringComparison.Ordinal);
        }
        finally
        {
            await runspace.DisposeAsync();
        }
    }

    [Fact]
    public async Task DisposeAsync_closes_the_runspace_so_a_further_Invoke_fails()
    {
        var runspace = new ZabbixScriptRunspace();

        await runspace.DisposeAsync();

        await Assert.ThrowsAnyAsync<Exception>(() => runspace.InvokeAsync("1", null, CancellationToken.None));
    }

    [Fact]
    public async Task A_call_cancelled_while_it_waits_for_the_runspace_ends_at_once_and_never_runs_while_the_others_run_in_order()
    {
        var runspace = new ZabbixScriptRunspace();
        using var release = new ManualResetEventSlim();
        try
        {
            var held = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            Task<PSDataCollection<PSObject>> holder = runspace.InvokeAsync(Hold, [held, release], CancellationToken.None);
            await held.Task.WaitAsync(Failsafe, TestContext.Current.CancellationToken);
            using var cancel = new CancellationTokenSource();
            Task<PSDataCollection<PSObject>> first = runspace.InvokeAsync("$script:order.Add('first')", null, CancellationToken.None);
            Task<PSDataCollection<PSObject>> cancelled = runspace.InvokeAsync("$script:order.Add('cancelled')", null, cancel.Token);
            Task<PSDataCollection<PSObject>> last = runspace.InvokeAsync("$script:order.Add('last'); $script:order -join ','", null, CancellationToken.None);
            Assert.False(cancelled.IsCompleted);

            await cancel.CancelAsync();

            // Ended while the holder still holds the runspace: the wait itself was cancelled, not the script after it.
            await Assert.ThrowsAnyAsync<OperationCanceledException>(() => cancelled.WaitAsync(Failsafe, TestContext.Current.CancellationToken));
            Assert.False(holder.IsCompleted);
            release.Set();
            PSDataCollection<PSObject>[] outputs = await Task.WhenAll(holder, first, last).WaitAsync(Failsafe, TestContext.Current.CancellationToken);
            Assert.Equal("holder,first,last", Assert.Single(outputs[2]).BaseObject);
        }
        finally
        {
            release.Set();
            await runspace.DisposeAsync();
        }
    }

    [Fact]
    public async Task Many_concurrent_calls_each_run_once_one_at_a_time_and_all_finish()
    {
        var runspace = new ZabbixScriptRunspace();
        try
        {
            await runspace.InvokeAsync("$script:running = 0; $script:most = 0; $script:runs = 0", null, CancellationToken.None);
            const string Probe = "$script:running++; $script:most = [Math]::Max($script:most, $script:running); $script:runs++; $script:running--";

            await Task.WhenAll(Enumerable.Range(0, 16).Select(_ => Task.Run(() => runspace.InvokeAsync(Probe, null, CancellationToken.None))))
                .WaitAsync(Failsafe, TestContext.Current.CancellationToken);

            PSDataCollection<PSObject> counts = await runspace.InvokeAsync("$script:runs; $script:most", null, CancellationToken.None);
            Assert.Equal([16, 1], counts.Select(count => (int)count.BaseObject));
        }
        finally
        {
            await runspace.DisposeAsync();
        }
    }

    [Fact]
    public async Task Cancelling_a_running_script_stops_it_and_reports_a_cancellation_not_a_failure()
    {
        var runspace = new ZabbixScriptRunspace();
        try
        {
            var started = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            using var cancel = new CancellationTokenSource();
            // The script under test waits for ever; Start-Sleep is a command the pipeline's stop interrupts.
            Task<PSDataCollection<PSObject>> running = runspace.InvokeAsync(
                "param($started) $null = $started.TrySetResult(); Start-Sleep -Seconds 3600; $script:finished = $true", [started], cancel.Token);
            await started.Task.WaitAsync(Failsafe, TestContext.Current.CancellationToken);

            await cancel.CancelAsync();

            await Assert.ThrowsAnyAsync<OperationCanceledException>(() => running.WaitAsync(Failsafe, TestContext.Current.CancellationToken));
            PSDataCollection<PSObject> check = await runspace.InvokeAsync("Test-Path variable:script:finished", null, CancellationToken.None);
            Assert.Equal(false, check[0].BaseObject);
        }
        finally
        {
            await runspace.DisposeAsync();
        }
    }
}
