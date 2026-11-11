// This Source Code Form is subject to the terms of the Mozilla Public
// License, v. 2.0. Copyright (c) 2026 schroeder247. SPDX-License-Identifier: MPL-2.0
namespace Community.Zabbix;

/// <summary>
/// Represents a set of hosts: the server's, <c>zabbix.AllHosts</c>, or a group's, <c>zabbix.HostGroups["Linux"].Hosts</c>.
/// Enumerate it for the hosts (one <c>host.get</c>, read whole and then yielded); reach one by its technical name,
/// <c>Hosts["web-01"]</c>, at no cost; ask it and every item beneath answers.
/// </summary>
public class ZabbixHostScope : ZabbixNode, IAsyncEnumerable<ZabbixHostNode>
{
    private static readonly ZabbixOutput<ZabbixHost> s_hostFields = [ZabbixHostFields.HostId, ZabbixHostFields.HostName];

    /// <summary>Initializes a scope for mocking; every member of a scope built this way is to be overridden.</summary>
    protected ZabbixHostScope()
    {
    }

    internal ZabbixHostScope(ZabbixClient client, ZabbixSelection selection) : base(client, selection)
    {
    }

    /// <summary>
    /// Gets one host of this set, by technical name: <c>Hosts["web-01"]</c>. A group's host is read only while it's in the group.
    /// No call is made until it's asked.
    /// </summary>
    /// <param name="hostName">The host's technical name.</param>
    /// <returns>The host's node.</returns>
    /// <exception cref="ArgumentException"><paramref name="hostName"/> is empty.</exception>
    public virtual ZabbixHostNode this[string hostName]
    {
        get
        {
            ArgumentException.ThrowIfNullOrWhiteSpace(hostName);
            Client.ThrowIfDisposed();
            return new ZabbixHostNode(Client, Selection with { Host = hostName });
        }
    }

    /// <inheritdoc/>
    /// <exception cref="ZabbixNotFoundException">The group does not exist.</exception>
    public virtual async IAsyncEnumerator<ZabbixHostNode> GetAsyncEnumerator(CancellationToken cancellationToken = default)
    {
        IReadOnlyList<ulong>? groups = null;
        if (Selection.HasGroup)
        {
            groups = await GroupIdsAsync(cancellationToken).ConfigureAwait(false);
            if (groups.Count == 0) throw Selection.AbsentGroup();
        }
        foreach (ZabbixHost host in await Client.Hosts.GetAsync(new ZabbixHostGetParameters { Output = s_hostFields, GroupIds = groups }, cancellationToken).ConfigureAwait(false))
        {
            if (host.HostName is { } name) yield return new ZabbixHostNode(Client, Selection with { Host = name, HostId = host.HostId });
        }
    }
}
