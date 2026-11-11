// This Source Code Form is subject to the terms of the Mozilla Public
// License, v. 2.0. Copyright (c) 2026 schroeder247. SPDX-License-Identifier: MPL-2.0
using System.Diagnostics;

namespace Community.Zabbix.Analyzers;

/// <summary>
/// The less obvious doors past the ban list. Every marked line does what the design standard forbids in a library: block on a
/// task, read or schedule on the wall clock, start raw thread-pool work, write to the console. These are the twins a ban
/// list naming only <c>Task.Wait</c>, <c>Task&lt;T&gt;.Result</c> and the two <c>TaskAwaiter.GetResult</c>s would miss
/// (their <c>ValueTask</c>, <c>ConfigureAwait</c> and <c>WaitAll/WaitAny</c> twins, <c>new Stopwatch()</c>, the two timers,
/// the thread pool, blocking primitives and <c>Console</c>); the ban list names each of them, so each mark is an
/// <c>expect:</c>. <c>AdversarialDesignConformanceTests.Every_ban_list_gap_is_a_build_error</c> keeps holding
/// each of these lines to its id.
/// </summary>
public static class AdversarialProbe
{
    /// <summary>Blocks, times and schedules through the doors the ban list leaves open.</summary>
    /// <param name="work">A task.</param>
    /// <param name="value">A value task.</param>
    /// <param name="plain">A value task without a result.</param>
    /// <param name="many">Tasks.</param>
    /// <returns>A number.</returns>
    public static long Block(Task<int> work, ValueTask<int> value, ValueTask plain, Task[] many)
    {
        ArgumentNullException.ThrowIfNull(work);
        ArgumentNullException.ThrowIfNull(many);
        long total = value.Result;                                                              // expect: RS0030
        plain.GetAwaiter().GetResult();                                                         // expect: RS0030
        total += work.ConfigureAwait(false).GetAwaiter().GetResult();                           // expect: RS0030
        Task.WaitAll(many);                                                                     // expect: RS0030
        total += Task.WaitAny(many);                                                            // expect: RS0030
        var watch = new Stopwatch();                                                            // expect: RS0030
        total += watch.ElapsedTicks;
        using var timer = new Timer(_ => { }, null, TimeSpan.Zero, TimeSpan.FromSeconds(1));    // expect: RS0030
        using var legacy = new System.Timers.Timer(1000);                                       // expect: RS0030
        _ = ThreadPool.QueueUserWorkItem(_ => { });                                             // expect: RS0030
        using var gate = new ManualResetEventSlim();
        _ = gate.Wait(TimeSpan.FromSeconds(1));                                                 // expect: RS0030
        _ = SpinWait.SpinUntil(() => true, 10);                                                 // expect: RS0030
        Console.WriteLine(total);                                                               // expect: RS0030
        return total;
    }
}
