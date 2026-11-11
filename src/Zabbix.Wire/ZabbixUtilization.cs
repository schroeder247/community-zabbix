// This Source Code Form is subject to the terms of the Mozilla Public
// License, v. 2.0. Copyright (c) 2026 schroeder247. SPDX-License-Identifier: MPL-2.0
using System.Text.Json.Serialization;

namespace Community.Zabbix;

/// <summary>
/// Represents how much of the last minute a group of processes spent in one state, as percentages: the average over the
/// processes, and the busiest and the least busy.
/// </summary>
public sealed record ZabbixUtilization
{
    /// <summary>The average over the processes, as a percentage.</summary>
    [JsonPropertyName("avg")]
    public double? Average { get; init; }

    /// <summary>The highest of the processes, as a percentage.</summary>
    [JsonPropertyName("max")]
    public double? Max { get; init; }

    /// <summary>The lowest of the processes, as a percentage.</summary>
    [JsonPropertyName("min")]
    public double? Min { get; init; }

    [System.Diagnostics.CodeAnalysis.SuppressMessage("CodeQuality", "IDE0051", Justification = "The record's synthesized ToString calls it; the analyser does not see the call.")]
    private bool PrintMembers(System.Text.StringBuilder builder)
    {
        bool more = false;
        ZabbixRecordText.Member(builder, ref more, nameof(Average), Average);
        ZabbixRecordText.Member(builder, ref more, nameof(Max), Max);
        ZabbixRecordText.Member(builder, ref more, nameof(Min), Min);
        return more;
    }
}
