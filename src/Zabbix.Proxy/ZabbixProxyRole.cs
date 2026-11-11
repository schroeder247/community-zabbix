// This Source Code Form is subject to the terms of the Mozilla Public
// License, v. 2.0. Copyright (c) 2026 schroeder247. SPDX-License-Identifier: MPL-2.0

namespace Community.Zabbix;

/// <summary>
/// Zabbix's <c>ProxyMode</c>: which half of the pair (<see cref="ZabbixActiveProxyServiceCollectionExtensions.AddZabbixActiveProxy"/>,
/// <see cref="ZabbixPassiveProxyServiceCollectionExtensions.AddZabbixPassiveProxy"/>) a document row's <see cref="ZabbixProxyOptions.Mode"/>
/// selects — the document has one <c>Proxies</c> section for both because upstream's own configuration parameter is on the
/// row, not on which verb the deploying code happens to call. Named a role, not a mode, because <c>ZabbixProxyMode</c> is
/// the API model's own enum (<c>proxy.operating_mode</c>), and an application that references both packages would see two.
/// </summary>
public enum ZabbixProxyRole
{
    /// <summary>The proxy connects out and pulls its configuration (<c>ProxyMode=0</c>, the default): <see cref="ZabbixActiveProxyServiceCollectionExtensions.AddZabbixActiveProxy"/>.</summary>
    Active = 0,

    /// <summary>The server connects in (<c>ProxyMode=1</c>): <see cref="ZabbixPassiveProxyServiceCollectionExtensions.AddZabbixPassiveProxy"/>.</summary>
    Passive = 1,
}
