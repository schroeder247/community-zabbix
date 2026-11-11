// This Source Code Form is subject to the terms of the Mozilla Public
// License, v. 2.0. Copyright (c) 2026 schroeder247. SPDX-License-Identifier: MPL-2.0
using System.Text.Json;
using System.Text.Json.Serialization;

namespace Community.Zabbix;

/// <summary>Represents one recipient of a test report and whether the server sent it the report.</summary>
public sealed record ZabbixReportRecipient : IZabbixAdditionalFields
{
    /// <summary>Whether the report was sent: 1 sent, 0 not.</summary>
    [JsonPropertyName("status")]
    public int? Status { get; init; }

    /// <summary>Who the report went to, as the media type addresses them.</summary>
    [JsonPropertyName("recipient")]
    public string? Recipient { get; init; }

    /// <summary>Why it wasn't sent, when it wasn't.</summary>
    [JsonPropertyName("info")]
    public string? Info { get; init; }

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
        ZabbixRecordText.Member(builder, ref more, nameof(Status), Status);
        ZabbixRecordText.Member(builder, ref more, nameof(Recipient), Recipient);
        ZabbixRecordText.Member(builder, ref more, nameof(Info), Info);
        ZabbixRecordText.Count(builder, ref more, nameof(AdditionalFields), AdditionalFields);
        return more;
    }

    /// <summary>Whether <paramref name="other"/> holds the same values, its lists and fields compared by value.</summary>
    /// <param name="other">The other record.</param>
    /// <returns><see langword="true"/> when every member holds the same value; otherwise <see langword="false"/>.</returns>
    public bool Equals(ZabbixReportRecipient? other)
        => other is not null
            && Equals(Status, other.Status)
            && string.Equals(Recipient, other.Recipient, StringComparison.Ordinal)
            && string.Equals(Info, other.Info, StringComparison.Ordinal)
            && ZabbixValueEquality.DictionaryEqual(AdditionalFields, other.AdditionalFields);

    /// <inheritdoc/>
    public override int GetHashCode()
    {
        var hash = new HashCode();
        hash.Add(Status);
        hash.Add(Recipient, StringComparer.Ordinal);
        hash.Add(Info, StringComparer.Ordinal);
        ZabbixValueEquality.AddDictionary(ref hash, AdditionalFields);
        return hash.ToHashCode();
    }
}
