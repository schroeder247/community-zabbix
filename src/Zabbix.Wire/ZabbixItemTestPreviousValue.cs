// This Source Code Form is subject to the terms of the Mozilla Public
// License, v. 2.0. Copyright (c) 2026 schroeder247. SPDX-License-Identifier: MPL-2.0
using System.Text.Json;
using System.Text.Json.Serialization;

namespace Community.Zabbix;

/// <summary>
/// Represents the previous value a preprocessing test compares with: the value, and how long before now it was
/// collected.
/// </summary>
public sealed record ZabbixItemTestPreviousValue : IJsonOnSerializing
{
    /// <summary>The previous value.</summary>
    [JsonPropertyName("value")]
    public string? Value { get; set; }

    /// <summary>How long before now the value was collected; the server reads it as <c>now-30s</c>.</summary>
    [JsonPropertyName("timestamp")]
    [JsonConverter(typeof(Protocol.ZabbixNowMinusConverter))]
    public TimeSpan? Age { get; set; }

    /// <summary>
    /// Fields this record doesn't model, sent as they are beside the modelled ones: what a newer server reads, by its wire
    /// name.
    /// </summary>
    [JsonExtensionData]
    [System.Diagnostics.CodeAnalysis.SuppressMessage("Usage", "CA2227:Collection properties should be read only", Justification = "System.Text.Json fills extension data through a setter, as the generated requests do.")]
    public IDictionary<string, JsonElement>? AdditionalFields { get; set; }

    private static readonly System.Collections.Frozen.FrozenSet<string> s_modelledWires = System.Collections.Frozen.FrozenSet.Create(StringComparer.Ordinal, "value", "timestamp");

    void IJsonOnSerializing.OnSerializing() => ZabbixExtensionDataGuard.Refuse(AdditionalFields, nameof(ZabbixItemTestPreviousValue), s_modelledWires);

    [System.Diagnostics.CodeAnalysis.SuppressMessage("CodeQuality", "IDE0051", Justification = "The record's synthesized ToString calls it; the analyser does not see the call.")]
    private bool PrintMembers(System.Text.StringBuilder builder)
    {
        bool more = false;
        ZabbixRecordText.Member(builder, ref more, nameof(Value), Value);
        ZabbixRecordText.Member(builder, ref more, nameof(Age), Age);
        ZabbixRecordText.Count(builder, ref more, nameof(AdditionalFields), AdditionalFields);
        return more;
    }

    /// <summary>Whether <paramref name="other"/> holds the same values, its lists and fields compared by value.</summary>
    /// <param name="other">The other record.</param>
    /// <returns><see langword="true"/> when every member holds the same value; otherwise <see langword="false"/>.</returns>
    public bool Equals(ZabbixItemTestPreviousValue? other)
        => other is not null
            && string.Equals(Value, other.Value, StringComparison.Ordinal)
            && Equals(Age, other.Age)
            && ZabbixValueEquality.DictionaryEqual(AdditionalFields, other.AdditionalFields);

    /// <inheritdoc/>
    public override int GetHashCode()
    {
        var hash = new HashCode();
        hash.Add(Value, StringComparer.Ordinal);
        hash.Add(Age);
        ZabbixValueEquality.AddDictionary(ref hash, AdditionalFields);
        return hash.ToHashCode();
    }
}
