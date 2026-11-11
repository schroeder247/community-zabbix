// This Source Code Form is subject to the terms of the Mozilla Public
// License, v. 2.0. Copyright (c) 2026 schroeder247. SPDX-License-Identifier: MPL-2.0
using System.Diagnostics;

namespace Community.Zabbix.Analyzers;

/// <summary>No wall-clock read compiles in a library: time is a TimeProvider (BannedSymbols.txt).</summary>
public static class WallClockProbe
{
    /// <summary>Reads the wall clock four ways.</summary>
    /// <returns>Nothing useful.</returns>
    public static long Read()
    {
        DateTime utc = DateTime.UtcNow;                                                     // expect: RS0030
        DateTimeOffset offset = DateTimeOffset.UtcNow;                                      // expect: RS0030
        var watch = Stopwatch.StartNew();                                                   // expect: RS0030
        return utc.Ticks + offset.Ticks + watch.ElapsedTicks + Environment.TickCount64;     // expect: RS0030
    }
}
