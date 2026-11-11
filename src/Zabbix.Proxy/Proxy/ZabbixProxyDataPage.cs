// This Source Code Form is subject to the terms of the Mozilla Public
// License, v. 2.0. Copyright (c) 2026 schroeder247. SPDX-License-Identifier: MPL-2.0
namespace Community.Zabbix.Proxy;

/// <summary>
/// One <c>proxy data</c> page to send: the four typed lists (history, discovery, auto-registration, interface
/// availability) plus task results, each <see langword="null"/> or empty when this page carries none of that kind.
/// </summary>
/// <param name="History">Values, by item id, with the store's sequence as their id.</param>
/// <param name="Discovery">Discovery check results.</param>
/// <param name="Autoregistration">Unknown active-agent hosts offered for registration.</param>
/// <param name="Availability">Interface availability changes.</param>
/// <param name="Tasks">Task results, for a task the server hands back on <see cref="ZabbixProxyResponse.Tasks"/>.</param>
public sealed record ZabbixProxyDataPage(
    IReadOnlyList<ZabbixProxyHistoryItem>? History = null,
    IReadOnlyList<ZabbixProxyDiscoveryItem>? Discovery = null,
    IReadOnlyList<ZabbixProxyAutoregistrationItem>? Autoregistration = null,
    IReadOnlyList<ZabbixProxyAvailabilityItem>? Availability = null,
    IReadOnlyList<ZabbixProxyTaskResult>? Tasks = null)
{
    /// <summary>An empty page: the probe a held proxy sends.</summary>
    public static ZabbixProxyDataPage Empty { get; } = new();

    /// <summary>Whether every list is empty or absent.</summary>
    public bool IsEmpty => Count(History) == 0 && Count(Discovery) == 0 && Count(Autoregistration) == 0 && Count(Availability) == 0 && Count(Tasks) == 0;

    /// <summary>Whether <paramref name="other"/> carries the same lists, element by element in order.</summary>
    /// <param name="other">The other page.</param>
    public bool Equals(ZabbixProxyDataPage? other)
        => other is not null && ZabbixValueEquality.ListEqual(History, other.History) && ZabbixValueEquality.ListEqual(Discovery, other.Discovery)
            && ZabbixValueEquality.ListEqual(Autoregistration, other.Autoregistration) && ZabbixValueEquality.ListEqual(Availability, other.Availability)
            && ZabbixValueEquality.ListEqual(Tasks, other.Tasks);

    /// <inheritdoc/>
    public override int GetHashCode()
    {
        var hash = new HashCode();
        ZabbixValueEquality.AddList(ref hash, History);
        ZabbixValueEquality.AddList(ref hash, Discovery);
        ZabbixValueEquality.AddList(ref hash, Autoregistration);
        ZabbixValueEquality.AddList(ref hash, Availability);
        ZabbixValueEquality.AddList(ref hash, Tasks);
        return hash.ToHashCode();
    }

    private static int Count<T>(IReadOnlyList<T>? list) => list?.Count ?? 0;
}
