// This Source Code Form is subject to the terms of the Mozilla Public
// License, v. 2.0. Copyright (c) 2026 schroeder247. SPDX-License-Identifier: MPL-2.0
using System.Text.Json;
using System.Text.Json.Serialization;

namespace Community.Zabbix;

/// <summary>
/// Represents what one preprocessing step did with the value under test: the value it gave the next step, or its error
/// and what the error handler did.
/// </summary>
public sealed record ZabbixItemTestStepResult : IZabbixAdditionalFields
{
    /// <summary>The value the step gave the next one; absent when the step failed or discarded the value.</summary>
    [JsonPropertyName("result")]
    public string? Result { get; init; }

    /// <summary>The error the step failed with.</summary>
    [JsonPropertyName("error")]
    public ZabbixItemTestStepError? Error { get; init; }

    /// <summary>
    /// What the step's error handler did, the API's <c>ZabbixPreprocessingErrorHandler</c> as its number; absent when
    /// it did the default.
    /// </summary>
    [JsonPropertyName("action")]
    public int? Action { get; init; }

    /// <summary>The error message the error handler set.</summary>
    [JsonPropertyName("failed")]
    public string? Failed { get; init; }

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
        ZabbixRecordText.Member(builder, ref more, nameof(Result), Result);
        ZabbixRecordText.Member(builder, ref more, nameof(Error), Error);
        ZabbixRecordText.Member(builder, ref more, nameof(Action), Action);
        ZabbixRecordText.Member(builder, ref more, nameof(Failed), Failed);
        ZabbixRecordText.Member(builder, ref more, nameof(IsTruncated), IsTruncated);
        ZabbixRecordText.Member(builder, ref more, nameof(OriginalSize), OriginalSize);
        ZabbixRecordText.Count(builder, ref more, nameof(AdditionalFields), AdditionalFields);
        return more;
    }

    /// <summary>Whether <paramref name="other"/> holds the same values, its lists and fields compared by value.</summary>
    /// <param name="other">The other record.</param>
    /// <returns><see langword="true"/> when every member holds the same value; otherwise <see langword="false"/>.</returns>
    public bool Equals(ZabbixItemTestStepResult? other)
        => other is not null
            && string.Equals(Result, other.Result, StringComparison.Ordinal)
            && Equals(Error, other.Error)
            && Equals(Action, other.Action)
            && string.Equals(Failed, other.Failed, StringComparison.Ordinal)
            && Equals(IsTruncated, other.IsTruncated)
            && Equals(OriginalSize, other.OriginalSize)
            && ZabbixValueEquality.DictionaryEqual(AdditionalFields, other.AdditionalFields);

    /// <inheritdoc/>
    public override int GetHashCode()
    {
        var hash = new HashCode();
        hash.Add(Result, StringComparer.Ordinal);
        hash.Add(Error);
        hash.Add(Action);
        hash.Add(Failed, StringComparer.Ordinal);
        hash.Add(IsTruncated);
        hash.Add(OriginalSize);
        ZabbixValueEquality.AddDictionary(ref hash, AdditionalFields);
        return hash.ToHashCode();
    }
}
