// This Source Code Form is subject to the terms of the Mozilla Public
// License, v. 2.0. Copyright (c) 2026 schroeder247. SPDX-License-Identifier: MPL-2.0
using System.Text.Json.Serialization;

namespace Community.Zabbix;

/// <summary>
/// Represents what <c>status.get</c> answers: the counts the frontend's System information page shows, for the whole
/// installation and for each proxy.
/// </summary>
/// <remarks>
/// <para>
/// The server sends each count as an entry with the attributes it counts by (a host's status, an item's status and
/// state, a proxy), and an entry with no proxy, or proxy 0, for the whole installation (<c>status_stats_export</c> in
/// <c>trapper.c</c>); the record holds the whole installation's counts and lists each proxy's in <see cref="Proxies"/>.
/// </para>
/// <para>
/// Only a super admin's session gets the counts: for any other, every count is <see langword="null"/> and
/// <see cref="Proxies"/> is empty. A count is <see langword="null"/> too when the server couldn't read it (the template
/// and user counts come from the database).
/// </para>
/// </remarks>
public sealed record ZabbixServerStatus
{
    /// <summary>The server's version: a release candidate's suffix (<c>rc1</c>) is left out.</summary>
    public Version? Version { get; init; }

    /// <summary>How many templates there are.</summary>
    public long? TemplateCount { get; init; }

    /// <summary>How many hosts are enabled.</summary>
    public long? EnabledHostCount { get; init; }

    /// <summary>How many hosts are disabled.</summary>
    public long? DisabledHostCount { get; init; }

    /// <summary>How many items are enabled and supported.</summary>
    public long? EnabledItemCount { get; init; }

    /// <summary>How many items are disabled.</summary>
    public long? DisabledItemCount { get; init; }

    /// <summary>How many items are enabled but not supported.</summary>
    public long? UnsupportedItemCount { get; init; }

    /// <summary>How many enabled triggers are in the OK state.</summary>
    public long? OkTriggerCount { get; init; }

    /// <summary>How many enabled triggers are in the problem state.</summary>
    public long? ProblemTriggerCount { get; init; }

    /// <summary>How many triggers are disabled.</summary>
    public long? DisabledTriggerCount { get; init; }

    /// <summary>How many users are signed in now.</summary>
    public long? OnlineUserCount { get; init; }

    /// <summary>How many users aren't signed in.</summary>
    public long? OfflineUserCount { get; init; }

    /// <summary>How many new values per second the server's items need.</summary>
    public double? RequiredPerformance { get; init; }

    /// <summary>What each proxy monitors, by proxy; empty when the server has no proxies.</summary>
    public IReadOnlyList<ZabbixProxyLoad> Proxies { get; init; } = [];

    /// <summary>Whether <paramref name="other"/> holds the same counts, its proxies compared in order.</summary>
    /// <param name="other">The other status.</param>
    /// <returns><see langword="true"/> when every member holds the same value; otherwise <see langword="false"/>.</returns>
    public bool Equals(ZabbixServerStatus? other)
        => other is not null
            && Equals(Version, other.Version)
            && TemplateCount == other.TemplateCount
            && EnabledHostCount == other.EnabledHostCount
            && DisabledHostCount == other.DisabledHostCount
            && EnabledItemCount == other.EnabledItemCount
            && DisabledItemCount == other.DisabledItemCount
            && UnsupportedItemCount == other.UnsupportedItemCount
            && OkTriggerCount == other.OkTriggerCount
            && ProblemTriggerCount == other.ProblemTriggerCount
            && DisabledTriggerCount == other.DisabledTriggerCount
            && OnlineUserCount == other.OnlineUserCount
            && OfflineUserCount == other.OfflineUserCount
            && Equals(RequiredPerformance, other.RequiredPerformance)
            && ZabbixValueEquality.ListEqual(Proxies, other.Proxies);

    /// <inheritdoc/>
    public override int GetHashCode()
    {
        var hash = new HashCode();
        hash.Add(Version);
        hash.Add(TemplateCount);
        hash.Add(EnabledHostCount);
        hash.Add(DisabledHostCount);
        hash.Add(EnabledItemCount);
        hash.Add(DisabledItemCount);
        hash.Add(UnsupportedItemCount);
        hash.Add(OkTriggerCount);
        hash.Add(ProblemTriggerCount);
        hash.Add(DisabledTriggerCount);
        hash.Add(OnlineUserCount);
        hash.Add(OfflineUserCount);
        hash.Add(RequiredPerformance);
        ZabbixValueEquality.AddList(ref hash, Proxies);
        return hash.ToHashCode();
    }

    /// <summary>The counts the server sent, by what each counts: the whole installation's, and each proxy's.</summary>
    /// <param name="sections">The reply's <c>data</c>, read section by section.</param>
    /// <returns>The status.</returns>
    internal static ZabbixServerStatus From(ZabbixStatusSections sections)
    {
        var proxies = new SortedSet<ulong>();
        foreach (ZabbixStatusEntry entry in (sections.Hosts ?? []).Concat(sections.Items ?? []).Concat(sections.RequiredPerformance ?? []))
        {
            if (entry.Attributes?.ProxyId is ulong proxy and not 0) proxies.Add(proxy);
        }
        return new ZabbixServerStatus
        {
            Version = sections.Server?.Version,
            TemplateCount = Count(sections.Templates, null, static _ => true),
            EnabledHostCount = Count(sections.Hosts, 0, static a => a.Status == HostMonitored),
            DisabledHostCount = Count(sections.Hosts, 0, static a => a.Status == HostNotMonitored),
            EnabledItemCount = Count(sections.Items, 0, static a => a.Status == ItemActive && a.State == ItemNormal),
            UnsupportedItemCount = Count(sections.Items, 0, static a => a.Status == ItemActive && a.State == ItemNotSupported),
            DisabledItemCount = Count(sections.Items, 0, static a => a.Status == ItemDisabled),
            OkTriggerCount = Count(sections.Triggers, null, static a => a.Status == TriggerEnabled && a.Value == TriggerOk),
            ProblemTriggerCount = Count(sections.Triggers, null, static a => a.Status == TriggerEnabled && a.Value == TriggerProblem),
            DisabledTriggerCount = Count(sections.Triggers, null, static a => a.Status == TriggerDisabled),
            OnlineUserCount = Count(sections.Users, null, static a => a.Status == SessionActive),
            OfflineUserCount = Count(sections.Users, null, static a => a.Status == SessionPassive),
            RequiredPerformance = Sum(sections.RequiredPerformance, 0, static _ => true),
            Proxies = [.. proxies.Select(proxy => new ZabbixProxyLoad
            {
                ProxyId = proxy,
                EnabledHostCount = Count(sections.Hosts, proxy, static a => a.Status == HostMonitored),
                DisabledHostCount = Count(sections.Hosts, proxy, static a => a.Status == HostNotMonitored),
                EnabledItemCount = Count(sections.Items, proxy, static a => a.Status == ItemActive && a.State == ItemNormal),
                UnsupportedItemCount = Count(sections.Items, proxy, static a => a.Status == ItemActive && a.State == ItemNotSupported),
                DisabledItemCount = Count(sections.Items, proxy, static a => a.Status == ItemDisabled),
                RequiredPerformance = Sum(sections.RequiredPerformance, proxy, static _ => true),
            })],
        };
    }

    // The attribute values status_sections counts by (trapper.c): HOST_STATUS_*, ITEM_STATUS_*, ITEM_STATE_*,
    // TRIGGER_STATUS_*, TRIGGER_VALUE_* and ZBX_SESSION_*.
    private const int HostMonitored = 0, HostNotMonitored = 1;
    private const int ItemActive = 0, ItemDisabled = 1, ItemNormal = 0, ItemNotSupported = 1;
    private const int TriggerEnabled = 0, TriggerDisabled = 1, TriggerOk = 0, TriggerProblem = 1;
    private const int SessionActive = 0, SessionPassive = 1;

    // A section's entries for one proxy (null: a section without proxies) that match: absent when the section is.
    private static double? Sum(IReadOnlyList<ZabbixStatusEntry>? entries, ulong? proxy, Func<ZabbixStatusAttributes, bool> match)
        => entries?.Where(entry => (proxy is null || (entry.Attributes?.ProxyId ?? 0) == proxy) && match(entry.Attributes ?? ZabbixStatusAttributes.None))
            .Sum(entry => entry.Count);

    private static long? Count(IReadOnlyList<ZabbixStatusEntry>? entries, ulong? proxy, Func<ZabbixStatusAttributes, bool> match)
        => Sum(entries, proxy, match) is double sum ? (long)sum : null;
}

/// <summary>
/// The <c>data</c> of a <c>status.get</c> reply as the server writes it: a list of counted entries per section, and the
/// server's version (<c>status_sections</c> in <c>trapper.c</c>).
/// </summary>
internal sealed record ZabbixStatusSections
{
    [JsonPropertyName("template stats")]
    public IReadOnlyList<ZabbixStatusEntry>? Templates { get; init; }

    [JsonPropertyName("host stats")]
    public IReadOnlyList<ZabbixStatusEntry>? Hosts { get; init; }

    [JsonPropertyName("item stats")]
    public IReadOnlyList<ZabbixStatusEntry>? Items { get; init; }

    [JsonPropertyName("trigger stats")]
    public IReadOnlyList<ZabbixStatusEntry>? Triggers { get; init; }

    [JsonPropertyName("user stats")]
    public IReadOnlyList<ZabbixStatusEntry>? Users { get; init; }

    [JsonPropertyName("required performance")]
    public IReadOnlyList<ZabbixStatusEntry>? RequiredPerformance { get; init; }

    [JsonPropertyName("server stats")]
    public ZabbixStatusServer? Server { get; init; }
}

/// <summary>One counted entry: what it counts by, and the count (a number; the required performance's is its text).</summary>
internal sealed record ZabbixStatusEntry
{
    [JsonPropertyName("attributes")]
    public ZabbixStatusAttributes? Attributes { get; init; }

    [JsonPropertyName("count")]
    public double Count { get; init; }
}

/// <summary>What an entry counts by; each section names some of them.</summary>
internal sealed record ZabbixStatusAttributes
{
    /// <summary>An entry with no attributes, which counts everything in its section.</summary>
    public static ZabbixStatusAttributes None { get; } = new();

    [JsonPropertyName("proxyid")]
    public ulong? ProxyId { get; init; }

    [JsonPropertyName("status")]
    public int? Status { get; init; }

    [JsonPropertyName("state")]
    public int? State { get; init; }

    [JsonPropertyName("value")]
    public int? Value { get; init; }
}

/// <summary>The <c>server stats</c> section: the server's version.</summary>
internal sealed record ZabbixStatusServer
{
    [JsonPropertyName("version")]
    [JsonConverter(typeof(ZabbixVersionTextConverter))]
    public Version? Version { get; init; }
}
