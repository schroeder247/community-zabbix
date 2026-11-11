// This Source Code Form is subject to the terms of the Mozilla Public
// License, v. 2.0. Copyright (c) 2026 schroeder247. SPDX-License-Identifier: MPL-2.0
using System.Management.Automation;

namespace Community.Zabbix.PowerShell.Commands;

/// <summary>
/// <para type="synopsis">Gets graph items (<c>graphitem.get</c>): the items plotted on a graph, and how.</para>
/// <para type="description">
/// By graph (<c>-Id</c>; piped objects with a <c>GraphId</c> bind to it) or by item (<c>-ItemId</c>). There is no
/// name, <c>-Filter</c> or search selection: <c>graphitem.get</c> takes only graph and item ids. <c>-SelectGraphs</c>
/// adds each graph item's graphs.
/// </para>
/// </summary>
/// <remarks>
/// Hand-written: <c>ZabbixGraphItemGetParameters</c> has neither <c>filter</c> nor <c>search</c> (only
/// <c>graphids</c>/<c>itemids</c>), so it cannot implement <see cref="IZabbixGetParameters"/> and
/// <see cref="ZabbixGetCmdlet{TEntity,TParameters}"/> does not fit it.
/// </remarks>
[Cmdlet(VerbsCommon.Get, "ZbxGraphItem", DefaultParameterSetName = ById)]
[OutputType(typeof(ZabbixGraphItem))]
public sealed class GetZbxGraphItemCommand : ZabbixServerCmdlet
{
    private const string ById = "ById";
    private const string ByItem = "ByItem";

    /// <summary>The graphs whose items to get.</summary>
    [Parameter(Mandatory = true, Position = 0, ParameterSetName = ById, ValueFromPipelineByPropertyName = true)]
    [Alias("GraphId")]
    [ZabbixId]
    public ulong[]? Id { get; set; }

    /// <summary>The items whose graph items to get, instead of a graph.</summary>
    [Parameter(Mandatory = true, ParameterSetName = ByItem)]
    public ulong[]? ItemId { get; set; }

    /// <summary>At most this many.</summary>
    [Parameter]
    [ValidateRange(1, int.MaxValue)]
    public int? Limit { get; set; }

    /// <summary>Returns every field of the related graphs, not just their ids.</summary>
    [Parameter]
    public SwitchParameter SelectGraphs { get; set; }

    /// <inheritdoc/>
    protected override void ProcessRecord()
    {
        var parameters = new ZabbixGraphItemGetParameters
        {
            GraphIds = ParameterSetName == ById ? [.. Id!] : null,
            ItemIds = ParameterSetName == ByItem ? [.. ItemId!] : null,
            Limit = Limit,
            SelectGraphs = SelectGraphs.IsPresent ? ZabbixOutput.Extend : null,
        };
        WriteAll(cancellationToken => ZabbixClient.GraphItems.GetAsync(parameters, cancellationToken), parameters);
    }
}
