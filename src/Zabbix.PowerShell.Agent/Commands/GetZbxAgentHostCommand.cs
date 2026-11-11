// This Source Code Form is subject to the terms of the Mozilla Public
// License, v. 2.0. Copyright (c) 2026 schroeder247. SPDX-License-Identifier: MPL-2.0
using System.Management.Automation;

namespace Community.Zabbix.PowerShell.Commands;

/// <summary>
/// <para type="synopsis">Gets the agent host a running session provisioned, as the server sees it, with which of its
/// items a local handler still answers.</para>
/// </summary>
[Cmdlet(VerbsCommon.Get, "ZbxAgentHost")]
[OutputType(typeof(ZabbixAgentHostStatus))]
public sealed class GetZbxAgentHostCommand : ZabbixAgentHostCommand
{
    /// <inheritdoc/>
    protected override void ProcessRecord()
    {
        if (!TryGetSession(out ZabbixAgentSession session)) return;
        if (!TryGetRecord(session, out ZabbixAgentHostRecord? record)) return;

        var localKeys = new HashSet<string>(session.ProvisionedKeys, StringComparer.Ordinal);
        if (TryRun(cancellationToken => ZabbixAgentHostProvisioning.ReadStatusAsync(ZabbixClient, record.HostId, localKeys, cancellationToken), out ZabbixAgentHostStatus? status, record.HostName))
            WriteObject(status);
    }
}
