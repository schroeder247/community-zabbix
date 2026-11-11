// This Source Code Form is subject to the terms of the Mozilla Public
// License, v. 2.0. Copyright (c) 2026 schroeder247. SPDX-License-Identifier: MPL-2.0
namespace Community.Zabbix.PowerShell.Commands;

/// <summary>
/// Records what a running agent session provisioned on the server: the host, its interface and its items. It is
/// attached to the <see cref="ZabbixAgentSession"/> that provisioned it (<see cref="ZabbixAgentSession.HostRecord"/>),
/// never kept in static state, so <c>Get-</c>/<c>Clear-ZbxAgentHost</c> know exactly what
/// <c>Initialize-</c>/<c>Publish-ZbxAgentHost</c> created and what they reused and must leave alone.
/// </summary>
internal sealed class ZabbixAgentHostRecord
{
    public ZabbixAgentHostRecord(ulong hostId, string hostName, ulong? interfaceId, bool hostCreated, bool interfaceCreated)
    {
        HostId = hostId;
        HostName = hostName;
        InterfaceId = interfaceId;
        HostCreated = hostCreated;
        InterfaceCreated = interfaceCreated;
    }

    /// <summary>The host's id.</summary>
    public ulong HostId { get; }

    /// <summary>The host's technical name.</summary>
    public string HostName { get; }

    /// <summary>The agent interface's id; <see langword="null"/> when the server reports no interface for the host.</summary>
    public ulong? InterfaceId { get; set; }

    /// <summary>Whether this bookkeeping created the host (as opposed to reusing one that already existed).</summary>
    public bool HostCreated { get; }

    /// <summary>Whether this bookkeeping created the interface (as opposed to reusing one the host already had).</summary>
    public bool InterfaceCreated { get; set; }

    /// <summary>Item key -&gt; item id, for every item this bookkeeping has created or is tracking.</summary>
    public Dictionary<string, ulong> ItemIds { get; } = new(StringComparer.Ordinal);
}
