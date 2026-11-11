// This Source Code Form is subject to the terms of the Mozilla Public
// License, v. 2.0. Copyright (c) 2026 schroeder247. SPDX-License-Identifier: MPL-2.0
namespace Community.Zabbix;

/// <summary>
/// Represents one host group, by its name, <c>zabbix.HostGroups["Linux servers"]</c>, or by its id,
/// <c>zabbix.HostGroups[5]</c>: the same node either way. Read it, change it, delete it, reach its hosts by name
/// (<c>Hosts["web-01"]</c>), or ask it and every item of its hosts answers. No call is made until it's asked.
/// </summary>
public class ZabbixHostGroupNode : ZabbixNode
{
    // Null only in a node built for mocking, whose members are overridden.
    private readonly ZabbixHostScope? _hosts;

    /// <summary>Initializes a group node for mocking; every member of a node built this way is to be overridden.</summary>
    protected ZabbixHostGroupNode()
    {
    }

    internal ZabbixHostGroupNode(ZabbixClient client, ZabbixSelection selection) : base(client, selection)
    {
        if (!selection.HasGroup) throw new ArgumentException("A host group node is selected by its group.", nameof(selection));
        _hosts = new ZabbixHostScope(client, selection);
    }

    /// <summary>The group's id, when the node was reached by it; <see langword="null"/> when it was reached by name.</summary>
    public virtual ulong? Id => Selection.GroupId;

    /// <summary>The group's name, when the node was reached by it; <see langword="null"/> when it was reached by id.</summary>
    public virtual string? Name => Selection.Group;

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
        => await FindAsync(cancellationToken).ConfigureAwait(false) ?? throw Selection.AbsentGroup();

    /// <summary>Reads the group, if there is one.</summary>
    /// <param name="cancellationToken">A cancellation token.</param>
    /// <returns>The group, or <see langword="null"/> when the server has none of this name or id that the caller may read.</returns>
    public virtual async Task<ZabbixHostGroup?> FindAsync(CancellationToken cancellationToken = default)
    {
        foreach (ZabbixHostGroup group in await Client.HostGroups.GetAsync(Selection.ToGroupGet() with { Limit = 1 }, cancellationToken).ConfigureAwait(false))
            return group;
        return null;
    }

    /// <summary>Deletes the group.</summary>
    /// <param name="cancellationToken">A cancellation token.</param>
    /// <returns>A task that completes when the server has deleted it.</returns>
    /// <exception cref="ZabbixNotFoundException">The node was reached by name, and the group does not exist.</exception>
    /// <exception cref="ZabbixApiException">The server refused, or the node was reached by id and the server has no group with it.</exception>
    public virtual async Task DeleteAsync(CancellationToken cancellationToken = default)
        => await Client.HostGroups.DeleteAsync(await IdAsync(cancellationToken).ConfigureAwait(false), cancellationToken).ConfigureAwait(false);

    /// <summary>
    /// Changes the group: names it by its id in a <c>hostgroup.update</c> request, reading the id first when the node was
    /// reached by name, and lets <paramref name="configure"/> set the fields to change, <c>g =&gt; g.Name = "Linux servers"</c>
    /// to rename it.
    /// </summary>
    /// <param name="configure">Sets the fields to change.</param>
    /// <param name="cancellationToken">A cancellation token.</param>
    /// <returns>A task that completes when the server has changed the group.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="configure"/> is <see langword="null"/>.</exception>
    /// <exception cref="ZabbixNotFoundException">The node was reached by name, and the group does not exist.</exception>
    /// <exception cref="ZabbixApiException">The server refused the change, or the node was reached by id and the server has no group with it.</exception>
    public virtual Task UpdateAsync(Action<ZabbixHostGroupUpdateRequest> configure, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(configure);
        return UpdateCoreAsync(configure, cancellationToken);
    }

    private async Task UpdateCoreAsync(Action<ZabbixHostGroupUpdateRequest> configure, CancellationToken cancellationToken)
    {
        var request = new ZabbixHostGroupUpdateRequest(await IdAsync(cancellationToken).ConfigureAwait(false));
        configure(request);
        await Client.HostGroups.UpdateAsync(request, cancellationToken).ConfigureAwait(false);
    }

    // The id the node was reached by, or the one hostgroup.get answers for its name.
    private async Task<ulong> IdAsync(CancellationToken cancellationToken)
        => Selection.GroupId ?? (await GetAsync(cancellationToken).ConfigureAwait(false)).GroupId ?? throw Selection.AbsentGroup();
}
