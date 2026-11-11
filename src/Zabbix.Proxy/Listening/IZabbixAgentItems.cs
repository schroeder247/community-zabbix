// This Source Code Form is subject to the terms of the Mozilla Public
// License, v. 2.0. Copyright (c) 2026 schroeder247. SPDX-License-Identifier: MPL-2.0
using System.Diagnostics.CodeAnalysis;

namespace Community.Zabbix.Listening;

/// <summary>
/// What a trapper needs to take <c>agent data</c> from a 4.4 or later agent, whose rows name items by id: the receiver's
/// hosts and items (a proxy's configuration) and its agents' sessions. A trapper without it reads an agent's rows by host
/// and key only.
/// </summary>
internal interface IZabbixAgentItems
{
    /// <summary>The frame's host, by its technical name (<c>zbx_dc_config_get_hostid_by_name</c>).</summary>
    bool TryGetHost(string host, out ulong hostId);

    /// <summary>An item by id: the host and key it is written under (<c>zbx_dc_config_history_recv_get_items_by_itemids</c>).</summary>
    bool TryGetItem(ulong itemId, [NotNullWhen(true)] out string? host, out ZabbixItemKey key);

    /// <summary>The data session of an agent on a host (<c>zbx_dc_get_or_create_session</c>); <see langword="null"/> when the session table is full, and the frame's values are then taken without de-duplication.</summary>
    ZabbixAgentSession? Session(ulong hostId, string session);
}
