// This Source Code Form is subject to the terms of the Mozilla Public
// License, v. 2.0. Copyright (c) 2026 schroeder247. SPDX-License-Identifier: MPL-2.0
using System.Management.Automation;
using System.Net;

namespace Community.Zabbix.PowerShell.Commands;

/// <summary>
/// <para type="synopsis">Gets the item queue: how many items are overdue, and by how much (<c>queue.get</c>, super admin only).</para>
/// <para type="description">An overview grouped by item type (default) or by proxy (<c>-ByProxy</c>); the overdue items themselves with <c>-Details</c>.</para>
/// </summary>
[Cmdlet(VerbsCommon.Get, "ZbxQueue", DefaultParameterSetName = OverviewSet)]
[OutputType(typeof(ZabbixQueueDelays), ParameterSetName = new[] { OverviewSet })]
[OutputType(typeof(ZabbixQueueDetails), ParameterSetName = new[] { DetailsSet })]
public sealed class GetZbxQueueCommand : ZabbixServerWireCmdlet
{
    private const string OverviewSet = "Overview";
    private const string DetailsSet = "Details";

    /// <summary>
    /// The server's trapper: <c>host</c> or <c>host:port</c> (port 10051 when omitted; an IPv6 address with a port is
    /// bracketed, <c>[::1]:10051</c>). Several are the nodes of one HA cluster, tried in order. The connection's
    /// (<c>Connect-ZbxServer -Trapper</c>), else the active HA node's, when omitted.
    /// </summary>
    [Parameter]
    [ValidateNotNullOrEmpty]
    [ZabbixEndPoint]
    public DnsEndPoint[]? Server { get; set; }

    /// <summary>Groups the overview by proxy rather than by item type.</summary>
    [Parameter(ParameterSetName = OverviewSet)]
    public SwitchParameter ByProxy { get; set; }

    /// <summary>Lists the overdue items themselves, most overdue first, instead of the overview.</summary>
    [Parameter(Mandatory = true, ParameterSetName = DetailsSet)]
    public SwitchParameter Details { get; set; }

    /// <summary>How many overdue items to list; 100 by default.</summary>
    [Parameter(ParameterSetName = DetailsSet)]
    [ValidateRange(1, 10000)]
    public int? Limit { get; set; }

    /// <inheritdoc/>
    protected override void ProcessRecord()
    {
        if (ParameterSetName == DetailsSet)
        {
            if (TryRun(async cancellationToken =>
                {
                    return await CallWireAsync(Server, (wire, token) => wire.GetQueueDetailsAsync(Limit ?? 100, token), cancellationToken).ConfigureAwait(false);
                }, out ZabbixQueueDetails? details, Server is null ? Connected.Name : string.Join(", ", Server.Select(ZabbixEndPoint.Format))))
            {
                WriteObject(details);
            }
        }
        else if (TryRun(async cancellationToken =>
            {
                ZabbixQueueOverview overview = ByProxy ? ZabbixQueueOverview.ByProxy : ZabbixQueueOverview.ByItemType;
                return await CallWireAsync(Server, (wire, token) => wire.GetQueueAsync(overview, token), cancellationToken).ConfigureAwait(false);
            }, out IReadOnlyList<ZabbixQueueDelays>? rows, Server is null ? Connected.Name : string.Join(", ", Server.Select(ZabbixEndPoint.Format))))
        {
            foreach (ZabbixQueueDelays row in rows) WriteObject(row);
        }
    }
}
