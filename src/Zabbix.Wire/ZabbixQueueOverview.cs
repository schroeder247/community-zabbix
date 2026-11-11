// This Source Code Form is subject to the terms of the Mozilla Public
// License, v. 2.0. Copyright (c) 2026 schroeder247. SPDX-License-Identifier: MPL-2.0

namespace Community.Zabbix;

/// <summary>How <c>queue.get</c> groups the overdue items (<c>ZBX_PROTO_VALUE_GET_QUEUE_*</c>).</summary>
public enum ZabbixQueueOverview
{
    /// <summary>By item type (<c>overview</c>): each row's <see cref="ZabbixQueueDelays.ItemType"/>.</summary>
    ByItemType,

    /// <summary>By proxy (<c>overview by proxy</c>): each row's <see cref="ZabbixQueueDelays.ProxyId"/>, 0 for the server.</summary>
    ByProxy,
}
