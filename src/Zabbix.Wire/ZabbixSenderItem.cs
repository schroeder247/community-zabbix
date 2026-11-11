// This Source Code Form is subject to the terms of the Mozilla Public
// License, v. 2.0. Copyright (c) 2026 schroeder247. SPDX-License-Identifier: MPL-2.0
using System.Text.Json.Serialization;

namespace Community.Zabbix;

/// <summary>
/// One value as the trapper's <c>sender data</c> request carries it — a projection of <see cref="ZabbixValue"/>, never
/// authored by hand: its instant goes on the wire as <c>clock</c>, Unix seconds, and <c>ns</c>, the nanoseconds within
/// that second.
/// </summary>
/// <param name="Host">The host.</param>
/// <param name="Key">The item key, as text.</param>
/// <param name="Value">The value.</param>
/// <param name="Clock">When the value was taken, to the nanosecond.</param>
/// <param name="State">1 when the item is not supported and <paramref name="Value"/> is the reason; absent otherwise. The
/// trapper reads a <c>sender data</c> row's <c>state</c> as it reads an agent's (<c>proxy.c</c>
/// <c>parse_history_data_row_value</c> serves both).</param>
internal sealed record ZabbixSenderItem(
    [property: JsonPropertyName("host")] string Host,
    [property: JsonPropertyName("key")] string Key,
    [property: JsonPropertyName("value")] string Value,
    DateTimeOffset Clock,
    [property: JsonPropertyName("state"), JsonPropertyOrder(1)] int? State = null)
{
    internal ZabbixSenderItem(string host, string key, string value, ZabbixClock clock, int? state = null)
        : this(host, key, value, default(DateTimeOffset), state)
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

    /// <summary>The value this item carries, for a caller to send again.</summary>
    /// <returns>The value.</returns>
    public ZabbixValue ToValue() => new(Host, Key, Value, Exact) { IsSupported = State is null };

    /// <summary>The wire form of a sample.</summary>
    /// <param name="value">The sample.</param>
    /// <returns>The wire item for the sample: its host, key, value and clock.</returns>
    public static ZabbixSenderItem From(ZabbixValue value)
    {
        ArgumentNullException.ThrowIfNull(value);
        return new ZabbixSenderItem(value.Host, value.Key.ToString(), value.Value, value.Clock, value.IsSupported ? null : 1);
    }
}
