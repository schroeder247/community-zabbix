// This Source Code Form is subject to the terms of the Mozilla Public
// License, v. 2.0. Copyright (c) 2026 schroeder247. SPDX-License-Identifier: MPL-2.0
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.Json.Serialization.Metadata;

namespace Community.Zabbix.Connector;

/// <summary>One line of a connector stream: an item value or an event, told apart by <c>itemid</c> / <c>eventid</c>.</summary>
public abstract record ZabbixConnectorRecord
{
    /// <summary>When, to the nanosecond (the line's <c>clock</c> and <c>ns</c>).</summary>
    [JsonIgnore]
    public DateTimeOffset Clock { get => Exact.ToDateTimeOffset(); init => Exact = ZabbixClock.FromDateTimeOffset(value); }

    internal ZabbixClock Exact { get; init; }

    [JsonInclude]
    [JsonPropertyName("clock")]
    internal long ClockSeconds { get => Exact.Seconds; init => Exact = ZabbixClockFields.WithSeconds(Exact, value); }

    [JsonInclude]
    [JsonPropertyName("ns")]
    internal int ClockNanoseconds { get => Exact.Nanoseconds; init => Exact = ZabbixClockFields.WithNanoseconds(Exact, value, "ns"); }
}

/// <summary>
/// One connector line as a record: an item value when it carries <c>itemid</c> (a log value carries <c>eventid</c> too),
/// an event when it carries only <c>eventid</c>, and a <see cref="JsonException"/> otherwise. The discriminator is the
/// presence of a member, not a <c>$type</c> value, so STJ's <c>[JsonDerivedType]</c> polymorphism cannot say it; this
/// converter scans a copy of the reader (the whole value is buffered before a converter runs) and hands the value to the
/// derived type's generated metadata.
/// </summary>
internal sealed class ZabbixConnectorRecordConverter : JsonConverter<ZabbixConnectorRecord>
{
    /// <inheritdoc/>
    public override ZabbixConnectorRecord? Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);
        if (reader.TokenType != JsonTokenType.StartObject) throw new JsonException($"A connector line is a JSON object, not {reader.TokenType}.");
        Utf8JsonReader scan = reader;
        int depth = scan.CurrentDepth;
        bool value = false;
        bool @event = false;
        while (scan.Read() && scan.CurrentDepth > depth)
        {
            if (scan.TokenType != JsonTokenType.PropertyName) continue;
            if (scan.ValueTextEquals("itemid"u8)) value = true;
            else if (scan.ValueTextEquals("eventid"u8)) @event = true;
            if (!scan.Read() || !scan.TrySkip()) throw new JsonException("A connector line ended inside a member.");   // the value is buffered whole: TrySkip succeeds
        }
        return value ? JsonSerializer.Deserialize(ref reader, (JsonTypeInfo<ZabbixConnectorValue>)options.GetTypeInfo(typeof(ZabbixConnectorValue)))
            : @event ? JsonSerializer.Deserialize(ref reader, (JsonTypeInfo<ZabbixConnectorEvent>)options.GetTypeInfo(typeof(ZabbixConnectorEvent)))
            : throw new JsonException("A connector line is an item value (itemid) or an event (eventid); this one is neither.");
    }

    /// <inheritdoc/>
    public override void Write(Utf8JsonWriter writer, ZabbixConnectorRecord value, JsonSerializerOptions options)
    {
        ArgumentNullException.ThrowIfNull(value);
        ArgumentNullException.ThrowIfNull(options);
        JsonSerializer.Serialize(writer, value, options.GetTypeInfo(value.GetType()));
    }
}
