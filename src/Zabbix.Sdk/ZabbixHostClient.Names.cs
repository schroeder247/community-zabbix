// This Source Code Form is subject to the terms of the Mozilla Public
// License, v. 2.0. Copyright (c) 2026 schroeder247. SPDX-License-Identifier: MPL-2.0
namespace Community.Zabbix;

public partial class ZabbixHostClient
{
    /// <summary>Sets the client the name indexer's nodes read through.</summary>
    internal ZabbixClient? Owner { get; set; }

    /// <summary>Gets one host, by technical name: <c>zabbix.Hosts["web-01"]</c>. No call is made until it's asked.</summary>
    /// <param name="name">The host's technical name.</param>
    /// <returns>The host's node.</returns>
    /// <exception cref="ArgumentException"><paramref name="name"/> is empty.</exception>
    public virtual ZabbixHostNode this[string name]
    {
        get
        {
            ArgumentException.ThrowIfNullOrWhiteSpace(name);
            ZabbixClient owner = Owner ?? throw new InvalidOperationException("The client was built for mocking, and this member is not overridden.");
            owner.ThrowIfDisposed();
            return new ZabbixHostNode(owner, new ZabbixSelection(Host: name));
        }
    }
}
