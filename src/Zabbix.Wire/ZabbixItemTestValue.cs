// This Source Code Form is subject to the terms of the Mozilla Public
// License, v. 2.0. Copyright (c) 2026 schroeder247. SPDX-License-Identifier: MPL-2.0
using System.Text.Json;
using System.Text.Json.Serialization;

namespace Community.Zabbix;

/// <summary>Represents the value the server collected for an item under test, or why it couldn't.</summary>
public sealed record ZabbixItemTestValue : IZabbixAdditionalFields
{
    /// <summary>The value collected; absent when the collection failed.</summary>
    [JsonPropertyName("result")]
    public string? Result { get; init; }

    /// <summary>Why the collection failed; absent when it succeeded.</summary>
    [JsonPropertyName("error")]
    public string? Error { get; init; }

    /// <summary>
    /// Whether the server cut the text short to send it; <see cref="OriginalSize"/> says how long it was.
    /// </summary>
    [JsonPropertyName("truncated")]
    public bool? IsTruncated { get; init; }

    /// <summary>How many bytes the text had before the server cut it short.</summary>
    [JsonPropertyName("original_size")]
    public long? OriginalSize { get; init; }

    /// <summary>How the value's lines end: <c>LF</c> or <c>CRLF</c>.</summary>
    [JsonPropertyName("eol")]
    public string? EndOfLine { get; init; }

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
        ZabbixRecordText.Member(builder, ref more, nameof(IsTruncated), IsTruncated);
        ZabbixRecordText.Member(builder, ref more, nameof(OriginalSize), OriginalSize);
        ZabbixRecordText.Member(builder, ref more, nameof(EndOfLine), EndOfLine);
        ZabbixRecordText.Count(builder, ref more, nameof(AdditionalFields), AdditionalFields);
        return more;
    }

    /// <summary>Whether <paramref name="other"/> holds the same values, its lists and fields compared by value.</summary>
    /// <param name="other">The other record.</param>
    /// <returns><see langword="true"/> when every member holds the same value; otherwise <see langword="false"/>.</returns>
    public bool Equals(ZabbixItemTestValue? other)
        => other is not null
            && string.Equals(Result, other.Result, StringComparison.Ordinal)
            && string.Equals(Error, other.Error, StringComparison.Ordinal)
            && Equals(IsTruncated, other.IsTruncated)
            && Equals(OriginalSize, other.OriginalSize)
            && string.Equals(EndOfLine, other.EndOfLine, StringComparison.Ordinal)
            && ZabbixValueEquality.DictionaryEqual(AdditionalFields, other.AdditionalFields);

    /// <inheritdoc/>
    public override int GetHashCode()
    {
        var hash = new HashCode();
        hash.Add(Result, StringComparer.Ordinal);
        hash.Add(Error, StringComparer.Ordinal);
        hash.Add(IsTruncated);
        hash.Add(OriginalSize);
        hash.Add(EndOfLine, StringComparer.Ordinal);
        ZabbixValueEquality.AddDictionary(ref hash, AdditionalFields);
        return hash.ToHashCode();
    }
}
