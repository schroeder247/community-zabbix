// This Source Code Form is subject to the terms of the Mozilla Public
// License, v. 2.0. Copyright (c) 2026 schroeder247. SPDX-License-Identifier: MPL-2.0
namespace Community.Zabbix;

/// <summary>
/// How far back an average reaches: one of the system's load averages, <see cref="ZabbixItemKeys.Cpu.Load"/>, or an agent's
/// average over its own samples, such as <see cref="ZabbixItemKeys.Cpu.UserPercent"/>.
/// </summary>
public enum ZabbixAverageWindow
{
    /// <summary>The last minute.</summary>
    OneMinute,

    /// <summary>The last five minutes.</summary>
    FiveMinutes,

    /// <summary>The last fifteen minutes.</summary>
    FifteenMinutes,
}
