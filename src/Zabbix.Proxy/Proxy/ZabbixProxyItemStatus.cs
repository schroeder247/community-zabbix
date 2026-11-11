// This Source Code Form is subject to the terms of the Mozilla Public
// License, v. 2.0. Copyright (c) 2026 schroeder247. SPDX-License-Identifier: MPL-2.0
namespace Community.Zabbix.Proxy;

/// <summary>An item's status in the server's configuration (upstream's <c>ITEM_STATUS_*</c>).</summary>
public enum ZabbixProxyItemStatus
{
    /// <summary><c>ITEM_STATUS_ACTIVE</c>: the item is collected.</summary>
    Active = 0,

    /// <summary><c>ITEM_STATUS_DISABLED</c>: a value for it is refused.</summary>
    Disabled = 1,
}
