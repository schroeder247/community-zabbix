// This Source Code Form is subject to the terms of the Mozilla Public
// License, v. 2.0. Copyright (c) 2026 schroeder247. SPDX-License-Identifier: MPL-2.0
namespace Community.Zabbix;

/// <summary>
/// Represents one host group, by its name: <c>zabbix.HostGroups["Linux servers"]</c>. Read it, delete it, reach its hosts by
/// name (<c>Hosts["web-01"]</c>), or ask it and every item of its hosts answers. No call is made until it's asked.
/// </summary>
public class ZabbixHostGroupNode : ZabbixNode
{
    // Null only in a node built for mocking, whose members are overridden.
    private readonly ZabbixHostScope? _hosts;

    /// <summary>Initializes a group node for mocking; every member of a node built this way is to be overridden.</summary>
    protected ZabbixHostGroupNode()
    {
        Name = string.Empty;
    }

    internal ZabbixHostGroupNode(ZabbixClient client, ZabbixSelection selection) : base(client, selection)
    {
        Name = selection.Group ?? throw new ArgumentException("A host group node is selected by its group.", nameof(selection));
        _hosts = new ZabbixHostScope(client, selection);
    }

    /// <summary>The group's name.</summary>
    public virtual string Name { get; }

    /// <summary>
    /// The group's hosts: enumerate them, or reach one by its technical name, <c>Hosts["web-01"]</c>, which reads that host only
    /// while it's in the group. No call is made until it's asked.
    /// </summary>
    public virtual ZabbixHostScope Hosts => _hosts ?? throw new InvalidOperationException("The node was built for mocking, and this member is not overridden.");

    /// <summary>Reads the group.</summary>
    /// <param name="cancellationToken">A cancellation token.</param>
    /// <returns>The group, as <c>hostgroup.get</c> returns it.</returns>
    /// <exception cref="ZabbixNotFoundException">The group does not exist.</exception>
    public virtual async Task<ZabbixHostGroup> GetAsync(CancellationToken cancellationToken = default)
        => await FindAsync(cancellationToken).ConfigureAwait(false) ?? throw new ZabbixNotFoundException("hostgroup", Name);

    /// <summary>Reads the group, if there is one.</summary>
    /// <param name="cancellationToken">A cancellation token.</param>
    /// <returns>The group, or <see langword="null"/> when the server has none of this name that the caller may read.</returns>
    public virtual async Task<ZabbixHostGroup?> FindAsync(CancellationToken cancellationToken = default)
    {
        foreach (ZabbixHostGroup group in await Client.HostGroups.GetAsync(new ZabbixHostGroupGetParameters
        {
            Filter = new() { ["name"] = Name },
            Limit = 1,
        }, cancellationToken).ConfigureAwait(false))
        {
            return group;
        }
        return null;
    }

    /// <summary>Deletes the group.</summary>
    /// <param name="cancellationToken">A cancellation token.</param>
    /// <returns>A task that completes when the server has deleted it.</returns>
    /// <exception cref="ZabbixNotFoundException">The group does not exist.</exception>
    public virtual async Task DeleteAsync(CancellationToken cancellationToken = default)
    {
        ZabbixHostGroup group = await GetAsync(cancellationToken).ConfigureAwait(false);
        await Client.HostGroups.DeleteAsync(group.GroupId ?? throw new ZabbixNotFoundException("hostgroup", Name), cancellationToken).ConfigureAwait(false);
    }

    /// <summary>
    /// Changes the group: reads its id, names it in a <c>hostgroup.update</c> request, and lets <paramref name="configure"/>
    /// set the fields to change, <c>g =&gt; g.Name = "Linux servers"</c> to rename it.
    /// </summary>
    /// <param name="configure">Sets the fields to change.</param>
    /// <param name="cancellationToken">A cancellation token.</param>
    /// <returns>A task that completes when the server has changed the group.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="configure"/> is <see langword="null"/>.</exception>
    /// <exception cref="ZabbixNotFoundException">The group does not exist.</exception>
    public virtual Task UpdateAsync(Action<ZabbixHostGroupUpdateRequest> configure, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(configure);
        return UpdateCoreAsync(configure, cancellationToken);
    }

    private async Task UpdateCoreAsync(Action<ZabbixHostGroupUpdateRequest> configure, CancellationToken cancellationToken)
    {
        ZabbixHostGroup group = await GetAsync(cancellationToken).ConfigureAwait(false);
        var request = new ZabbixHostGroupUpdateRequest(group.GroupId ?? throw new ZabbixNotFoundException("hostgroup", Name));
        configure(request);
        await Client.HostGroups.UpdateAsync(request, cancellationToken).ConfigureAwait(false);
    }
}
