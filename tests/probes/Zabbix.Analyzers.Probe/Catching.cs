// This Source Code Form is subject to the terms of the Mozilla Public
// License, v. 2.0. Copyright (c) 2026 schroeder247. SPDX-License-Identifier: MPL-2.0
namespace Community.Zabbix.Analyzers;

/// <summary>
/// No catch-all. CA1031 rejects the plain form; the FILTERED form that logs and swallows passes CA1031
/// (measured), so DesignConformanceTests' Roslyn rule is what rejects it: the line marked <c>rule:</c> is the one
/// that rule must find, and the build must not (it is the gap the rule closes).
/// </summary>
public static class CatchingProbe
{
    /// <summary>Swallows two ways.</summary>
    /// <param name="work">What to run.</param>
    /// <param name="log">Where the failure goes.</param>
    public static void Swallow(Action work, Func<Exception, bool> log)
    {
        ArgumentNullException.ThrowIfNull(work);
        ArgumentNullException.ThrowIfNull(log);
        try { work(); }
        catch (Exception) { }                                                               // expect: CA1031
        try { work(); }
        catch (Exception failure) when (log(failure)) { }                                   // rule: filtered-catch-all
        try { work(); }
        catch (Exception failure) when (failure is InvalidOperationException or ArgumentException) { }
    }
}
