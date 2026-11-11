// This Source Code Form is subject to the terms of the Mozilla Public
// License, v. 2.0. Copyright (c) 2026 schroeder247. SPDX-License-Identifier: MPL-2.0
using System.Text.Json;
using System.Text.Json.Serialization;

namespace Community.Zabbix.Connector;

/// <summary>An item value the server streamed.</summary>
public sealed record ZabbixConnectorValue : ZabbixConnectorRecord
{
    /// <summary>The item.</summary>
    [JsonPropertyName("itemid")]
    public ulong ItemId { get; init; }

    /// <summary>The item's name.</summary>
    [JsonPropertyName("name")]
    public string? Name { get; init; }

    /// <summary>The value type.</summary>
    [JsonPropertyName("type")]
    public ZabbixConnectorValueType Type { get; init; }

    /// <summary>The value, in whatever JSON form the type takes.</summary>
    [JsonPropertyName("value")]
    public JsonElement Value { get; init; }

    /// <summary>The host.</summary>
    [JsonPropertyName("host")]
    public ZabbixConnectorHost? Host { get; init; }

    /// <summary>The host's groups.</summary>
    [JsonPropertyName("groups")]
    public IReadOnlyList<string>? Groups { get; init; }

    /// <summary>The item's tags.</summary>
    [JsonPropertyName("item_tags")]
    public IReadOnlyList<ZabbixTag>? ItemTags { get; init; }

    /// <summary>A log value's own timestamp (value type 2), in seconds; <see langword="null"/> for other types.</summary>
    [JsonPropertyName("timestamp")]
    public long? Timestamp { get; init; }

    /// <summary>A log value's source (value type 2).</summary>
    [JsonPropertyName("source")]
    public string? Source { get; init; }

    /// <summary>A log value's severity (value type 2).</summary>
    [JsonPropertyName("severity")]
    public int? Severity { get; init; }

    /// <summary>A log value's event id (value type 2; <c>eventid</c> beside <c>itemid</c>, so the line is still a value).</summary>
    [JsonPropertyName("eventid")]
    public long? LogEventId { get; init; }

    /// <summary>Whether <paramref name="other"/> is the same value: its groups and tags in order, its JSON by content.</summary>
    /// <param name="other">The other value.</param>
    public bool Equals(ZabbixConnectorValue? other)
        => base.Equals(other) && ItemId == other.ItemId && string.Equals(Name, other.Name, StringComparison.Ordinal) && Type == other.Type
            && ZabbixValueEquality.JsonEqual(Value, other.Value) && Equals(Host, other.Host)
            && ZabbixValueEquality.ListEqual(Groups, other.Groups) && ZabbixValueEquality.ListEqual(ItemTags, other.ItemTags)
            && Timestamp == other.Timestamp && string.Equals(Source, other.Source, StringComparison.Ordinal) && Severity == other.Severity
            && LogEventId == other.LogEventId;

    /// <inheritdoc/>
    public override int GetHashCode()
    {
        var hash = new HashCode();
        hash.Add(base.GetHashCode());
        hash.Add(ItemId);
        hash.Add(Name, StringComparer.Ordinal);
        ZabbixValueEquality.AddJson(ref hash, Value);
        ZabbixValueEquality.AddList(ref hash, Groups);
        ZabbixValueEquality.AddList(ref hash, ItemTags);
        return hash.ToHashCode();
    }
}
