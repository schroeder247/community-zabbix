// This Source Code Form is subject to the terms of the Mozilla Public
// License, v. 2.0. Copyright (c) 2026 schroeder247. SPDX-License-Identifier: MPL-2.0
namespace Community.Zabbix.Proxy;

/// <summary>
/// Specifies whether a proxy holds more data behind a page it uploads: upstream's <c>more</c> tag
/// (<c>ZBX_PROXY_DATA_DONE</c> or <c>ZBX_PROXY_DATA_MORE</c>).
/// </summary>
public enum ZabbixProxyBacklog
{
    /// <summary>Nothing more waits behind the page: the tag is left out (<c>ZBX_PROXY_DATA_DONE</c>).</summary>
    None = 0,

    /// <summary>More waits behind the page, so the next one follows at once (<c>ZBX_PROXY_DATA_MORE</c>).</summary>
    More = 1,
}
