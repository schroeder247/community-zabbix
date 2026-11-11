// This Source Code Form is subject to the terms of the Mozilla Public
// License, v. 2.0. Copyright (c) 2026 schroeder247. SPDX-License-Identifier: MPL-2.0
namespace Community.Zabbix.Analyzers;

/// <summary>No raw thread, SemaphoreSlim throttle or clockless delay compiles in a library.</summary>
public static class ThreadsProbe
{
    /// <summary>Starts a thread, throttles and sleeps.</summary>
    /// <param name="cancellationToken">Cancels the wait.</param>
    /// <returns>When done.</returns>
    public static async Task RunAsync(CancellationToken cancellationToken)
    {
        var thread = new Thread(() => { });                                                // expect: RS0030
        thread.Start();                                                                     // expect: RS0030
        using var gate = new SemaphoreSlim(1);                                              // expect: RS0030
        await Task.Delay(TimeSpan.FromSeconds(1), cancellationToken).ConfigureAwait(false); // expect: RS0030
    }
}
