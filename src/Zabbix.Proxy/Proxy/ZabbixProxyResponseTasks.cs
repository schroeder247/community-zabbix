// This Source Code Form is subject to the terms of the Mozilla Public
// License, v. 2.0. Copyright (c) 2026 schroeder247. SPDX-License-Identifier: MPL-2.0
using System.Text.Json;

namespace Community.Zabbix.Proxy;

/// <summary>
/// Decodes a <see cref="ZabbixProxyResponse"/>'s <c>tasks</c> array into <see cref="ZabbixProxyTask"/>. A public
/// extension (not a member of the generated record) so a caller composing its own upload loop over
/// <see cref="ZabbixProxyUplink"/> directly — as the PowerShell module's active-proxy session does — reads the same
/// shape the proxy hosts do, with nothing re-parsed.
/// </summary>
public static class ZabbixProxyResponseTasks
{
    /// <summary>The tasks the server handed back; empty when there were none.</summary>
    public static IReadOnlyList<ZabbixProxyTask> DeserializeTasks(this ZabbixProxyResponse response)
    {
        ArgumentNullException.ThrowIfNull(response);
        return response.Tasks is { ValueKind: JsonValueKind.Array } tasks
            ? tasks.Deserialize(ZabbixProxyJson.Default.IReadOnlyListZabbixProxyTask) ?? []
            : [];
    }
}
