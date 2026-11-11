// This Source Code Form is subject to the terms of the Mozilla Public
// License, v. 2.0. Copyright (c) 2026 schroeder247. SPDX-License-Identifier: MPL-2.0
namespace Community.Zabbix;

/// <summary>
/// Represents a set of hosts: the server's, or a group's. Enumerate it for the hosts (one <c>host.get</c>, read whole and then
/// yielded); descend with <see cref="Host"/> at no cost; ask it and every item beneath answers.
/// </summary>
public class ZabbixHostScope : ZabbixNode, IAsyncEnumerable<ZabbixHostNode>
{
    private static readonly ZabbixOutput s_hostFields = ZabbixOutput.Of("hostid", "host");

    /// <summary>Initializes a scope for mocking; every member of a scope built this way is to be overridden.</summary>
    protected ZabbixHostScope()
    {
    }

    internal ZabbixHostScope(ZabbixClient client, ZabbixSelection selection) : base(client, selection)
    {
    }

    /// <summary>The group this scope is, when it is one.</summary>
    public virtual string? Group => Selection.Group;

    /// <summary>One host beneath this scope, by technical name; no call is made until it is asked.</summary>
    /// <param name="host">The technical name.</param>
    /// <returns>The host's node.</returns>
    public virtual ZabbixHostNode Host(string host)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(host);
        return new ZabbixHostNode(Client, Selection with { Host = host });
    }

    /// <inheritdoc/>
    /// <exception cref="ZabbixNotFoundException">The group does not exist.</exception>
    public virtual async IAsyncEnumerator<ZabbixHostNode> GetAsyncEnumerator(CancellationToken cancellationToken = default)
    {
        IReadOnlyList<ulong>? groups = null;
        if (Selection.Group is { } group)
        {
            groups = await GroupIdsAsync(group, cancellationToken).ConfigureAwait(false);
            if (groups.Count == 0) throw new ZabbixNotFoundException("hostgroup", group);
        }
        foreach (ZabbixHost host in await Client.Hosts.GetAsync(new ZabbixHostGetParameters { Output = s_hostFields, GroupIds = groups }, cancellationToken).ConfigureAwait(false))
        {
            if (host.TechnicalName is { } name) yield return new ZabbixHostNode(Client, Selection with { Host = name });
        }
    }
}
