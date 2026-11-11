// This Source Code Form is subject to the terms of the Mozilla Public
// License, v. 2.0. Copyright (c) 2026 schroeder247. SPDX-License-Identifier: MPL-2.0
using System.Management.Automation;

namespace Community.Zabbix.PowerShell.Tests;

/// <summary>One bridge over the async API with the stop token.</summary>
public sealed class CancellationTests
{
    [Fact]
    public async Task Ctrl_c_reaches_the_socket()
    {
        await using FakeZabbix server = await FakeZabbix.StartAsync();
        server.On("host.get", "[]").Stall("host.get");
        using Shell shell = Shell.Open();
        shell.Connect(server);
        TaskCompletionSource<string> arrived = server.ExpectArrival();

        await using Call call = shell.Start("Get-ZbxHost");
        await arrived.Task.WaitAsync(TimeSpan.FromSeconds(30), TestContext.Current.CancellationToken);

        await call.StopAsync().WaitAsync(TimeSpan.FromSeconds(30), TestContext.Current.CancellationToken);

        // The bound is a failure guard, not a wait: the observable is the server seeing the request aborted.
        await server.Aborted.Task.WaitAsync(TimeSpan.FromSeconds(30), TestContext.Current.CancellationToken);
        await Assert.ThrowsAsync<PipelineStoppedException>(() => call.Running.WaitAsync(TimeSpan.FromSeconds(30), TestContext.Current.CancellationToken));
        Assert.Equal(PSInvocationState.Stopped, call.InvocationStateInfo.State);
    }

    [Fact]
    public async Task A_downstream_command_that_has_enough_cancels_the_read()
    {
        using Shell shell = Shell.Open();
        var cancelled = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);

        Run run = shell.Invoke("Invoke-ZbxTestStream -Cancelled $cancelled | Select-Object -First 3", new Dictionary<string, object?> { ["cancelled"] = cancelled });

        Assert.Equal([0, 1, 2], run.Output.Select(o => (int)o.BaseObject));
        Assert.Empty(run.Errors);
        await cancelled.Task.WaitAsync(TimeSpan.FromSeconds(30), TestContext.Current.CancellationToken);
    }
}
