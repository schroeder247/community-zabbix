// This Source Code Form is subject to the terms of the Mozilla Public
// License, v. 2.0. Copyright (c) 2026 schroeder247. SPDX-License-Identifier: MPL-2.0
namespace Community.Zabbix;

/// <summary>What a load average is measured against.</summary>
public enum ZabbixCpuLoadScope
{
    /// <summary>The machine's whole load.</summary>
    All,

    /// <summary>The load divided by the processors online.</summary>
    PerProcessor,
}
