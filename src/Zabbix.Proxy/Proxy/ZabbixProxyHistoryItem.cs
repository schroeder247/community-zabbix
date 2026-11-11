// This Source Code Form is subject to the terms of the Mozilla Public
// License, v. 2.0. Copyright (c) 2026 schroeder247. SPDX-License-Identifier: MPL-2.0
using System.Text.Json.Serialization;

namespace Community.Zabbix.Proxy;

/// <summary>
/// One value as <c>proxy data</c> carries it: by item id, with its id — the store's sequence, which the server
/// de-duplicates on — and its state (<c>pb_history.c</c> <c>zbx_pb_history_get_rows</c>).
/// </summary>
/// <param name="Sequence">The store's sequence, the row's <c>id</c> on the wire.</param>
/// <param name="ItemId">The item.</param>
/// <param name="Clock">When the value was taken, to the nanosecond (the wire's <c>clock</c> and <c>ns</c>).</param>
/// <param name="Value">The value, or the not-supported reason when <paramref name="State"/> is 1; absent (a row that
/// carries no value, <c>ZBX_PROXY_HISTORY_FLAG_NOVALUE</c>) when <see langword="null"/>.</param>
/// <param name="State">1 when the item is not supported; absent for a supported value, as upstream writes it only
/// when it is not <c>ITEM_STATE_NORMAL</c>.</param>
public sealed record ZabbixProxyHistoryItem(
    [property: JsonPropertyName("id")] long Sequence,
    [property: JsonPropertyName("itemid")] ulong ItemId,
    DateTimeOffset Clock,
    [property: JsonPropertyName("value")] string? Value,
    [property: JsonPropertyName("state")] int? State = null)
{
    internal ZabbixProxyHistoryItem(long sequence, ulong itemId, ZabbixClock clock, string? value, int? state = null)
        : this(sequence, itemId, default(DateTimeOffset), value, state)
    {
        Exact = clock;
    }

    /// <summary>When the value was taken, to 100 ns.</summary>
    [JsonIgnore]
    public DateTimeOffset Clock { get => Exact.ToDateTimeOffset(); init => Exact = ZabbixClock.FromDateTimeOffset(value); }

    internal ZabbixClock Exact { get; init; } = ZabbixClock.FromDateTimeOffset(Clock);

    [JsonInclude]
    [JsonPropertyName("clock")]
    internal long ClockSeconds { get => Exact.Seconds; init => Exact = ZabbixClockFields.WithSeconds(Exact, value); }

    [JsonInclude]
    [JsonPropertyName("ns")]
    internal int ClockNanoseconds { get => Exact.Nanoseconds; init => Exact = ZabbixClockFields.WithNanoseconds(Exact, value, "ns"); }
}
