// This Source Code Form is subject to the terms of the Mozilla Public
// License, v. 2.0. Copyright (c) 2026 schroeder247. SPDX-License-Identifier: MPL-2.0
using System.Diagnostics;
using Community.Zabbix.PowerShell.Diagnostics;

namespace Community.Zabbix.PowerShell.Sessions;

/// <summary>
/// Provides the base of every <c>Stop-Zbx&lt;Session&gt;</c> cmdlet, which forgets the session in this runspace and stops
/// it (its loops cancelled and awaited), confirmed with <c>ShouldProcess</c>.
/// </summary>
public abstract class ZabbixStopSessionCmdlet : ZabbixSessionCmdlet
{
    /// <inheritdoc/>
    protected override void ProcessRecord()
    {
        if (Found() is not { } session || !ShouldProcess(Name, $"Stop {SessionKind}")) return;
        ZabbixContext.Sessions.Forget(session);
        using Activity? span = ZabbixPowerShellTelemetry.StartCommandSpan(MyInvocation.MyCommand.Name);
        TryRun(async _ => await session.DisposeAsync().ConfigureAwait(false), Name);
    }
}
