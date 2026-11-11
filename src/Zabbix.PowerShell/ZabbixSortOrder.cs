// This Source Code Form is subject to the terms of the Mozilla Public
// License, v. 2.0. Copyright (c) 2026 schroeder247. SPDX-License-Identifier: MPL-2.0
namespace Community.Zabbix.PowerShell;

/// <summary>
/// <c>sortorder</c>, spelled out: <c>-SortOrder</c> takes readable words, not the wire's bare <c>ASC</c>/<c>DESC</c>
/// strings. One per <c>-SortField</c> (shorter than <c>-SortField</c> defaults to <see cref="Ascending"/> for the rest,
/// the API's own default).
/// </summary>
public enum ZabbixSortOrder
{
    /// <summary>Wire <c>ASC</c>.</summary>
    Ascending,

    /// <summary>Wire <c>DESC</c>.</summary>
    Descending,
}

/// <summary>Converts <see cref="ZabbixSortOrder"/> to the SDK's <see cref="System.ComponentModel.ListSortDirection"/>.</summary>
internal static class ZabbixSortOrderExtensions
{
    /// <summary>The SDK's direction, which the wire writes <c>ASC</c>/<c>DESC</c>.</summary>
    /// <param name="order">The order.</param>
    public static System.ComponentModel.ListSortDirection ToDirection(this ZabbixSortOrder order)
        => order == ZabbixSortOrder.Descending ? System.ComponentModel.ListSortDirection.Descending : System.ComponentModel.ListSortDirection.Ascending;
}
