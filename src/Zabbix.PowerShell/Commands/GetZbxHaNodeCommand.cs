// This Source Code Form is subject to the terms of the Mozilla Public
// License, v. 2.0. Copyright (c) 2026 schroeder247. SPDX-License-Identifier: MPL-2.0
using System.Management.Automation;

namespace Community.Zabbix.PowerShell.Commands;

/// <summary>
/// <para type="synopsis">Gets the server's HA cluster nodes (<c>hanode.get</c>).</para>
/// <para type="description">
/// All nodes, the nodes with the given ids, or the nodes whose <c>-Name</c>, <c>-Address</c> and <c>-Status</c> match
/// exactly (no wildcards: <c>hanode.get</c> has no search).
/// </para>
/// </summary>
/// <remarks>
/// Hand-written: <c>ZabbixHaNodeGetParameters</c> has <c>filter</c> but no <c>search</c>, so it cannot implement
/// <see cref="IZabbixGetParameters"/>.
/// </remarks>
[Cmdlet(VerbsCommon.Get, "ZbxHaNode", DefaultParameterSetName = ZabbixParameterSets.All)]
[OutputType(typeof(ZabbixHaNode))]
public sealed class GetZbxHaNodeCommand : ZabbixServerCmdlet
{
    /// <summary>The node ids: CUIDs, 25-character strings, not numbers.</summary>
    [Parameter(Mandatory = true, Position = 0, ParameterSetName = ZabbixParameterSets.ById)]
    [ValidateNotNullOrEmpty]
    public string[]? Id { get; set; }

    /// <summary>The node's visible name; exact match.</summary>
    [Parameter(ParameterSetName = ZabbixParameterSets.All)]
    public string? Name { get; set; }

    /// <summary>The node's address; exact match.</summary>
    [Parameter(ParameterSetName = ZabbixParameterSets.All)]
    public string? Address { get; set; }

    /// <summary>The node's status; exact match.</summary>
    [Parameter(ParameterSetName = ZabbixParameterSets.All)]
    public ZabbixHaNodeStatus? Status { get; set; }

    /// <inheritdoc/>
    protected override void ProcessRecord()
    {
        Dictionary<string, ZabbixFieldValue>? filter = null;
        if (Name is not null || Address is not null || Status is not null)
        {
            filter = new Dictionary<string, ZabbixFieldValue>(StringComparer.Ordinal);
            if (Name is not null) filter["name"] = Name;
            if (Address is not null) filter["address"] = Address;
            if (Status is not null) filter["status"] = (int)Status.Value;
        }
        var parameters = new ZabbixHaNodeGetParameters
        {
            HaNodeIds = ParameterSetName == ZabbixParameterSets.ById ? [.. Id!] : null,
            Filter = new(filter),
        };
        WriteAll(cancellationToken => ZabbixClient.HaNodes.GetAsync(parameters, cancellationToken), parameters);
    }
}
