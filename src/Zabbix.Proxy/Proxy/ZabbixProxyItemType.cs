// This Source Code Form is subject to the terms of the Mozilla Public
// License, v. 2.0. Copyright (c) 2026 schroeder247. SPDX-License-Identifier: MPL-2.0
namespace Community.Zabbix.Proxy;

/// <summary>
/// An item's type as the server's configuration carries it (upstream's <c>ITEM_TYPE_*</c>, <c>zbxcommon.h</c>). A proxy
/// admits pushed values for <see cref="Trapper"/>, <see cref="HttpAgent"/> (with traps allowed) and
/// <see cref="ZabbixAgentActive"/>; a type a later server adds reads as its number.
/// </summary>
public enum ZabbixProxyItemType
{
    /// <summary><c>ITEM_TYPE_ZABBIX</c>: a passive agent check.</summary>
    ZabbixAgent = 0,

    /// <summary><c>ITEM_TYPE_TRAPPER</c>: values a sender pushes.</summary>
    Trapper = 2,

    /// <summary><c>ITEM_TYPE_SIMPLE</c>.</summary>
    SimpleCheck = 3,

    /// <summary><c>ITEM_TYPE_INTERNAL</c>.</summary>
    Internal = 5,

    /// <summary><c>ITEM_TYPE_ZABBIX_ACTIVE</c>: an active agent check, whose values arrive as <c>agent data</c>.</summary>
    ZabbixAgentActive = 7,

    /// <summary><c>ITEM_TYPE_HTTPTEST</c>: a web scenario's item.</summary>
    WebItem = 9,

    /// <summary><c>ITEM_TYPE_EXTERNAL</c>.</summary>
    External = 10,

    /// <summary><c>ITEM_TYPE_DB_MONITOR</c>.</summary>
    DatabaseMonitor = 11,

    /// <summary><c>ITEM_TYPE_IPMI</c>.</summary>
    Ipmi = 12,

    /// <summary><c>ITEM_TYPE_SSH</c>.</summary>
    Ssh = 13,

    /// <summary><c>ITEM_TYPE_TELNET</c>.</summary>
    Telnet = 14,

    /// <summary><c>ITEM_TYPE_CALCULATED</c>.</summary>
    Calculated = 15,

    /// <summary><c>ITEM_TYPE_JMX</c>.</summary>
    Jmx = 16,

    /// <summary><c>ITEM_TYPE_SNMPTRAP</c>.</summary>
    SnmpTrap = 17,

    /// <summary><c>ITEM_TYPE_DEPENDENT</c>.</summary>
    Dependent = 18,

    /// <summary><c>ITEM_TYPE_HTTPAGENT</c>: polls, and also takes pushed values when its <c>allow_traps</c> is set.</summary>
    HttpAgent = 19,

    /// <summary><c>ITEM_TYPE_SNMP</c>.</summary>
    Snmp = 20,

    /// <summary><c>ITEM_TYPE_SCRIPT</c>.</summary>
    Script = 21,

    /// <summary><c>ITEM_TYPE_BROWSER</c>.</summary>
    Browser = 22,

    /// <summary><c>ITEM_TYPE_NESTED_LLD</c> (8.0).</summary>
    NestedDiscovery = 23,
}
