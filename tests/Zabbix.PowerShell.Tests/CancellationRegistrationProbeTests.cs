// This Source Code Form is subject to the terms of the Mozilla Public
// License, v. 2.0. Copyright (c) 2026 schroeder247. SPDX-License-Identifier: MPL-2.0
namespace Community.Zabbix.PowerShell.Tests;

/// <summary>
/// Pins the primitive behind a flake of the server-side abort signal (<see cref="FakeZabbix.Aborted"/>): a
/// <see cref="CancellationTokenSource"/> runs its callbacks newest first, and a registration disposed before the
/// cancelling thread reaches it never runs. A handler that registers its own signal and then awaits a wait on the same
/// token (whose registration is newer) can therefore resume from the wait's callback and dispose its signal first.
/// </summary>
public sealed class CancellationRegistrationProbeTests
{
    [Fact]
    public async Task A_registration_disposed_by_the_resumed_waiter_before_cancel_reaches_it_never_runs()
    {
        using var source = new CancellationTokenSource();
        bool signalled = false;
        CancellationTokenRegistration signal = source.Token.Register(() => signalled = true);
        var waiterResumed = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        // The newer registration plays the wait's: it runs first, and the "handler" resumes on another thread and
        // disposes its own signal while the cancelling thread is still inside this callback.
        using CancellationTokenRegistration wait = source.Token.Register(() => Task.Run(() => { signal.Dispose(); waiterResumed.SetResult(); }).Wait());

        await source.CancelAsync();

        await waiterResumed.Task.WaitAsync(TimeSpan.FromSeconds(30), TestContext.Current.CancellationToken);
        Assert.True(source.IsCancellationRequested);
        Assert.False(signalled);
    }
}
