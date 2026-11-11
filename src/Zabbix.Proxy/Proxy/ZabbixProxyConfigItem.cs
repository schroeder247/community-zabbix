// This Source Code Form is subject to the terms of the Mozilla Public
// License, v. 2.0. Copyright (c) 2026 schroeder247. SPDX-License-Identifier: MPL-2.0
namespace Community.Zabbix.Proxy;

/// <summary>An item the proxy monitors, as the server's <c>items</c> table sends it: what admission needs.</summary>
/// <param name="ItemId">The item's id: what <c>proxy data</c> carries.</param>
/// <param name="HostId">Its host.</param>
/// <param name="Key">The key, verbatim: Zabbix matches a sender's key literally.</param>
/// <param name="Type">The item type.</param>
/// <param name="Status">Whether it is collected.</param>
/// <param name="TrapperHosts">The hosts that may send traps, macros unresolved; empty when the item names none.</param>
/// <param name="AllowTraps">For an HTTP agent item: whether it also takes traps.</param>
/// <param name="Delay">The update interval, macros unresolved; what <c>active checks</c> answers with.</param>
/// <param name="Timeout">The item's own check timeout, macros unresolved; <see langword="null"/> when the item has none.</param>
public sealed record ZabbixProxyConfigItem(ulong ItemId, ulong HostId, string Key, ZabbixItemType Type, ZabbixItemStatus Status, IReadOnlyList<ZabbixPeer> TrapperHosts, bool AllowTraps, ZabbixInterval Delay = default, ZabbixDuration? Timeout = null)
{
    /// <summary>Whether <paramref name="other"/> is the same item: every member equal, the trapper hosts in the same order.</summary>
    /// <param name="other">The item to compare with this one; <see langword="null"/> is never equal.</param>
    /// <returns><see langword="true"/> when every member holds the same value; otherwise <see langword="false"/>.</returns>
    public bool Equals(ZabbixProxyConfigItem? other)
        => other is not null && ItemId == other.ItemId && HostId == other.HostId && string.Equals(Key, other.Key, StringComparison.Ordinal)
            && Type == other.Type && Status == other.Status && ZabbixValueEquality.ListEqual(TrapperHosts, other.TrapperHosts)
            && AllowTraps == other.AllowTraps && Delay == other.Delay && Timeout == other.Timeout;

    /// <inheritdoc/>
    public override int GetHashCode()
    {
        var hash = new HashCode();
        hash.Add(ItemId);
        hash.Add(HostId);
        hash.Add(Key, StringComparer.Ordinal);
        hash.Add(Type);
        hash.Add(Status);
        ZabbixValueEquality.AddList(ref hash, TrapperHosts);
        hash.Add(AllowTraps);
        hash.Add(Delay);
        hash.Add(Timeout);
        return hash.ToHashCode();
    }
}
