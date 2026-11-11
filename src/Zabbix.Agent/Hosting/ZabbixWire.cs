// This Source Code Form is subject to the terms of the Mozilla Public
// License, v. 2.0. Copyright (c) 2026 schroeder247. SPDX-License-Identifier: MPL-2.0
namespace Community.Zabbix.Hosting;

/// <summary>Constants of the agent protocol.</summary>
internal static class ZabbixWire
{
    /// <summary>
    /// The version this agent reports. Unlike a proxy's, an agent's version gates nothing on 7.0–8.0: the server reads it
    /// only to shape its answer for agents older than 4.4 (<c>src/libs/zbxtrapper/active.c</c>,
    /// <c>ZBX_COMPONENT_VERSION(4, 4, 0) &gt; version</c>), which is why one constant serves every server version
    /// (verified live against 7.0.31 and 8.0.0rc1).
    /// </summary>
    public const string AgentVersion = "7.4.0";

    /// <summary>The variant: 2 is agent 2.</summary>
    public const int AgentVariant = 2;
}
