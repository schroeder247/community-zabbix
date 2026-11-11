// This Source Code Form is subject to the terms of the Mozilla Public
// License, v. 2.0. Copyright (c) 2026 schroeder247. SPDX-License-Identifier: MPL-2.0
namespace Community.Zabbix.PowerShell.Commands;

/// <summary>One item of a <see cref="ZabbixAgentHostStatus"/>.</summary>
/// <param name="Key">The item's key.</param>
/// <param name="ItemId">The item's id.</param>
/// <param name="HasLocalHandler">Whether the session this status was read through answers this key.</param>
public sealed record ZabbixAgentHostItemStatus(string Key, ulong ItemId, bool HasLocalHandler);
