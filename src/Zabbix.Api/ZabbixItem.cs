// This Source Code Form is subject to the terms of the Mozilla Public
// License, v. 2.0. Copyright (c) 2026 schroeder247. SPDX-License-Identifier: MPL-2.0
using System.Text.Json.Serialization;

namespace Community.Zabbix;

/// <summary>What <c>item.get</c> adds to an item on request and what the tree reads off it.</summary>
public sealed partial record ZabbixItem
{
    /// <summary>
    /// The technical name of the item's host, when <c>selectHosts</c> included <c>host</c>; <see langword="null"/> otherwise
    /// (the id is <see cref="HostId"/>, never passed off as a name).
    /// </summary>
    [JsonIgnore]
    public string? HostName => Hosts is [{ TechnicalName: { } host }, ..] ? host : null;
}
