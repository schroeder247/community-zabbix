// This Source Code Form is subject to the terms of the Mozilla Public
// License, v. 2.0. Copyright (c) 2026 schroeder247. SPDX-License-Identifier: MPL-2.0
namespace Community.Zabbix.Proxy;

/// <summary>An item the proxy monitors, as the server's <c>items</c> table sends it: what admission needs.</summary>
/// <param name="ItemId">The item's id: what <c>proxy data</c> carries.</param>
/// <param name="HostId">Its host.</param>
/// <param name="Key">The key, verbatim: Zabbix matches a sender's key literally.</param>
/// <param name="Type">The item type.</param>
/// <param name="Status">Whether it is collected.</param>
/// <param name="TrapperHosts">The allowed peers for traps, macros unresolved.</param>
/// <param name="AllowTraps">For an HTTP agent item: whether it also takes traps.</param>
/// <param name="Delay">The update interval, verbatim (a plain seconds count or a flexible/scheduling grammar); what <c>active checks</c> answers with.</param>
/// <param name="Timeout">The item's own check timeout, verbatim (macros unresolved); empty when the item has none.</param>
public sealed record ZabbixProxyConfigItem(ulong ItemId, ulong HostId, string Key, ZabbixProxyItemType Type, ZabbixProxyItemStatus Status, string TrapperHosts, bool AllowTraps, string Delay = "", string Timeout = "");
