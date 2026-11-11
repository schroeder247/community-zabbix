// This Source Code Form is subject to the terms of the Mozilla Public
// License, v. 2.0. Copyright (c) 2026 schroeder247. SPDX-License-Identifier: MPL-2.0
using System.Management.Automation;

namespace Community.Zabbix.PowerShell.Commands;

/// <summary>
/// <para type="synopsis">Gets hosts.</para>
/// <para type="description">
/// By id, by technical name (case-insensitive; <c>*</c> and <c>?</c> are wildcards), by a full <c>host.get</c> selection
/// (<c>-Filter</c>, a <c>ZabbixHostGetParameters</c>), or all; <c>-Group</c> narrows any of these to hosts in the given
/// host groups.
/// </para>
/// </summary>
[Cmdlet(VerbsCommon.Get, "ZbxHost", DefaultParameterSetName = ZabbixParameterSets.All, SupportsPaging = true)]
[OutputType(typeof(ZabbixHost))]
public sealed class GetZbxHostCommand : ZabbixGetCmdlet<ZabbixHost, ZabbixHostGetParameters>
{
    /// <summary>
    /// The hosts' technical names, case-insensitive; <c>*</c> and <c>?</c> are wildcards, brackets are literal. A piped
    /// name selects the same way.
    /// </summary>
    [Parameter(Mandatory = true, Position = 0, ParameterSetName = ZabbixParameterSets.ByName)]
    [SupportsWildcards]
    [ZabbixCompleter<ZabbixHost>]
    public override string[]? Name { get; set; }

    /// <summary>The hosts' ids; piped objects carrying a <c>HostId</c> bind here, and a piped id selects the same way.</summary>
    [Parameter(Mandatory = true, ParameterSetName = ZabbixParameterSets.ById, ValueFromPipelineByPropertyName = true)]
    [Parameter(ParameterSetName = ZabbixParameterSets.All, ValueFromPipelineByPropertyName = true)]
    [Alias("HostId")]
    [ZabbixId]
    public override ulong[]? Id { get; set; }

    /// <summary>The full selection, as <c>host.get</c>'s own parameters.</summary>
    [Parameter(Mandatory = true, ParameterSetName = ZabbixParameterSets.ByFilter)]
    public override ZabbixHostGetParameters? Filter { get; set; }

    /// <summary>At most this many hosts.</summary>
    [Parameter(ParameterSetName = ZabbixParameterSets.All)]
    [Parameter(ParameterSetName = ZabbixParameterSets.ByName)]
    [ValidateRange(1, int.MaxValue)]
    public override int? Limit { get; set; }

    /// <summary>Sort by these fields, in order (<c>sortfield</c>); Tab completes the fields host.get sorts by.</summary>
    /// <remarks>The completions are <see cref="ZabbixHostGetParameters.SortableFields"/>, as a generated <c>Get-</c> writes them; a test holds the two equal.</remarks>
    [Parameter]
    [ArgumentCompletions("host", "hostid", "name", "status")]
    public override string[]? SortField { get; set; }

    /// <summary>Only hosts in these host groups: ids, names, or objects (e.g. from <c>Get-ZbxHostGroup</c>).</summary>
    /// <remarks>
    /// Filters by hostgroup: ids, names, or objects (e.g. from Get-ZbxHostGroup). Wire field groupids.
    /// Get-ZbxHost is hand-written (ZabbixCmdletEmitter.EmitGet skips "host"), so it does not get the generated
    /// reference-filter parameters; the same ZabbixGetCmdlet&lt;TEntity, TParameters&gt; hook
    /// (HasReferenceFilters/ApplyReferenceFiltersAsync) applies here by hand, the shape a generated Get-ZbxItem's
    /// -Group uses.
    /// </remarks>
    [Parameter(ValueFromPipelineByPropertyName = true)]
    [Alias("GroupId")]
    [ZabbixReference<ZabbixHostGroup>]
    [ZabbixCompleter<ZabbixHostGroup>]
    public ZabbixReference<ZabbixHostGroup>[]? Group { get; set; }

    /// <inheritdoc/>
    protected override bool HasReferenceFilters => Group is not null;

    /// <inheritdoc/>
    protected override async Task<ZabbixHostGetParameters> ApplyReferenceFiltersAsync(ZabbixHostGetParameters parameters, CancellationToken cancellationToken)
    {
        if (Group is not null)
            parameters = parameters with { GroupIds = [.. (await ResolveAsync<ZabbixHostGroup>(Group, cancellationToken).ConfigureAwait(false))] };
        return parameters;
    }

    // The same -Select*/-Editable set ZabbixCmdletEmitter.EmitGet gives every generated Get-Zbx<Entity>, added here by
    // hand because Get-ZbxHost is hand-written (EmitGet skips "host"). One SwitchParameter per ZabbixHostGetParameters
    // "select..." option, bare rather than a field list (see ZabbixCmdletEmitter's note on -SelectX), folded the same
    // way ZabbixGetCmdlet<TEntity,TParameters>.ApplyEntityFilters composes for a generated cmdlet.

    /// <summary>Select host groups. Wire field selectHostGroups.</summary>
    [Parameter]
    public SwitchParameter SelectHostGroups { get; set; }

    /// <summary>Select the discovery data of a discovered host (7.4+). Wire field selectDiscoveryData.</summary>
    [Parameter]
    public SwitchParameter SelectDiscoveryData { get; set; }

    /// <summary>Select the discovery rules that discovered the host (7.4+). Wire field selectDiscoveryRules.</summary>
    [Parameter]
    public SwitchParameter SelectDiscoveryRules { get; set; }

    /// <summary>Select parent templates. Wire field selectParentTemplates.</summary>
    [Parameter]
    public SwitchParameter SelectParentTemplates { get; set; }

    /// <summary>Select items. Wire field selectItems.</summary>
    [Parameter]
    public SwitchParameter SelectItems { get; set; }

    /// <summary>Select triggers. Wire field selectTriggers.</summary>
    [Parameter]
    public SwitchParameter SelectTriggers { get; set; }

    /// <summary>Select graphs. Wire field selectGraphs.</summary>
    [Parameter]
    public SwitchParameter SelectGraphs { get; set; }

    /// <summary>Select macros. Wire field selectMacros.</summary>
    [Parameter]
    public SwitchParameter SelectMacros { get; set; }

    /// <summary>Select dashboards. Wire field selectDashboards.</summary>
    [Parameter]
    public SwitchParameter SelectDashboards { get; set; }

    /// <summary>Select interfaces. Wire field selectInterfaces.</summary>
    [Parameter]
    public SwitchParameter SelectInterfaces { get; set; }

    /// <summary>Select inventory. Wire field selectInventory.</summary>
    [Parameter]
    public SwitchParameter SelectInventory { get; set; }

    /// <summary>Select http tests. Wire field selectHttpTests.</summary>
    [Parameter]
    public SwitchParameter SelectHttpTests { get; set; }

    /// <summary>Select discovery rule. Wire field selectDiscoveryRule.</summary>
    [Parameter]
    public SwitchParameter SelectDiscoveryRule { get; set; }

    /// <summary>Select tags. Wire field selectTags.</summary>
    [Parameter]
    public SwitchParameter SelectTags { get; set; }

    /// <summary>Select inherited tags. Wire field selectInheritedTags.</summary>
    [Parameter]
    public SwitchParameter SelectInheritedTags { get; set; }

    /// <summary>Select value maps. Wire field selectValueMaps.</summary>
    [Parameter]
    public SwitchParameter SelectValueMaps { get; set; }

    /// <summary>Only hosts the current user can edit. Wire field editable.</summary>
    [Parameter]
    public SwitchParameter Editable { get; set; }

    /// <inheritdoc/>
    protected override ZabbixHostGetParameters ApplyEntityFilters(ZabbixHostGetParameters parameters)
    {
        if (SortWire(parameters.Sort) is { } sort) parameters = parameters with { Sort = sort };
        if (SearchDictionary() is { } search) parameters = parameters with { Search = new(search) };
        if (SearchByAny.IsPresent) parameters = parameters with { SearchByAny = true };
        if (StartSearch.IsPresent) parameters = parameters with { StartSearch = true };
        if (ExcludeSearch.IsPresent) parameters = parameters with { ExcludeSearch = true };
        if (SearchWildcardsEnabled.IsPresent) parameters = parameters with { SearchWildcardsEnabled = true };
        if (SelectHostGroups.IsPresent) parameters = parameters with { SelectHostGroups = ZabbixOutput.Extend };
        if (SelectParentTemplates.IsPresent) parameters = parameters with { SelectParentTemplates = ZabbixOutput.Extend };
        if (SelectItems.IsPresent) parameters = parameters with { SelectItems = ZabbixOutput.Extend };
        if (SelectTriggers.IsPresent) parameters = parameters with { SelectTriggers = ZabbixOutput.Extend };
        if (SelectGraphs.IsPresent) parameters = parameters with { SelectGraphs = ZabbixOutput.Extend };
        if (SelectMacros.IsPresent) parameters = parameters with { SelectMacros = ZabbixOutput.Extend };
        if (SelectDashboards.IsPresent) parameters = parameters with { SelectDashboards = ZabbixOutput.Extend };
        if (SelectInterfaces.IsPresent) parameters = parameters with { SelectInterfaces = ZabbixOutput.Extend };
        if (SelectInventory.IsPresent) parameters = parameters with { SelectInventory = ZabbixOutput.Extend };
        if (SelectHttpTests.IsPresent) parameters = parameters with { SelectHttpTests = ZabbixOutput.Extend };
        if (SelectDiscoveryRule.IsPresent) parameters = parameters with { SelectDiscoveryRule = ZabbixOutput.Extend };
        if (SelectTags.IsPresent) parameters = parameters with { SelectTags = ZabbixOutput.Extend };
        if (SelectInheritedTags.IsPresent) parameters = parameters with { SelectInheritedTags = ZabbixOutput.Extend };
        if (SelectValueMaps.IsPresent) parameters = parameters with { SelectValueMaps = ZabbixOutput.Extend };
        if (SelectDiscoveryData.IsPresent) parameters = parameters with { SelectDiscoveryData = ZabbixOutput.Extend };
        if (SelectDiscoveryRules.IsPresent) parameters = parameters with { SelectDiscoveryRules = ZabbixOutput.Extend };
        if (Editable.IsPresent) parameters = parameters with { Editable = true };
        return parameters;
    }

    /// <inheritdoc/>
    protected override void Query(ZabbixClient client, ZabbixHostGetParameters parameters)
    {
        if (CountOutput.IsPresent) { if (TryRun(cancellationToken => client.Hosts.CountAsync(parameters, cancellationToken), out long count, parameters)) WriteObject(count); return; }
        WriteAll(cancellationToken => client.Hosts.GetAsync(parameters, cancellationToken), parameters);
    }

    /// <inheritdoc/>
    protected override Task<long> CountAsync(ZabbixClient client, ZabbixHostGetParameters parameters, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(client);
        return client.Hosts.CountAsync(parameters, cancellationToken);
    }
}
