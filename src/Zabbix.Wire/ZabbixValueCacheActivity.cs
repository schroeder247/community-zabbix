// This Source Code Form is subject to the terms of the Mozilla Public
// License, v. 2.0. Copyright (c) 2026 schroeder247. SPDX-License-Identifier: MPL-2.0
using System.Text.Json.Serialization;

namespace Community.Zabbix;

/// <summary>
/// Represents how the value cache answered requests for item history, as <c>zabbix.stats</c> reports it
/// (<c>vcache.cache</c>).
/// </summary>
public sealed record ZabbixValueCacheActivity
{
    /// <summary>How many requests the cache answered.</summary>
    [JsonPropertyName("requests")]
    public long? RequestCount { get; init; }

    /// <summary>How many it answered from memory.</summary>
    [JsonPropertyName("hits")]
    public long? HitCount { get; init; }

    /// <summary>How many it read from the database.</summary>
    [JsonPropertyName("misses")]
    public long? MissCount { get; init; }

    /// <summary>Whether the cache works normally or has run low on memory.</summary>
    [JsonPropertyName("mode")]
    public ZabbixValueCacheMode? Mode { get; init; }

    [System.Diagnostics.CodeAnalysis.SuppressMessage("CodeQuality", "IDE0051", Justification = "The record's synthesized ToString calls it; the analyser does not see the call.")]
    private bool PrintMembers(System.Text.StringBuilder builder)
    {
        bool more = false;
        ZabbixRecordText.Member(builder, ref more, nameof(RequestCount), RequestCount);
        ZabbixRecordText.Member(builder, ref more, nameof(HitCount), HitCount);
        ZabbixRecordText.Member(builder, ref more, nameof(MissCount), MissCount);
        ZabbixRecordText.Member(builder, ref more, nameof(Mode), Mode);
        return more;
    }
}
