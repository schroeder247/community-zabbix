// This Source Code Form is subject to the terms of the Mozilla Public
// License, v. 2.0. Copyright (c) 2026 schroeder247. SPDX-License-Identifier: MPL-2.0
using System.Text.Json.Serialization;

namespace Community.Zabbix;

/// <summary>
/// Represents the history write cache of a server or proxy, as <c>zabbix.stats</c> reports it (<c>wcache</c>): the
/// values it took, and how full its parts are.
/// </summary>
public sealed record ZabbixWriteCacheStatistics
{
    /// <summary>How many values the cache took, by value type.</summary>
    [JsonPropertyName("values")]
    public ZabbixHistoryValueCounts? Values { get; init; }

    /// <summary>The part that holds the values (<c>HistoryCacheSize</c>).</summary>
    [JsonPropertyName("history")]
    public ZabbixCacheUsage? History { get; init; }

    /// <summary>The part that indexes the values (<c>HistoryIndexCacheSize</c>).</summary>
    [JsonPropertyName("index")]
    public ZabbixCacheUsage? Index { get; init; }

    /// <summary>The trend cache (<c>TrendCacheSize</c>); a server's only.</summary>
    [JsonPropertyName("trend")]
    public ZabbixCacheUsage? Trend { get; init; }

    [System.Diagnostics.CodeAnalysis.SuppressMessage("CodeQuality", "IDE0051", Justification = "The record's synthesized ToString calls it; the analyser does not see the call.")]
    private bool PrintMembers(System.Text.StringBuilder builder)
    {
        bool more = false;
        ZabbixRecordText.Member(builder, ref more, nameof(Values), Values);
        ZabbixRecordText.Member(builder, ref more, nameof(History), History);
        ZabbixRecordText.Member(builder, ref more, nameof(Index), Index);
        ZabbixRecordText.Member(builder, ref more, nameof(Trend), Trend);
        return more;
    }
}
