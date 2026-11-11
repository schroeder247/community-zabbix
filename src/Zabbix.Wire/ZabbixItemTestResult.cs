// This Source Code Form is subject to the terms of the Mozilla Public
// License, v. 2.0. Copyright (c) 2026 schroeder247. SPDX-License-Identifier: MPL-2.0
using System.Text.Json;
using System.Text.Json.Serialization;

namespace Community.Zabbix;

/// <summary>
/// Represents what <c>item.test</c> answers: the value the server collected, and what the preprocessing steps made of
/// it.
/// </summary>
public sealed record ZabbixItemTestResult : IZabbixAdditionalFields
{
    /// <summary>The value collected, or why it couldn't be; absent when the test preprocessed a given value.</summary>
    [JsonPropertyName("item")]
    public ZabbixItemTestValue? Item { get; init; }

    /// <summary>What the preprocessing steps did.</summary>
    [JsonPropertyName("preprocessing")]
    public ZabbixItemTestPreprocessing? Preprocessing { get; init; }

    /// <summary>Every field the reply carried that this record doesn't model, kept as the server sent it.</summary>
    [JsonExtensionData]
    [JsonInclude]
    internal IDictionary<string, JsonElement>? AdditionalFields { get; set; }

    /// <inheritdoc/>
    IDictionary<string, JsonElement>? IZabbixAdditionalFields.AdditionalFields => AdditionalFields;

    [System.Diagnostics.CodeAnalysis.SuppressMessage("CodeQuality", "IDE0051", Justification = "The record's synthesized ToString calls it; the analyser does not see the call.")]
    private bool PrintMembers(System.Text.StringBuilder builder)
    {
        bool more = false;
        ZabbixRecordText.Member(builder, ref more, nameof(Item), Item);
        ZabbixRecordText.Member(builder, ref more, nameof(Preprocessing), Preprocessing);
        ZabbixRecordText.Count(builder, ref more, nameof(AdditionalFields), AdditionalFields);
        return more;
    }

    /// <summary>Whether <paramref name="other"/> holds the same values, its lists and fields compared by value.</summary>
    /// <param name="other">The other record.</param>
    /// <returns><see langword="true"/> when every member holds the same value; otherwise <see langword="false"/>.</returns>
    public bool Equals(ZabbixItemTestResult? other)
        => other is not null
            && Equals(Item, other.Item)
            && Equals(Preprocessing, other.Preprocessing)
            && ZabbixValueEquality.DictionaryEqual(AdditionalFields, other.AdditionalFields);

    /// <inheritdoc/>
    public override int GetHashCode()
    {
        var hash = new HashCode();
        hash.Add(Item);
        hash.Add(Preprocessing);
        ZabbixValueEquality.AddDictionary(ref hash, AdditionalFields);
        return hash.ToHashCode();
    }
}
