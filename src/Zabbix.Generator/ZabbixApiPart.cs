// This Source Code Form is subject to the terms of the Mozilla Public
// License, v. 2.0. Copyright (c) 2026 schroeder247. SPDX-License-Identifier: MPL-2.0
namespace Community.Zabbix.Generator;

/// <summary>Which assembly's share of the API is being emitted.</summary>
internal enum ZabbixApiPart
{
    /// <summary>The records, enums, requests and the capability table (Zabbix.Api).</summary>
    Models,

    /// <summary>The clients and the aggregate (Zabbix).</summary>
    Clients,
}
