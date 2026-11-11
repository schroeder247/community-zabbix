// This Source Code Form is subject to the terms of the Mozilla Public
// License, v. 2.0. Copyright (c) 2026 schroeder247. SPDX-License-Identifier: MPL-2.0
using Community.Zabbix.PowerShell.Diagnostics;

namespace Community.Zabbix.PowerShell.Sessions;

/// <summary>
/// Provides the base of every <c>Clear-Zbx&lt;Session&gt;Log</c> cmdlet, which empties the session's ring buffer of log
/// lines.
/// </summary>
public abstract class ZabbixClearSessionLogCmdlet : ZabbixSessionCmdlet
{
    /// <inheritdoc/>
    protected override void ProcessRecord()
    {
        if (Found() is not IZabbixLoggedSession logged) return;
        if (ShouldProcess(Name, $"Clear the {SessionKind} session's log")) logged.Logs.Clear();
    }
}
