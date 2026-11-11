// This Source Code Form is subject to the terms of the Mozilla Public
// License, v. 2.0. Copyright (c) 2026 schroeder247. SPDX-License-Identifier: MPL-2.0
namespace Community.Zabbix;

public partial class ZabbixHostClient
{
    /// <summary>Sets the client the indexers' nodes read through.</summary>
    internal ZabbixClient? Owner { get; set; }

    /// <summary>Gets one host, by technical name: <c>zabbix.Hosts["web-01"]</c>. No call is made until it's asked.</summary>
    /// <param name="name">The host's technical name.</param>
    /// <returns>The host's node, the one its id reaches too.</returns>
    /// <exception cref="ArgumentException"><paramref name="name"/> is empty.</exception>
    public virtual ZabbixHostNode this[string name]
    {
        get
        {
            ArgumentException.ThrowIfNullOrWhiteSpace(name);
            return new ZabbixHostNode(Root, new ZabbixSelection(Host: name));
        }
    }

    /// <summary>Gets one host, by its id: <c>zabbix.Hosts[10084]</c>. No call is made until it's asked.</summary>
    /// <param name="id">The host's id.</param>
    /// <returns>The host's node, the one its technical name reaches too.</returns>
    public virtual ZabbixHostNode this[ulong id] => new(Root, new ZabbixSelection(HostId: id));

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
