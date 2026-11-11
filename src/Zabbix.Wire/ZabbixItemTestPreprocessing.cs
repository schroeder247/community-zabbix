// This Source Code Form is subject to the terms of the Mozilla Public
// License, v. 2.0. Copyright (c) 2026 schroeder247. SPDX-License-Identifier: MPL-2.0
using System.Text.Json;
using System.Text.Json.Serialization;

namespace Community.Zabbix;

/// <summary>
/// Represents what the preprocessing steps of an item under test did: each step's outcome, and the value they gave, or
/// the error they failed with.
/// </summary>
public sealed record ZabbixItemTestPreprocessing : IZabbixAdditionalFields
{
    /// <summary>Each step's outcome, in order.</summary>
    [JsonPropertyName("steps")]
    public IReadOnlyList<ZabbixItemTestStepResult>? Steps { get; init; }

    /// <summary>The value the steps gave; absent when they failed.</summary>
    [JsonPropertyName("result")]
    public string? Result { get; init; }

    /// <summary>Why the steps failed; absent when they succeeded.</summary>
    [JsonPropertyName("error")]
    public string? Error { get; init; }

    /// <summary>Whether the test also ran the steps on the previous value, to compare with it.</summary>
    [JsonPropertyName("previous")]
    public bool? UsedPreviousValue { get; init; }

    /// <summary>Whether the server cut the result or the error short to send it.</summary>
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
        ZabbixRecordText.Count(builder, ref more, nameof(Steps), Steps);
        ZabbixRecordText.Member(builder, ref more, nameof(Result), Result);
        ZabbixRecordText.Member(builder, ref more, nameof(Error), Error);
        ZabbixRecordText.Member(builder, ref more, nameof(UsedPreviousValue), UsedPreviousValue);
        ZabbixRecordText.Member(builder, ref more, nameof(IsTruncated), IsTruncated);
        ZabbixRecordText.Member(builder, ref more, nameof(OriginalSize), OriginalSize);
        ZabbixRecordText.Count(builder, ref more, nameof(AdditionalFields), AdditionalFields);
        return more;
    }

    /// <summary>Whether <paramref name="other"/> holds the same values, its lists and fields compared by value.</summary>
    /// <param name="other">The other record.</param>
    /// <returns><see langword="true"/> when every member holds the same value; otherwise <see langword="false"/>.</returns>
    public bool Equals(ZabbixItemTestPreprocessing? other)
        => other is not null
            && ZabbixValueEquality.ListEqual(Steps, other.Steps)
            && string.Equals(Result, other.Result, StringComparison.Ordinal)
            && string.Equals(Error, other.Error, StringComparison.Ordinal)
            && Equals(UsedPreviousValue, other.UsedPreviousValue)
            && Equals(IsTruncated, other.IsTruncated)
            && Equals(OriginalSize, other.OriginalSize)
            && ZabbixValueEquality.DictionaryEqual(AdditionalFields, other.AdditionalFields);

    /// <inheritdoc/>
    public override int GetHashCode()
    {
        var hash = new HashCode();
        ZabbixValueEquality.AddList(ref hash, Steps);
        hash.Add(Result, StringComparer.Ordinal);
        hash.Add(Error, StringComparer.Ordinal);
        hash.Add(UsedPreviousValue);
        hash.Add(IsTruncated);
        hash.Add(OriginalSize);
        ZabbixValueEquality.AddDictionary(ref hash, AdditionalFields);
        return hash.ToHashCode();
    }
}
