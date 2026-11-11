// This Source Code Form is subject to the terms of the Mozilla Public
// License, v. 2.0. Copyright (c) 2026 schroeder247. SPDX-License-Identifier: MPL-2.0
using System.Text.Json;
using System.Text.Json.Serialization;

namespace Community.Zabbix;

/// <summary>
/// Represents what <c>zabbix.stats</c> answers: a server's or a proxy's internal statistics, the values of its
/// <c>zabbix[...]</c> internal items.
/// </summary>
/// <remarks>
/// Zabbix builds the reply from the component's parts, so it differs between a server and a proxy and between versions.
/// The record types what every component of 7.0, 7.4 and 8.0 answers alike and keeps the rest reachable through
/// IZabbixAdditionalFields, as the server sent it: a server's HA nodes (ha), proxies (proxy), values-per-second limit
/// (vps) and trend function cache (tcache), the preprocessing manager's queues (preprocessing), and a proxy's buffer
/// (proxy buffer) and its own name, mode and encryption.
/// </remarks>
public sealed record ZabbixServerStatistics : IZabbixAdditionalFields
{
    /// <summary>When the server or proxy started.</summary>
    [JsonPropertyName("boottime")]
    [JsonConverter(typeof(ZabbixUnixTimeConverter))]
    public DateTimeOffset? BootTime { get; init; }

    /// <summary>How long the server or proxy has run.</summary>
    [JsonPropertyName("uptime")]
    [JsonConverter(typeof(ZabbixSecondsConverter))]
    public TimeSpan? Uptime { get; init; }

    /// <summary>The server's or proxy's version: a release candidate's suffix (<c>rc1</c>) is left out.</summary>
    [JsonPropertyName("version")]
    [JsonConverter(typeof(ZabbixVersionTextConverter))]
    public Version? Version { get; init; }

    /// <summary>How many hosts it monitors.</summary>
    [JsonPropertyName("hosts")]
    public long? HostCount { get; init; }

    /// <summary>How many items it monitors.</summary>
    [JsonPropertyName("items")]
    public long? ItemCount { get; init; }

    /// <summary>How many of its items are not supported.</summary>
    [JsonPropertyName("items_unsupported")]
    public long? UnsupportedItemCount { get; init; }

    /// <summary>How many new values per second it is expected to take.</summary>
    [JsonPropertyName("requiredperformance")]
    public double? RequiredPerformance { get; init; }

    /// <summary>The configuration cache (<c>CacheSize</c>).</summary>
    [JsonPropertyName("rcache")]
    public ZabbixCacheUsage? ConfigurationCache { get; init; }

    /// <summary>The history write cache.</summary>
    [JsonPropertyName("wcache")]
    public ZabbixWriteCacheStatistics? WriteCache { get; init; }

    /// <summary>The value cache; a server's only.</summary>
    [JsonPropertyName("vcache")]
    public ZabbixValueCacheStatistics? ValueCache { get; init; }

    /// <summary>The VMware cache (<c>VMwareCacheSize</c>); only while VMware collectors run.</summary>
    [JsonPropertyName("vmware")]
    public ZabbixCacheUsage? VMwareCache { get; init; }

    /// <summary>How many values wait for preprocessing.</summary>
    [JsonPropertyName("preprocessing_queue")]
    public long? PreprocessingQueue { get; init; }

    /// <summary>How many network discovery checks wait to run.</summary>
    [JsonPropertyName("discovery_queue")]
    public long? DiscoveryQueue { get; init; }

    /// <summary>How many low-level discovery values wait to be processed; a server's only.</summary>
    [JsonPropertyName("lld_queue")]
    public long? LowLevelDiscoveryQueue { get; init; }

    /// <summary>How many values wait for a connector; a server's only.</summary>
    [JsonPropertyName("connector_queue")]
    public long? ConnectorQueue { get; init; }

    /// <summary>How many triggers the server has; a server's only.</summary>
    [JsonPropertyName("triggers")]
    public long? TriggerCount { get; init; }

    /// <summary>
    /// The processes by type, by the name the <c>zabbix[process,...]</c> item takes: <c>poller</c>, <c>trapper</c>,
    /// <c>preprocessing worker</c>. Which types there are depends on the component and its version.
    /// </summary>
    [JsonPropertyName("process")]
    public IReadOnlyDictionary<string, ZabbixProcessUtilization>? Processes { get; init; }

    /// <summary>Every field the reply carried that this record doesn't model, kept as the server sent it.</summary>
    [JsonExtensionData]
    [JsonInclude]
    internal IDictionary<string, JsonElement>? AdditionalFields { get; set; }

    /// <inheritdoc/>
    IDictionary<string, JsonElement>? IZabbixAdditionalFields.AdditionalFields => AdditionalFields;

    [System.Diagnostics.CodeAnalysis.SuppressMessage("CodeQuality", "IDE0051", Justification = "The record's synthesized ToString calls it; the analyser does not see the call.")]
    private bool PrintMembers(System.Text.StringBuilder builder)
    {
        bool more = false;
        ZabbixRecordText.Member(builder, ref more, nameof(BootTime), BootTime);
        ZabbixRecordText.Member(builder, ref more, nameof(Uptime), Uptime);
        ZabbixRecordText.Member(builder, ref more, nameof(Version), Version);
        ZabbixRecordText.Member(builder, ref more, nameof(HostCount), HostCount);
        ZabbixRecordText.Member(builder, ref more, nameof(ItemCount), ItemCount);
        ZabbixRecordText.Member(builder, ref more, nameof(UnsupportedItemCount), UnsupportedItemCount);
        ZabbixRecordText.Member(builder, ref more, nameof(RequiredPerformance), RequiredPerformance);
        ZabbixRecordText.Member(builder, ref more, nameof(ConfigurationCache), ConfigurationCache);
        ZabbixRecordText.Member(builder, ref more, nameof(WriteCache), WriteCache);
        ZabbixRecordText.Member(builder, ref more, nameof(ValueCache), ValueCache);
        ZabbixRecordText.Member(builder, ref more, nameof(VMwareCache), VMwareCache);
        ZabbixRecordText.Member(builder, ref more, nameof(PreprocessingQueue), PreprocessingQueue);
        ZabbixRecordText.Member(builder, ref more, nameof(DiscoveryQueue), DiscoveryQueue);
        ZabbixRecordText.Member(builder, ref more, nameof(LowLevelDiscoveryQueue), LowLevelDiscoveryQueue);
        ZabbixRecordText.Member(builder, ref more, nameof(ConnectorQueue), ConnectorQueue);
        ZabbixRecordText.Member(builder, ref more, nameof(TriggerCount), TriggerCount);
        ZabbixRecordText.Count(builder, ref more, nameof(Processes), Processes);
        ZabbixRecordText.Count(builder, ref more, nameof(AdditionalFields), AdditionalFields);
        return more;
    }

    /// <summary>Whether <paramref name="other"/> holds the same values, its lists and fields compared by value.</summary>
    /// <param name="other">The other record.</param>
    /// <returns><see langword="true"/> when every member holds the same value; otherwise <see langword="false"/>.</returns>
    public bool Equals(ZabbixServerStatistics? other)
        => other is not null
            && Equals(BootTime, other.BootTime)
            && Equals(Uptime, other.Uptime)
            && Equals(Version, other.Version)
            && Equals(HostCount, other.HostCount)
            && Equals(ItemCount, other.ItemCount)
            && Equals(UnsupportedItemCount, other.UnsupportedItemCount)
            && Equals(RequiredPerformance, other.RequiredPerformance)
            && Equals(ConfigurationCache, other.ConfigurationCache)
            && Equals(WriteCache, other.WriteCache)
            && Equals(ValueCache, other.ValueCache)
            && Equals(VMwareCache, other.VMwareCache)
            && Equals(PreprocessingQueue, other.PreprocessingQueue)
            && Equals(DiscoveryQueue, other.DiscoveryQueue)
            && Equals(LowLevelDiscoveryQueue, other.LowLevelDiscoveryQueue)
            && Equals(ConnectorQueue, other.ConnectorQueue)
            && Equals(TriggerCount, other.TriggerCount)
            && ZabbixValueEquality.DictionaryEqual(Processes, other.Processes)
            && ZabbixValueEquality.DictionaryEqual(AdditionalFields, other.AdditionalFields);

    /// <inheritdoc/>
    public override int GetHashCode()
    {
        var hash = new HashCode();
        hash.Add(BootTime);
        hash.Add(Uptime);
        hash.Add(Version);
        hash.Add(HostCount);
        hash.Add(ItemCount);
        hash.Add(UnsupportedItemCount);
        hash.Add(RequiredPerformance);
        hash.Add(ConfigurationCache);
        hash.Add(WriteCache);
        hash.Add(ValueCache);
        hash.Add(VMwareCache);
        hash.Add(PreprocessingQueue);
        hash.Add(DiscoveryQueue);
        hash.Add(LowLevelDiscoveryQueue);
        hash.Add(ConnectorQueue);
        hash.Add(TriggerCount);
        ZabbixValueEquality.AddDictionary(ref hash, Processes);
        ZabbixValueEquality.AddDictionary(ref hash, AdditionalFields);
        return hash.ToHashCode();
    }
}
