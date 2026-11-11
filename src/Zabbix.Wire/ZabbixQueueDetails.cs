// This Source Code Form is subject to the terms of the Mozilla Public
// License, v. 2.0. Copyright (c) 2026 schroeder247. SPDX-License-Identifier: MPL-2.0

namespace Community.Zabbix;

/// <summary>The queue in detail: the most overdue items first, and how many are overdue in all.</summary>
/// <param name="Items">Up to the limit asked for, most overdue first.</param>
/// <param name="Total">How many items are overdue.</param>
public sealed record ZabbixQueueDetails(IReadOnlyList<ZabbixQueuedItem> Items, long Total)
{
    /// <summary>Whether <paramref name="other"/> lists the same items in the same order, and the same total.</summary>
    /// <param name="other">The other queue.</param>
    /// <returns><see langword="true"/> when the totals and every item are equal; otherwise <see langword="false"/>.</returns>
    public bool Equals(ZabbixQueueDetails? other) => other is not null && Total == other.Total && ZabbixValueEquality.ListEqual(Items, other.Items);

    /// <inheritdoc/>
    public override int GetHashCode()
    {
        var hash = new HashCode();
        hash.Add(Total);
        ZabbixValueEquality.AddList(ref hash, Items);
        return hash.ToHashCode();
    }
}
