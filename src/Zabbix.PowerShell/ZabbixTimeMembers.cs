// This Source Code Form is subject to the terms of the Mozilla Public
// License, v. 2.0. Copyright (c) 2026 schroeder247. SPDX-License-Identifier: MPL-2.0
using System.Management.Automation;

namespace Community.Zabbix.PowerShell;

/// <summary>
/// Provides the <c>Time</c> and <c>LastTime</c> members the module's type data adds to a read record that carries a clock
/// (<c>Clock</c>, <c>LastClock</c>): the same instant as a local <see cref="DateTime"/>, as <c>Get-WinEvent</c>'s
/// <c>TimeCreated</c> is, so <c>Get-ZbxHistory | Where-Object Time -gt (Get-Date).AddHours(-1)</c> compares dates.
/// </summary>
/// <remarks>
/// Code properties (<c>CodeProperty</c> in <c>Community.Zabbix.types.ps1xml</c>), not script properties: they run no script, so
/// they work in a constrained language session. The record's own <c>Clock</c> keeps the wire's instant.
/// </remarks>
public static class ZabbixTimeMembers
{
    /// <summary>The record's <c>Clock</c> as a local time; <see langword="null"/> when unset.</summary>
    /// <param name="instance">The record.</param>
    /// <returns>The local time, or <see langword="null"/>.</returns>
    public static DateTime? Time(PSObject instance) => Of(instance, "Clock");

    /// <summary>The record's <c>LastClock</c> as a local time; <see langword="null"/> when unset or never (Zabbix's 0).</summary>
    /// <param name="instance">The record.</param>
    /// <returns>The local time, or <see langword="null"/>.</returns>
    public static DateTime? LastTime(PSObject instance) => Of(instance, "LastClock");

    private static DateTime? Of(PSObject instance, string member)
    {
        ArgumentNullException.ThrowIfNull(instance);
        return instance.Properties[member]?.Value is DateTimeOffset time && time.ToUnixTimeSeconds() > 0 ? time.LocalDateTime : null;
    }
}
