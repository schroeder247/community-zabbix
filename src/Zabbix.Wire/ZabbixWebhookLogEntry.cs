// This Source Code Form is subject to the terms of the Mozilla Public
// License, v. 2.0. Copyright (c) 2026 schroeder247. SPDX-License-Identifier: MPL-2.0
using System.Text.Json.Serialization;

namespace Community.Zabbix;

/// <summary>Represents one line a webhook wrote with <c>Zabbix.log</c> while it ran.</summary>
public sealed record ZabbixWebhookLogEntry
{
    /// <summary>The level the webhook passed to <c>Zabbix.log</c>, from 1 (critical) to 5 (trace).</summary>
    [JsonPropertyName("level")]
    public int? Level { get; init; }

    /// <summary>How long the webhook had run when it wrote the line.</summary>
    [JsonPropertyName("ms")]
    [JsonConverter(typeof(ZabbixMillisecondsConverter))]
    public TimeSpan? Elapsed { get; init; }

    /// <summary>The text the webhook wrote.</summary>
    [JsonPropertyName("message")]
    public string? Message { get; init; }

    [System.Diagnostics.CodeAnalysis.SuppressMessage("CodeQuality", "IDE0051", Justification = "The record's synthesized ToString calls it; the analyser does not see the call.")]
    private bool PrintMembers(System.Text.StringBuilder builder)
    {
        bool more = false;
        ZabbixRecordText.Member(builder, ref more, nameof(Level), Level);
        ZabbixRecordText.Member(builder, ref more, nameof(Elapsed), Elapsed);
        ZabbixRecordText.Member(builder, ref more, nameof(Message), Message);
        return more;
    }
}
