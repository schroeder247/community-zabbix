// This Source Code Form is subject to the terms of the Mozilla Public
// License, v. 2.0. Copyright (c) 2026 schroeder247. SPDX-License-Identifier: MPL-2.0
namespace Community.Zabbix;

/// <summary>Specifies how a server's value cache works (<c>ZBX_VC_MODE_*</c> in <c>zbxcachevalue.h</c>).</summary>
public enum ZabbixValueCacheMode
{
    /// <summary>The cache keeps the values requests ask for.</summary>
    Normal = 0,

    /// <summary>The cache has run low on memory, and stops caching values until it has room again.</summary>
    LowMemory = 1,
}
