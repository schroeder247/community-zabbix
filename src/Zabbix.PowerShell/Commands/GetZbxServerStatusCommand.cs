// This Source Code Form is subject to the terms of the Mozilla Public
// License, v. 2.0. Copyright (c) 2026 schroeder247. SPDX-License-Identifier: MPL-2.0
using System.Management.Automation;
using System.Net;

namespace Community.Zabbix.PowerShell.Commands;

/// <summary>
/// <para type="synopsis">Gets the server's "System information" statistics (<c>status.get type=full</c>, super admin only).</para>
/// </summary>
[Cmdlet(VerbsCommon.Get, "ZbxServerStatus")]
[OutputType(typeof(ZabbixServerStatus))]
public sealed class GetZbxServerStatusCommand : ZabbixServerWireCmdlet
{
    /// <summary>
    /// The server's trapper: <c>host</c> or <c>host:port</c> (port 10051 when omitted; an IPv6 address with a port is
    /// bracketed, <c>[::1]:10051</c>). Several are the nodes of one HA cluster, tried in order. The connection's
    /// (<c>Connect-ZbxServer -Trapper</c>), else the active HA node's, when omitted.
    /// </summary>
    [Parameter]
    [ValidateNotNullOrEmpty]
    [ZabbixEndPoint]
    public DnsEndPoint[]? Server { get; set; }

    /// <inheritdoc/>
    protected override void ProcessRecord()
    {
        if (TryRun(async cancellationToken =>
            {
                return await CallWireAsync(Server, (wire, token) => wire.GetStatusAsync(token), cancellationToken).ConfigureAwait(false);
            }, out ZabbixServerStatus? status, Connected.Name))
        {
            WriteObject(status);
        }
    }
}
