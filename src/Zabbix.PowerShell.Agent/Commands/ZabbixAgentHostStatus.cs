// This Source Code Form is subject to the terms of the Mozilla Public
// License, v. 2.0. Copyright (c) 2026 schroeder247. SPDX-License-Identifier: MPL-2.0
namespace Community.Zabbix.PowerShell.Commands;

/// <summary>The host as the server sees it, with which of its items a local handler still answers.</summary>
/// <param name="Host">The host, as <c>host.get</c> answered.</param>
/// <param name="Items">Its items, each flagged with whether a local handler answers that key.</param>
public sealed record ZabbixAgentHostStatus(ZabbixHost Host, IReadOnlyList<ZabbixAgentHostItemStatus> Items);
