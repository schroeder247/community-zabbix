// This Source Code Form is subject to the terms of the Mozilla Public
// License, v. 2.0. Copyright (c) 2026 schroeder247. SPDX-License-Identifier: MPL-2.0
using System.Threading.Channels;

namespace Community.Zabbix.Proxy;

/// <summary>
/// Represents a host network discovery found: an address with the services a rule's checks saw on it, written to a proxy's
/// discovery store as the rows the server reads (<see cref="ToRows"/>; the verb is
/// <see cref="ZabbixProxyDiscovery.WriteAsync(ChannelWriter{ZabbixProxyDiscoveryItem}, ZabbixProxyDiscoveredHost, CancellationToken)"/>).
/// </summary>
/// <param name="NetworkDiscoveryRuleId">The discovery rule (<c>druleid</c>); not 0.</param>
/// <param name="Ip">The address.</param>
/// <param name="Clock">When the checks ran; the rows carry it in whole seconds.</param>
/// <param name="Services">The services found; empty when none answered, which makes the host down.</param>
/// <param name="Dns">The address's name, when the rule resolved one.</param>
public sealed record ZabbixProxyDiscoveredHost(ulong NetworkDiscoveryRuleId, string Ip, DateTimeOffset Clock, IReadOnlyList<ZabbixProxyDiscoveredService> Services, string? Dns = null)
{
    /// <summary>The discovery rule; positive.</summary>
    public ulong NetworkDiscoveryRuleId { get; init; } = NetworkDiscoveryRuleId != 0 ? NetworkDiscoveryRuleId : throw new ArgumentOutOfRangeException(nameof(NetworkDiscoveryRuleId), NetworkDiscoveryRuleId, "A discovery rule id is not 0.");

    /// <summary>The address.</summary>
    public string Ip { get; init; } = string.IsNullOrWhiteSpace(Ip) ? throw new ArgumentException("A discovered host has an address.", nameof(Ip)) : Ip;

    /// <summary>The services found.</summary>
    public IReadOnlyList<ZabbixProxyDiscoveredService> Services { get; init; } = Services ?? throw new ArgumentNullException(nameof(Services));

    /// <summary>Whether <paramref name="other"/> is the same host, its services element by element in order.</summary>
    /// <param name="other">The other host.</param>
    public bool Equals(ZabbixProxyDiscoveredHost? other)
        => other is not null && NetworkDiscoveryRuleId == other.NetworkDiscoveryRuleId && string.Equals(Ip, other.Ip, StringComparison.Ordinal) && Clock == other.Clock
            && ZabbixValueEquality.ListEqual(Services, other.Services) && string.Equals(Dns, other.Dns, StringComparison.Ordinal);

    /// <inheritdoc/>
    public override int GetHashCode()
    {
        var hash = new HashCode();
        hash.Add(NetworkDiscoveryRuleId);
        hash.Add(Ip, StringComparer.Ordinal);
        hash.Add(Clock);
        ZabbixValueEquality.AddList(ref hash, Services);
        return hash.ToHashCode();
    }

    /// <summary>
    /// The rows upstream's proxy writes for this host (<c>discoverer.c</c> <c>process_services</c>, then
    /// <c>zbx_discovery_update_interface_proxy</c> → <c>zbx_pb_discovery_write_host</c>): one row per service, then the row
    /// with <c>dcheck</c> 0, port 0 and value "" that closes the host — up when any service is up, down otherwise. The
    /// server (<c>proxy.c</c> <c>process_services</c>) processes an address's services only when that closing row arrives;
    /// until then it parks them in <c>proxy_dhistory</c>.
    /// </summary>
    /// <returns>The rows, in the order they are to be written.</returns>
    public IReadOnlyList<ZabbixProxyDiscoveryItem> ToRows()
    {
        const int Up = 0, Down = 1;                                                        // DOBJECT_STATUS_UP, DOBJECT_STATUS_DOWN
        DateTimeOffset clock = DateTimeOffset.FromUnixTimeSeconds(Clock.ToUnixTimeSeconds()); // the rows carry whole seconds
        string dns = Dns ?? string.Empty;
        var rows = new ZabbixProxyDiscoveryItem[Services.Count + 1];
        bool anyUp = false;
        for (int i = 0; i < Services.Count; i++)
        {
            ZabbixProxyDiscoveredService service = Services[i];
            anyUp |= service.IsUp;
            rows[i] = new ZabbixProxyDiscoveryItem(clock, NetworkDiscoveryRuleId, service.NetworkDiscoveryCheckId, Ip, dns, service.Port, service.Value ?? string.Empty, service.IsUp ? Up : Down);
        }
        rows[^1] = new ZabbixProxyDiscoveryItem(clock, NetworkDiscoveryRuleId, NetworkDiscoveryCheckId: 0UL, Ip, dns, Port: 0, Value: string.Empty, anyUp ? Up : Down);
        return rows;
    }
}
