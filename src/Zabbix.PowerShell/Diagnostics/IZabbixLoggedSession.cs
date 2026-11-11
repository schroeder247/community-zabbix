// This Source Code Form is subject to the terms of the Mozilla Public
// License, v. 2.0. Copyright (c) 2026 schroeder247. SPDX-License-Identifier: MPL-2.0
namespace Community.Zabbix.PowerShell.Diagnostics;

/// <summary>
/// A session whose background loop logs through a <see cref="ZabbixPowerShellLoggerProvider"/> of its own, flushed by
/// the next command in the runspace exactly like a connection's own (<see cref="ZabbixCmdlet"/>).
/// </summary>
internal interface IZabbixLoggedSession
{
    /// <summary>The bridge from this session's own loggers to whichever command next runs in this runspace.</summary>
    ZabbixPowerShellLoggerProvider Logs { get; }
}
