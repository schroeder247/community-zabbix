// This Source Code Form is subject to the terms of the Mozilla Public
// License, v. 2.0. Copyright (c) 2026 schroeder247. SPDX-License-Identifier: MPL-2.0
namespace Community.Zabbix.PowerShell;

/// <summary>
/// Represents something the PowerShell session keeps running between commands: a watcher, an agent, a proxy. It is
/// started by one command, found by name by the next, and stopped by <c>Stop-</c>, by <c>Remove-Module</c> or by the
/// runspace closing — never by a process-wide registry.
/// </summary>
internal interface IZabbixSession : IAsyncDisposable
{
    /// <summary>The name the session knows it by (unique within its kind, case-insensitive).</summary>
    string Name { get; }

    /// <summary>When it was started.</summary>
    DateTimeOffset Started { get; }
}
