// This Source Code Form is subject to the terms of the Mozilla Public
// License, v. 2.0. Copyright (c) 2026 schroeder247. SPDX-License-Identifier: MPL-2.0
namespace Community.Zabbix;

/// <summary>
/// Represents the leaf of the tree: one item on one host. Its definition is the schema; its latest value and history are the function's
/// slice at this host and key.
/// </summary>
public class ZabbixItemNode : ZabbixNode
{
    /// <summary>Initializes an item node for mocking; every member of a node built this way is to be overridden.</summary>
    protected ZabbixItemNode()
    {
        HostName = string.Empty;
    }

    internal ZabbixItemNode(ZabbixClient client, ZabbixSelection selection) : base(client, selection)
    {
        HostName = selection.Host ?? throw new ArgumentException("An item node is selected by its host.", nameof(selection));
        Key = selection.Key ?? throw new ArgumentException("An item node is selected by its key.", nameof(selection));
    }

    /// <summary>The technical name of the item's host.</summary>
    public virtual string HostName { get; }

    /// <summary>The item's key.</summary>
    public virtual ZabbixItemKey Key { get; }

    /// <summary>What <c>item.get</c> says about the item now, with its latest value.</summary>
    /// <param name="cancellationToken">A cancellation token.</param>
    /// <returns>The item, as <c>item.get</c> returns it.</returns>
    /// <exception cref="ZabbixNotFoundException">The host, or the item, does not exist.</exception>
    public virtual async Task<ZabbixItem> GetAsync(CancellationToken cancellationToken = default)
    {
        await foreach (ZabbixItem item in GetItemsAsync(cancellationToken).ConfigureAwait(false)) return item;
        throw new ZabbixNotFoundException(HostName, Key);
    }

    /// <summary>
    /// The item's last value (<c>lastvalue</c>), or <see langword="null"/> when the item exists and has never collected.
    /// An item that does not exist is not a missing value, so it throws.
    /// </summary>
    /// <param name="cancellationToken">A cancellation token.</param>
    /// <returns>The value, or <see langword="null"/> when the item has never collected.</returns>
    /// <exception cref="ZabbixNotFoundException">The host, or the item, does not exist.</exception>
    public virtual async Task<ZabbixValue?> GetLastValueAsync(CancellationToken cancellationToken = default)
    {
        await foreach (ZabbixValue value in GetLatestAsync(cancellationToken).ConfigureAwait(false)) return value;
        return null;
    }
}
