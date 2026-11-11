// This Source Code Form is subject to the terms of the Mozilla Public
// License, v. 2.0. Copyright (c) 2026 schroeder247. SPDX-License-Identifier: MPL-2.0
namespace Community.Zabbix.Proxy;

/// <summary>A host the proxy monitors, as the server's <c>hosts</c> table sends it.</summary>
/// <param name="HostId">The host's id.</param>
/// <param name="Host">The technical name, which senders and agents use.</param>
/// <param name="Name">The visible name.</param>
/// <param name="Status">Whether it is monitored.</param>
public sealed record ZabbixProxyConfigHost(ulong HostId, string Host, string Name, ZabbixHostStatus Status);
