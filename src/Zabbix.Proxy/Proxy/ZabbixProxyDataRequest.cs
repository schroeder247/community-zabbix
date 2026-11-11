// This Source Code Form is subject to the terms of the Mozilla Public
// License, v. 2.0. Copyright (c) 2026 schroeder247. SPDX-License-Identifier: MPL-2.0
using System.Text.Json.Serialization;

namespace Community.Zabbix.Proxy;

/// <summary>
/// <c>proxy data</c>: a page of history, discovery, auto-registration, interface availability and task results — or, with
/// every list empty, the probe a held proxy sends, which carries no <c>history data</c> key because a server with uploads
/// disabled refuses any request that has one (<c>proxy_data_no_history</c>). An active proxy names itself
/// (<see cref="Request"/>, <see cref="Host"/>); a passive proxy's page, pulled by the server, carries neither
/// (<c>send_proxy_data</c>).
/// </summary>
public sealed record ZabbixProxyDataRequest(
    [property: JsonPropertyName("request")] string? Request,
    [property: JsonPropertyName("host")] string? Host,
    [property: JsonPropertyName("session")] string Session,
    [property: JsonPropertyName("history data")] IReadOnlyList<ZabbixProxyHistoryItem>? History,
    [property: JsonPropertyName("discovery data")] IReadOnlyList<ZabbixProxyDiscoveryItem>? Discovery,
    [property: JsonPropertyName("auto registration")] IReadOnlyList<ZabbixProxyAutoregistrationItem>? Autoregistration,
    [property: JsonPropertyName("interface availability")] IReadOnlyList<ZabbixProxyAvailabilityItem>? Availability,
    [property: JsonPropertyName("tasks")] IReadOnlyList<ZabbixProxyTaskResult>? Tasks,
    [property: JsonPropertyName("more")] int? More,
    [property: JsonPropertyName("version"), JsonConverter(typeof(ZabbixVersionTextConverter))] Version Version,
    DateTimeOffset Clock,
    [property: JsonPropertyName("proxy_delay")] long? ProxyDelay)
{
    /// <summary>When the value was taken, to 100 ns.</summary>
    [JsonIgnore]
    public DateTimeOffset Clock { get => Exact.ToDateTimeOffset(); init => Exact = ZabbixClock.FromDateTimeOffset(value); }

    internal ZabbixClock Exact { get; init; } = ZabbixClock.FromDateTimeOffset(Clock);

    [JsonInclude]
    [JsonPropertyName("clock")]
    internal long ClockSeconds { get => Exact.Seconds; init => Exact = ZabbixClockFields.WithSeconds(Exact, value); }

    [JsonInclude]
    [JsonPropertyName("ns")]
    internal int ClockNanoseconds { get => Exact.Nanoseconds; init => Exact = ZabbixClockFields.WithNanoseconds(Exact, value, "ns"); }

    /// <summary>Whether <paramref name="other"/> is the same request: its lists element by element in order.</summary>
    /// <param name="other">The other request.</param>
    public bool Equals(ZabbixProxyDataRequest? other)
        => other is not null && string.Equals(Request, other.Request, StringComparison.Ordinal) && string.Equals(Host, other.Host, StringComparison.Ordinal)
            && string.Equals(Session, other.Session, StringComparison.Ordinal)
            && ZabbixValueEquality.ListEqual(History, other.History) && ZabbixValueEquality.ListEqual(Discovery, other.Discovery)
            && ZabbixValueEquality.ListEqual(Autoregistration, other.Autoregistration) && ZabbixValueEquality.ListEqual(Availability, other.Availability)
            && ZabbixValueEquality.ListEqual(Tasks, other.Tasks) && More == other.More && Equals(Version, other.Version)
            && Exact == other.Exact && ProxyDelay == other.ProxyDelay;

    /// <inheritdoc/>
    public override int GetHashCode()
    {
        var hash = new HashCode();
        hash.Add(Session, StringComparer.Ordinal);
        ZabbixValueEquality.AddList(ref hash, History);
        ZabbixValueEquality.AddList(ref hash, Discovery);
        ZabbixValueEquality.AddList(ref hash, Autoregistration);
        ZabbixValueEquality.AddList(ref hash, Availability);
        ZabbixValueEquality.AddList(ref hash, Tasks);
        hash.Add(Clock);
        return hash.ToHashCode();
    }
}
