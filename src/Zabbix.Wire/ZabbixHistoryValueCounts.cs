// This Source Code Form is subject to the terms of the Mozilla Public
// License, v. 2.0. Copyright (c) 2026 schroeder247. SPDX-License-Identifier: MPL-2.0
using System.Text.Json.Serialization;

namespace Community.Zabbix;

/// <summary>
/// Represents how many values the history write cache has taken since the server or proxy started, by value type
/// (<c>zabbix.stats</c>' <c>wcache.values</c>).
/// </summary>
public sealed record ZabbixHistoryValueCounts
{
    /// <summary>Values of every type.</summary>
    [JsonPropertyName("all")]
    public long? TotalCount { get; init; }

    /// <summary>Floating-point values.</summary>
    [JsonPropertyName("float")]
    public long? FloatCount { get; init; }

    /// <summary>Unsigned integer values.</summary>
    [JsonPropertyName("uint")]
    public long? UnsignedCount { get; init; }

    /// <summary>Character values.</summary>
    [JsonPropertyName("str")]
    public long? CharacterCount { get; init; }

    /// <summary>Log values.</summary>
    [JsonPropertyName("log")]
    public long? LogCount { get; init; }

    /// <summary>Text values.</summary>
    [JsonPropertyName("text")]
    public long? TextCount { get; init; }

    /// <summary>Binary values.</summary>
    [JsonPropertyName("bin")]
    public long? BinaryCount { get; init; }

    /// <summary>JSON values; Zabbix 8.0 and later.</summary>
    [JsonPropertyName("json")]
    public long? JsonCount { get; init; }

    /// <summary>Values that made their item not supported.</summary>
    [JsonPropertyName("not supported")]
    public long? NotSupportedCount { get; init; }

    [System.Diagnostics.CodeAnalysis.SuppressMessage("CodeQuality", "IDE0051", Justification = "The record's synthesized ToString calls it; the analyser does not see the call.")]
    private bool PrintMembers(System.Text.StringBuilder builder)
    {
        bool more = false;
        ZabbixRecordText.Member(builder, ref more, nameof(TotalCount), TotalCount);
        ZabbixRecordText.Member(builder, ref more, nameof(FloatCount), FloatCount);
        ZabbixRecordText.Member(builder, ref more, nameof(UnsignedCount), UnsignedCount);
        ZabbixRecordText.Member(builder, ref more, nameof(CharacterCount), CharacterCount);
        ZabbixRecordText.Member(builder, ref more, nameof(LogCount), LogCount);
        ZabbixRecordText.Member(builder, ref more, nameof(TextCount), TextCount);
        ZabbixRecordText.Member(builder, ref more, nameof(BinaryCount), BinaryCount);
        ZabbixRecordText.Member(builder, ref more, nameof(JsonCount), JsonCount);
        ZabbixRecordText.Member(builder, ref more, nameof(NotSupportedCount), NotSupportedCount);
        return more;
    }
}
