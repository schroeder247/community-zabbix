// This Source Code Form is subject to the terms of the Mozilla Public
// License, v. 2.0. Copyright (c) 2026 schroeder247. SPDX-License-Identifier: MPL-2.0
namespace Community.Zabbix.PowerShell.Sessions;

/// <summary>
/// The agent or proxy a session runs, as <see cref="ZabbixSessionHost"/> drives it. This module does not carry the agent
/// package that defines them, so the agent and proxy modules, which do, build one of these over theirs.
/// </summary>
/// <param name="StartAsync">Its <c>StartAsync</c>.</param>
/// <param name="StopAsync">Its <c>StopAsync</c>.</param>
/// <param name="DisposeAsync">Its <c>DisposeAsync</c>.</param>
internal sealed record ZabbixSessionComponent(Func<CancellationToken, Task> StartAsync, Func<CancellationToken, Task> StopAsync, Func<ValueTask> DisposeAsync);
