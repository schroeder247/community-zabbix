// This Source Code Form is subject to the terms of the Mozilla Public
// License, v. 2.0. Copyright (c) 2026 schroeder247. SPDX-License-Identifier: MPL-2.0
namespace Community.Zabbix;

/// <summary>
/// Represents what one proxy monitors, as <c>status.get</c> counts it: its hosts and items, and the new values per second
/// they need.
/// </summary>
public sealed record ZabbixProxyLoad
{
    /// <summary>The proxy's id.</summary>
    public ulong ProxyId { get; init; }

    /// <summary>How many of the proxy's hosts are enabled.</summary>
    public long? EnabledHostCount { get; init; }

    /// <summary>How many of the proxy's hosts are disabled.</summary>
    public long? DisabledHostCount { get; init; }

    /// <summary>How many of the proxy's items are enabled and supported.</summary>
    public long? EnabledItemCount { get; init; }

    /// <summary>How many of the proxy's items are disabled.</summary>
    public long? DisabledItemCount { get; init; }

    /// <summary>How many of the proxy's items are enabled but not supported.</summary>
    public long? UnsupportedItemCount { get; init; }

    /// <summary>How many new values per second the proxy's items need.</summary>
    public double? RequiredPerformance { get; init; }
}
