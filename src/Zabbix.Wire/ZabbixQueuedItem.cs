// This Source Code Form is subject to the terms of the Mozilla Public
// License, v. 2.0. Copyright (c) 2026 schroeder247. SPDX-License-Identifier: MPL-2.0
using System.Text.Json.Serialization;

namespace Community.Zabbix;

/// <summary>One overdue item: its id and when it was due.</summary>
/// <param name="ItemId">The item.</param>
/// <param name="NextCheck">When it was due (the wire's <c>nextcheck</c>, Unix seconds).</param>
public sealed record ZabbixQueuedItem(
    [property: JsonPropertyName("itemid")] ulong ItemId,
    [property: JsonPropertyName("nextcheck"), JsonConverter(typeof(ZabbixUnixTimeConverter))] DateTimeOffset NextCheck);
