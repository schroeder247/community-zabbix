// This Source Code Form is subject to the terms of the Mozilla Public
// License, v. 2.0. Copyright (c) 2026 schroeder247. SPDX-License-Identifier: MPL-2.0
namespace Community.Zabbix;

/// <summary>Which processor time of a process to read.</summary>
public enum ZabbixProcessCpuTime
{
    /// <summary>All its time.</summary>
    Total,

    /// <summary>Its time running its own code.</summary>
    User,

    /// <summary>Its time running kernel code on its behalf.</summary>
    Privileged,
}
