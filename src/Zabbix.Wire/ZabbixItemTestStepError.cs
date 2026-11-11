// This Source Code Form is subject to the terms of the Mozilla Public
// License, v. 2.0. Copyright (c) 2026 schroeder247. SPDX-License-Identifier: MPL-2.0
using System.Text.Json;
using System.Text.Json.Serialization;

namespace Community.Zabbix;

/// <summary>Represents the error a preprocessing step under test failed with.</summary>
public sealed record ZabbixItemTestStepError : IZabbixAdditionalFields
{
    /// <summary>The error.</summary>
    [JsonPropertyName("value")]
    public string? Message { get; init; }

    /// <summary>Whether the server cut the text short to send it.</summary>
    [JsonPropertyName("truncated")]
    public bool? IsTruncated { get; init; }

    /// <summary>How many bytes the text had before the server cut it short.</summary>
    [JsonPropertyName("original_size")]
    public long? OriginalSize { get; init; }

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
        ZabbixRecordText.Member(builder, ref more, nameof(Message), Message);
        ZabbixRecordText.Member(builder, ref more, nameof(IsTruncated), IsTruncated);
        ZabbixRecordText.Member(builder, ref more, nameof(OriginalSize), OriginalSize);
        ZabbixRecordText.Count(builder, ref more, nameof(AdditionalFields), AdditionalFields);
        return more;
    }

    /// <summary>Whether <paramref name="other"/> holds the same values, its lists and fields compared by value.</summary>
    /// <param name="other">The other record.</param>
    /// <returns><see langword="true"/> when every member holds the same value; otherwise <see langword="false"/>.</returns>
    public bool Equals(ZabbixItemTestStepError? other)
        => other is not null
            && string.Equals(Message, other.Message, StringComparison.Ordinal)
            && Equals(IsTruncated, other.IsTruncated)
            && Equals(OriginalSize, other.OriginalSize)
            && ZabbixValueEquality.DictionaryEqual(AdditionalFields, other.AdditionalFields);

    /// <inheritdoc/>
    public override int GetHashCode()
    {
        var hash = new HashCode();
        hash.Add(Message, StringComparer.Ordinal);
        hash.Add(IsTruncated);
        hash.Add(OriginalSize);
        ZabbixValueEquality.AddDictionary(ref hash, AdditionalFields);
        return hash.ToHashCode();
    }
}
