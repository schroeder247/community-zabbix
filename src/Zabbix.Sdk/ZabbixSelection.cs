// This Source Code Form is subject to the terms of the Mozilla Public
// License, v. 2.0. Copyright (c) 2026 schroeder247. SPDX-License-Identifier: MPL-2.0
using System.Globalization;

namespace Community.Zabbix;

/// <summary>
/// Which items a node is about, in the API's own words: a group by its name or id, a host by its technical name or id, a key —
/// each narrowing what the level above selected. This <i>is</i> the <c>item.get</c> filter; there is nothing to resolve first.
/// </summary>
/// <param name="Group">Only hosts in the group of this name.</param>
/// <param name="Host">Only the host of this technical name.</param>
/// <param name="Key">Only this item.</param>
/// <param name="GroupId">Only hosts in the group of this id.</param>
/// <param name="HostId">Only the host of this id.</param>
internal sealed record ZabbixSelection(string? Group = null, string? Host = null, ZabbixItemKey? Key = null, ulong? GroupId = null, ulong? HostId = null)
{
    private static readonly ZabbixOutput<ZabbixItem> s_itemFields =
    [
        ZabbixItemFields.ItemId, ZabbixItemFields.HostId, ZabbixItemFields.Key, ZabbixItemFields.Name, ZabbixItemFields.Delay, ZabbixItemFields.ValueType,
        ZabbixItemFields.LastValue, ZabbixItemFields.LastClock, ZabbixItemFields.LastClockNanoseconds,
    ];

    private static readonly ZabbixOutput<ZabbixHost> s_hostFields = [ZabbixHostFields.HostId, ZabbixHostFields.HostName];

    /// <summary>Whether this selection is about one host.</summary>
    public bool HasHost => Host is not null || HostId is not null;

    /// <summary>Whether this selection is about one group's hosts.</summary>
    public bool HasGroup => Group is not null || GroupId is not null;

    /// <summary>The host as a message names it: <c>'web-01'</c>, or <c>with the id 10084</c>.</summary>
    public string HostText => Host is { } name ? $"'{name}'" : string.Create(CultureInfo.InvariantCulture, $"with the id {HostId}");

    /// <summary>The selection's host is absent: by its technical name, or by its id.</summary>
    /// <returns>The exception that says so.</returns>
    public ZabbixNotFoundException AbsentHost() => Host is { } name ? new ZabbixNotFoundException("host", name) : new ZabbixNotFoundException("host", HostId.GetValueOrDefault());

    /// <summary>The selection's group is absent: by its name, or by its id.</summary>
    /// <returns>The exception that says so.</returns>
    public ZabbixNotFoundException AbsentGroup() => Group is { } name ? new ZabbixNotFoundException("hostgroup", name) : new ZabbixNotFoundException("hostgroup", GroupId.GetValueOrDefault());

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
        HostName = Host,
        HostIds = HostId is { } hostId ? [hostId] : null,
        Group = Group,
        GroupIds = GroupId is { } groupId ? [groupId] : null,
        Filter = Key is { } key ? new() { ["key_"] = key.ToString() } : null,
        SelectHosts = s_hostFields,
        Sort = [ZabbixItemFields.ItemId],
    };

    /// <summary>The <c>host.get</c> that finds this selection's host, by its technical name or its id.</summary>
    public ZabbixHostGetParameters ToHostGet() => new()
    {
        HostIds = HostId is { } hostId ? [hostId] : null,
        Filter = Host is { } host ? new() { ["host"] = host } : null,
        Limit = 1,
    };

    /// <summary>The <c>hostgroup.get</c> that finds this selection's group, by its name or its id.</summary>
    public ZabbixHostGroupGetParameters ToGroupGet() => new()
    {
        GroupIds = GroupId is { } groupId ? [groupId] : null,
        Filter = Group is { } group ? new() { ["name"] = group } : null,
    };

    /// <summary>The depth this selection is, for telemetry: <c>server</c>, <c>group</c>, <c>host</c> or <c>item</c>.</summary>
    public string Scope => Key is not null ? "item" : HasHost ? "host" : HasGroup ? "group" : "server";
}
