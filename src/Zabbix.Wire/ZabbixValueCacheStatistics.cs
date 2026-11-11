// This Source Code Form is subject to the terms of the Mozilla Public
// License, v. 2.0. Copyright (c) 2026 schroeder247. SPDX-License-Identifier: MPL-2.0
using System.Text.Json.Serialization;

namespace Community.Zabbix;

/// <summary>
/// Represents a server's value cache, as <c>zabbix.stats</c> reports it (<c>vcache</c>): how full it is, and how it
/// answered.
/// </summary>
public sealed record ZabbixValueCacheStatistics
{
    /// <summary>How much of the cache is in use (<c>ValueCacheSize</c>).</summary>
    [JsonPropertyName("buffer")]
    public ZabbixCacheUsage? Buffer { get; init; }

    /// <summary>How the cache answered requests for item history.</summary>
    [JsonPropertyName("cache")]
    public ZabbixValueCacheActivity? Cache { get; init; }

    [System.Diagnostics.CodeAnalysis.SuppressMessage("CodeQuality", "IDE0051", Justification = "The record's synthesized ToString calls it; the analyser does not see the call.")]
    private bool PrintMembers(System.Text.StringBuilder builder)
    {
        bool more = false;
        ZabbixRecordText.Member(builder, ref more, nameof(Buffer), Buffer);
        ZabbixRecordText.Member(builder, ref more, nameof(Cache), Cache);
        return more;
    }
}
