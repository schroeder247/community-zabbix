// This Source Code Form is subject to the terms of the Mozilla Public
// License, v. 2.0. Copyright (c) 2026 schroeder247. SPDX-License-Identifier: MPL-2.0
using System.Text.Json.Serialization;

namespace Community.Zabbix;

/// <summary>
/// Represents the processes of one type a server or proxy runs (pollers, trappers, preprocessing workers), as
/// <c>zabbix.stats</c> reports them (<c>process</c>): how busy and how idle they were, and how many there are.
/// </summary>
public sealed record ZabbixProcessUtilization
{
    /// <summary>How much of the last minute the processes were busy.</summary>
    [JsonPropertyName("busy")]
    public ZabbixUtilization? Busy { get; init; }

    /// <summary>How much of the last minute the processes were idle.</summary>
    [JsonPropertyName("idle")]
    public ZabbixUtilization? Idle { get; init; }

    /// <summary>How many processes of the type run.</summary>
    [JsonPropertyName("count")]
    public int? Count { get; init; }

    [System.Diagnostics.CodeAnalysis.SuppressMessage("CodeQuality", "IDE0051", Justification = "The record's synthesized ToString calls it; the analyser does not see the call.")]
    private bool PrintMembers(System.Text.StringBuilder builder)
    {
        bool more = false;
        ZabbixRecordText.Member(builder, ref more, nameof(Busy), Busy);
        ZabbixRecordText.Member(builder, ref more, nameof(Idle), Idle);
        ZabbixRecordText.Member(builder, ref more, nameof(Count), Count);
        return more;
    }
}
