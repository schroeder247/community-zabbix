// This Source Code Form is subject to the terms of the Mozilla Public
// License, v. 2.0. Copyright (c) 2026 schroeder247. SPDX-License-Identifier: MPL-2.0
namespace Community.Zabbix;

/// <summary>
/// Which items a node is about, in the API's own words: a group name, a host name, a key — each narrowing what the
/// level above selected. This <i>is</i> the <c>item.get</c> filter; there is nothing to resolve first.
/// </summary>
/// <param name="Group">Only hosts in this group.</param>
/// <param name="Host">Only this host.</param>
/// <param name="Key">Only this item.</param>
internal sealed record ZabbixSelection(string? Group = null, string? Host = null, ZabbixItemKey? Key = null)
{
    private static readonly ZabbixOutput s_itemFields = ZabbixOutput.Of("itemid", "hostid", "key_", "name", "delay", "value_type", "lastvalue", "lastclock", "lastns");
    private static readonly ZabbixOutput s_hostFields = ZabbixOutput.Of("hostid", "host");

    /// <summary>The items this selection's key might be, however the server's item writes its parameters: every key of that name on the host.</summary>
    public ZabbixItemGetParameters ToKeySearch() => ToItemGet() with
    {
        Filter = null,
        Search = new() { ["key_"] = Key is { } key ? key.Name + "[" : string.Empty },
        StartSearch = true,
    };

    /// <summary>The wire form of this selection.</summary>
    public ZabbixItemGetParameters ToItemGet() => new()
    {
        Output = s_itemFields,
        Host = Host,
        Group = Group,
        Filter = Key is { } key ? new() { ["key_"] = key.ToString() } : null,
        SelectHosts = s_hostFields,
        SortField = ["itemid"],
    };

    /// <summary>The depth this selection is, for telemetry: <c>server</c>, <c>group</c>, <c>host</c> or <c>item</c>.</summary>
    public string Scope => Key is not null ? "item" : Host is not null ? "host" : Group is not null ? "group" : "server";
}
