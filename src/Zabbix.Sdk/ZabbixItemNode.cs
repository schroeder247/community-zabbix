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
    /// Changes the item: reads its id, names it in an <c>item.update</c> request, and lets <paramref name="configure"/> set
    /// the fields to change. What it leaves unset isn't sent, so the server keeps it.
    /// </summary>
    /// <param name="configure">Sets the fields to change: <c>i =&gt; i.Status = ZabbixItemStatus.Disabled</c>.</param>
    /// <param name="cancellationToken">A cancellation token.</param>
    /// <returns>A task that completes when the server has changed the item.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="configure"/> is <see langword="null"/>.</exception>
    /// <exception cref="ZabbixNotFoundException">The host, or the item, does not exist.</exception>
    public virtual Task UpdateAsync(Action<ZabbixItemUpdateRequest> configure, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(configure);
        return UpdateCoreAsync(configure, cancellationToken);
    }

    private async Task UpdateCoreAsync(Action<ZabbixItemUpdateRequest> configure, CancellationToken cancellationToken)
    {
        ZabbixItem item = await GetAsync(cancellationToken).ConfigureAwait(false);
        var request = new ZabbixItemUpdateRequest(item.ItemId ?? throw new ZabbixNotFoundException(HostName, Key));
        configure(request);
        await Client.Items.UpdateAsync(request, cancellationToken).ConfigureAwait(false);
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
