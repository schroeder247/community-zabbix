// This Source Code Form is subject to the terms of the Mozilla Public
// License, v. 2.0. Copyright (c) 2026 schroeder247. SPDX-License-Identifier: MPL-2.0
using System.Text.Json.Serialization;

namespace Community.Zabbix.Connector;

/// <summary>An event the server streamed.</summary>
public sealed record ZabbixConnectorEvent : ZabbixConnectorRecord
{
    /// <summary>The event.</summary>
    [JsonPropertyName("eventid")]
    public ulong EventId { get; init; }

    /// <summary>The event's name.</summary>
    [JsonPropertyName("name")]
    public string? Name { get; init; }

    /// <summary>The severity.</summary>
    [JsonPropertyName("severity")]
    public int? Severity { get; init; }

    /// <summary>1 for a problem, 0 for its recovery.</summary>
    [JsonPropertyName("value")]
    public int Value { get; init; }

    /// <summary>The problem this recovers, when it is a recovery.</summary>
    [JsonPropertyName("p_eventid")]
    public ulong? ProblemEventId { get; init; }

    /// <summary>The hosts.</summary>
    [JsonPropertyName("hosts")]
    public IReadOnlyList<ZabbixConnectorHost>? Hosts { get; init; }

    /// <summary>The groups.</summary>
    [JsonPropertyName("groups")]
    public IReadOnlyList<string>? Groups { get; init; }

    /// <summary>The tags.</summary>
    [JsonPropertyName("tags")]
    public IReadOnlyList<ZabbixTag>? Tags { get; init; }

    /// <summary>Whether <paramref name="other"/> is the same event: its hosts, groups and tags in order.</summary>
    /// <param name="other">The other event.</param>
    public bool Equals(ZabbixConnectorEvent? other)
        => base.Equals(other) && EventId == other.EventId && string.Equals(Name, other.Name, StringComparison.Ordinal) && Severity == other.Severity
            && Value == other.Value && ProblemEventId == other.ProblemEventId
            && ZabbixValueEquality.ListEqual(Hosts, other.Hosts) && ZabbixValueEquality.ListEqual(Groups, other.Groups) && ZabbixValueEquality.ListEqual(Tags, other.Tags);

    /// <inheritdoc/>
    public override int GetHashCode()
    {
        var hash = new HashCode();
        hash.Add(base.GetHashCode());
        hash.Add(EventId);
        hash.Add(Name, StringComparer.Ordinal);
        ZabbixValueEquality.AddList(ref hash, Hosts);
        ZabbixValueEquality.AddList(ref hash, Groups);
        ZabbixValueEquality.AddList(ref hash, Tags);
        return hash.ToHashCode();
    }
}
