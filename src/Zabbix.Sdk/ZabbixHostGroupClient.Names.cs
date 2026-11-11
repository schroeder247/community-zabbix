// This Source Code Form is subject to the terms of the Mozilla Public
// License, v. 2.0. Copyright (c) 2026 schroeder247. SPDX-License-Identifier: MPL-2.0
namespace Community.Zabbix;

public partial class ZabbixHostGroupClient
{
    /// <summary>Sets the client the name indexer's scopes read through.</summary>
    internal ZabbixClient? Owner { get; set; }

    /// <summary>Gets the hosts in one group, by the group's name: <c>zabbix.HostGroups["Linux"]</c>. No call is made until it's asked.</summary>
    /// <param name="name">The group's name.</param>
    /// <returns>A selection of the group's hosts.</returns>
    /// <exception cref="ArgumentException"><paramref name="name"/> is empty.</exception>
    public virtual ZabbixHostScope this[string name]
    {
        get
        {
            ArgumentException.ThrowIfNullOrWhiteSpace(name);
            ZabbixClient owner = Owner ?? throw new InvalidOperationException("The client was built for mocking, and this member is not overridden.");
            owner.ThrowIfDisposed();
            return new ZabbixHostScope(owner, new ZabbixSelection(Group: name));
        }
    }
}
