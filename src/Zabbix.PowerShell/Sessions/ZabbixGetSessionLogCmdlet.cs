// This Source Code Form is subject to the terms of the Mozilla Public
// License, v. 2.0. Copyright (c) 2026 schroeder247. SPDX-License-Identifier: MPL-2.0
using System.Management.Automation;
using Community.Zabbix.PowerShell.Diagnostics;
using Microsoft.Extensions.Logging;

namespace Community.Zabbix.PowerShell.Sessions;

/// <summary>
/// Provides the base of every <c>Get-Zbx&lt;Session&gt;Log</c> cmdlet, which reads the session's ring buffer of log lines
/// (its loops run off any command's pipeline, so their lines wait there), newest last, without changing it. Clearing it
/// is <see cref="ZabbixClearSessionLogCmdlet"/>, so a <c>Get-</c> never changes state.
/// </summary>
public abstract class ZabbixGetSessionLogCmdlet : ZabbixSessionCmdlet
{
    /// <summary>Only the last N entries.</summary>
    [Parameter]
    [ValidateRange(1, 1_000_000)]
    public int? Tail { get; set; }

    /// <summary>Only entries at or above this level.</summary>
    [Parameter]
    public LogLevel? Level { get; set; }

    /// <inheritdoc/>
    protected override void ProcessRecord()
    {
        if (Found() is not IZabbixLoggedSession logged) return;
        IEnumerable<ZabbixSessionLogEntry> entries = logged.Logs.Snapshot();
        if (Level is { } level) entries = entries.Where(entry => entry.Level >= level);
        List<ZabbixSessionLogEntry> ordered = [.. entries];
        if (Tail is { } tail && ordered.Count > tail) ordered = ordered[^tail..];
        foreach (ZabbixSessionLogEntry entry in ordered) WriteObject(entry);
    }
}
