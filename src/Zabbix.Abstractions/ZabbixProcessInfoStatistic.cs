// This Source Code Form is subject to the terms of the Mozilla Public
// License, v. 2.0. Copyright (c) 2026 schroeder247. SPDX-License-Identifier: MPL-2.0
namespace Community.Zabbix;

/// <summary>How the counters of the Windows processes of one image combine into one figure.</summary>
public enum ZabbixProcessInfoStatistic
{
    /// <summary>Their average.</summary>
    Average,

    /// <summary>The smallest of them.</summary>
    Minimum,

    /// <summary>The largest of them.</summary>
    Maximum,

    /// <summary>Their sum.</summary>
    Sum,
}
