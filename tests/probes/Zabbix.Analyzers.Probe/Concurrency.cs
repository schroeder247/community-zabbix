// This Source Code Form is subject to the terms of the Mozilla Public
// License, v. 2.0. Copyright (c) 2026 schroeder247. SPDX-License-Identifier: MPL-2.0
namespace Community.Zabbix.Analyzers;

/// <summary>No inline-continuing completion, unwrapped StartNew, constructed HttpClient, process exit or seeded Random compiles in a library.</summary>
public static class ConcurrencyProbe
{
    /// <summary>Uses each banned shape once.</summary>
    /// <returns>A number.</returns>
    public static int Run()
    {
        var signal = new TaskCompletionSource();                                            // expect: RS0030
        var typed = new TaskCompletionSource<int>();                                        // expect: RS0030
        Task started = Task.Factory.StartNew(() => { });                                    // expect: RS0030, CA2008
        using var http = new HttpClient();                                                  // expect: RS0030
        var random = new Random(7);                                                         // expect: RS0030
        if (signal.Task.IsCompleted && typed.Task.IsCompleted && started.IsCompleted) Environment.Exit(1);   // expect: RS0030
        return random.Next() + http.GetHashCode();                                          // expect: CA5394
    }
}
