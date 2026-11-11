// This Source Code Form is subject to the terms of the Mozilla Public
// License, v. 2.0. Copyright (c) 2026 schroeder247. SPDX-License-Identifier: MPL-2.0
using System.Text.Json.Serialization;

namespace Community.Zabbix;

/// <summary>
/// One row of the queue overview: how many items of one item type (or one proxy) are overdue by more than 5, 10, 30,
/// 60, 300 and 600 seconds (<c>queue_stats_export</c> in <c>trapper.c</c>).
/// </summary>
/// <param name="ItemType">The item type, when grouped by type.</param>
/// <param name="ProxyId">The proxy, when grouped by proxy; 0 is the server itself.</param>
/// <param name="Delay5">Overdue 5–10 s.</param>
/// <param name="Delay10">Overdue 10–30 s.</param>
/// <param name="Delay30">Overdue 30–60 s.</param>
/// <param name="Delay60">Overdue 60–300 s.</param>
/// <param name="Delay300">Overdue 300–600 s.</param>
/// <param name="Delay600">Overdue more than 600 s.</param>
public sealed record ZabbixQueueDelays(
    [property: JsonPropertyName("itemtype")] int? ItemType,
    [property: JsonPropertyName("proxyid")] ulong? ProxyId,
    [property: JsonPropertyName("delay5")] long Delay5,
    [property: JsonPropertyName("delay10")] long Delay10,
    [property: JsonPropertyName("delay30")] long Delay30,
    [property: JsonPropertyName("delay60")] long Delay60,
    [property: JsonPropertyName("delay300")] long Delay300,
    [property: JsonPropertyName("delay600")] long Delay600);
