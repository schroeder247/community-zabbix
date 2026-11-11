// This Source Code Form is subject to the terms of the Mozilla Public
// License, v. 2.0. Copyright (c) 2026 schroeder247. SPDX-License-Identifier: MPL-2.0
namespace Community.Zabbix;

public partial class ZabbixHostGroupClient
{
    /// <summary>Sets the client the indexers' nodes read through.</summary>
    internal ZabbixClient? Owner { get; set; }

    /// <summary>
    /// Gets one host group, by name: <c>zabbix.HostGroups["Linux"]</c>, which reads, changes and deletes the group and reaches
    /// its hosts, <c>zabbix.HostGroups["Linux"].Hosts["web-01"]</c>. No call is made until it's asked.
    /// </summary>
    /// <param name="name">The group's name.</param>
    /// <returns>The group's node, the one its id reaches too.</returns>
    /// <exception cref="ArgumentException"><paramref name="name"/> is empty.</exception>
    public virtual ZabbixHostGroupNode this[string name]
    {
        get
        {
            ArgumentException.ThrowIfNullOrWhiteSpace(name);
            return new ZabbixHostGroupNode(Root, new ZabbixSelection(Group: name));
        }
    }

    /// <summary>Gets one host group, by its id: <c>zabbix.HostGroups[5]</c>. No call is made until it's asked.</summary>
    /// <param name="id">The group's id.</param>
    /// <returns>The group's node, the one its name reaches too.</returns>
    public virtual ZabbixHostGroupNode this[ulong id] => new(Root, new ZabbixSelection(GroupId: id));

    // The client the nodes read through, which a client built for mocking hasn't got.
    private ZabbixClient Root
    {
        get
        {
            ZabbixClient owner = Owner ?? throw new InvalidOperationException("The client was built for mocking, and this member is not overridden.");
            owner.ThrowIfDisposed();
            return owner;
        }
    }
}
