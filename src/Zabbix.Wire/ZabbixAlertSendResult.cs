// This Source Code Form is subject to the terms of the Mozilla Public
// License, v. 2.0. Copyright (c) 2026 schroeder247. SPDX-License-Identifier: MPL-2.0
using System.Text.Json.Serialization;

namespace Community.Zabbix;

/// <summary>
/// Represents what <c>alert.send</c> answers when the test message went out: what a webhook returned, and its log.
/// </summary>
public sealed record ZabbixAlertSendResult
{
    /// <summary>What a webhook returned; absent for the other media types.</summary>
    [JsonPropertyName("data")]
    public string? Value { get; init; }

    /// <summary>A webhook's log and how long it ran; absent for the other media types.</summary>
    [JsonPropertyName("debug")]
    public ZabbixWebhookLog? Log { get; init; }

    [System.Diagnostics.CodeAnalysis.SuppressMessage("CodeQuality", "IDE0051", Justification = "The record's synthesized ToString calls it; the analyser does not see the call.")]
    private bool PrintMembers(System.Text.StringBuilder builder)
    {
        bool more = false;
        ZabbixRecordText.Member(builder, ref more, nameof(Value), Value);
        ZabbixRecordText.Member(builder, ref more, nameof(Log), Log);
        return more;
    }
}
