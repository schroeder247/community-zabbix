// This Source Code Form is subject to the terms of the Mozilla Public
// License, v. 2.0. Copyright (c) 2026 schroeder247. SPDX-License-Identifier: MPL-2.0
using System.Text.Json.Serialization;

namespace Community.Zabbix;

/// <summary>Represents a webhook's log: what it wrote with <c>Zabbix.log</c>, and how long it ran.</summary>
public sealed record ZabbixWebhookLog
{
    /// <summary>What the webhook logged, in the order it logged it.</summary>
    [JsonPropertyName("logs")]
    public IReadOnlyList<ZabbixWebhookLogEntry>? Entries { get; init; }

    /// <summary>How long the webhook ran.</summary>
    [JsonPropertyName("ms")]
    [JsonConverter(typeof(ZabbixMillisecondsConverter))]
    public TimeSpan? Elapsed { get; init; }

    [System.Diagnostics.CodeAnalysis.SuppressMessage("CodeQuality", "IDE0051", Justification = "The record's synthesized ToString calls it; the analyser does not see the call.")]
    private bool PrintMembers(System.Text.StringBuilder builder)
    {
        bool more = false;
        ZabbixRecordText.Count(builder, ref more, nameof(Entries), Entries);
        ZabbixRecordText.Member(builder, ref more, nameof(Elapsed), Elapsed);
        return more;
    }

    /// <summary>Whether <paramref name="other"/> holds the same values, its lists and fields compared by value.</summary>
    /// <param name="other">The other record.</param>
    /// <returns><see langword="true"/> when every member holds the same value; otherwise <see langword="false"/>.</returns>
    public bool Equals(ZabbixWebhookLog? other)
        => other is not null
            && ZabbixValueEquality.ListEqual(Entries, other.Entries)
            && Equals(Elapsed, other.Elapsed);

    /// <inheritdoc/>
    public override int GetHashCode()
    {
        var hash = new HashCode();
        ZabbixValueEquality.AddList(ref hash, Entries);
        hash.Add(Elapsed);
        return hash.ToHashCode();
    }
}
