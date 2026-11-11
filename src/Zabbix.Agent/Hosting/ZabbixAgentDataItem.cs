// This Source Code Form is subject to the terms of the Mozilla Public
// License, v. 2.0. Copyright (c) 2026 schroeder247. SPDX-License-Identifier: MPL-2.0
using System.Text.Json.Serialization;

namespace Community.Zabbix.Hosting;

/// <summary>
/// Represents one collected value as the agent buffers it and <c>agent data</c> carries it (upstream's
/// <c>active_buffer_element_t</c>). The <see cref="Id"/> is assigned once, when the value enters the buffer, so a retry
/// re-sends the same id and the server's per-session <c>last_id</c> drops a duplicate. <see cref="State"/> 1 is not
/// supported, and then <see cref="Value"/> is the reason: the state travels with the value, not beside it.
/// </summary>
/// <param name="Id">The value's id in the agent's session, ascending from 1.</param>
/// <param name="ItemId">The item it belongs to.</param>
/// <param name="Value">The value, or the not-supported reason.</param>
/// <param name="Clock">When it was collected, Unix seconds.</param>
/// <param name="Ns">The nanoseconds of <paramref name="Clock"/>.</param>
/// <param name="State">1 when the item is not supported; absent otherwise.</param>
internal sealed record ZabbixAgentDataItem(
    [property: JsonPropertyName("id")] long Id,
    [property: JsonPropertyName("itemid")] ulong ItemId,
    [property: JsonPropertyName("value")] string Value,
    [property: JsonPropertyName("clock")] long Clock,
    [property: JsonPropertyName("ns")] int Ns,
    [property: JsonPropertyName("state")] int? State);
