// This Source Code Form is subject to the terms of the Mozilla Public
// License, v. 2.0. Copyright (c) 2026 schroeder247. SPDX-License-Identifier: MPL-2.0
using System.Text.Json.Serialization;

namespace Community.Zabbix;

/// <summary>
/// Represents how much of one of the server's or proxy's shared memory caches is in use, as <c>zabbix.stats</c> reports
/// it.
/// </summary>
public sealed record ZabbixCacheUsage
{
    /// <summary>The cache's size, in bytes.</summary>
    [JsonPropertyName("total")]
    public long? TotalBytes { get; init; }

    /// <summary>How much of the cache is free, in bytes.</summary>
    [JsonPropertyName("free")]
    public long? FreeBytes { get; init; }

    /// <summary>How much of the cache is in use, in bytes.</summary>
    [JsonPropertyName("used")]
    public long? UsedBytes { get; init; }

    /// <summary>How much of the cache is free, as a percentage.</summary>
    [JsonPropertyName("pfree")]
    public double? FreePercent { get; init; }

    /// <summary>How much of the cache is in use, as a percentage.</summary>
    [JsonPropertyName("pused")]
    public double? UsedPercent { get; init; }

    [System.Diagnostics.CodeAnalysis.SuppressMessage("CodeQuality", "IDE0051", Justification = "The record's synthesized ToString calls it; the analyser does not see the call.")]
    private bool PrintMembers(System.Text.StringBuilder builder)
    {
        bool more = false;
        ZabbixRecordText.Member(builder, ref more, nameof(TotalBytes), TotalBytes);
        ZabbixRecordText.Member(builder, ref more, nameof(FreeBytes), FreeBytes);
        ZabbixRecordText.Member(builder, ref more, nameof(UsedBytes), UsedBytes);
        ZabbixRecordText.Member(builder, ref more, nameof(FreePercent), FreePercent);
        ZabbixRecordText.Member(builder, ref more, nameof(UsedPercent), UsedPercent);
        return more;
    }
}
