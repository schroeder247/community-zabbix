// This Source Code Form is subject to the terms of the Mozilla Public
// License, v. 2.0. Copyright (c) 2026 schroeder247. SPDX-License-Identifier: MPL-2.0
namespace Community.Zabbix;

/// <summary>How the matching processes' whole-number figures combine into one.</summary>
public enum ZabbixProcessMemoryStatistic
{
    /// <summary>Their sum.</summary>
    Sum,

    /// <summary>The largest of them.</summary>
    Maximum,

    /// <summary>The smallest of them.</summary>
    Minimum,
}
