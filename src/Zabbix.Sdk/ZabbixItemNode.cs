// This Source Code Form is subject to the terms of the Mozilla Public
// License, v. 2.0. Copyright (c) 2026 schroeder247. SPDX-License-Identifier: MPL-2.0
namespace Community.Zabbix;

/// <summary>
/// Represents the leaf of the tree: one item on one host, <c>host.Items["system.uptime"]</c>. Its definition is the schema;
/// its latest value and history are the function's slice at this host and key.
/// </summary>
public class ZabbixItemNode : ZabbixNode
{
    // Null only in a node built for mocking, whose members are overridden.
    private readonly ZabbixHostNode? _host;

    /// <summary>Initializes an item node for mocking; every member of a node built this way is to be overridden.</summary>
    protected ZabbixItemNode()
    {
    }

    internal ZabbixItemNode(ZabbixClient client, ZabbixSelection selection, ZabbixHostNode host) : base(client, selection)
    {
        Key = selection.Key ?? throw new ArgumentException("An item node is selected by its key.", nameof(selection));
        _host = host;
    }

    /// <summary>The item's host: the node it was reached from.</summary>
    public virtual ZabbixHostNode Host => _host ?? throw new InvalidOperationException("The node was built for mocking, and this member is not overridden.");

    /// <summary>The item's key.</summary>
    public virtual ZabbixItemKey Key { get; }

    /// <summary>What <c>item.get</c> says about the item now, with its latest value.</summary>
    /// <param name="cancellationToken">A cancellation token.</param>
    /// <returns>The item, as <c>item.get</c> returns it.</returns>
    /// <exception cref="ZabbixNotFoundException">The host, or the item, does not exist.</exception>
    public virtual async Task<ZabbixItem> GetAsync(CancellationToken cancellationToken = default)
    {
        await foreach (ZabbixItem item in GetItemsAsync(cancellationToken).ConfigureAwait(false)) return item;
        throw ZabbixNotFoundException.NoItem(Selection.HostText, Key);
    }

    /// <summary>What <c>item.get</c> says about the item now, if there is one.</summary>
    /// <param name="cancellationToken">A cancellation token.</param>
    /// <returns>The item, or <see langword="null"/> when the host, or the item, does not exist.</returns>
    public virtual async Task<ZabbixItem?> FindAsync(CancellationToken cancellationToken = default)
        => await ReadItemsAsync(cancellationToken).ConfigureAwait(false) is [var item, ..] ? item : null;

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
        var request = new ZabbixItemUpdateRequest(item.ItemId ?? throw ZabbixNotFoundException.NoItem(Selection.HostText, Key));
        configure(request);
        await Client.Items.UpdateAsync(request, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>The item's last value (<c>lastvalue</c>).</summary>
    /// <param name="cancellationToken">A cancellation token.</param>
    /// <returns>The value.</returns>
    /// <exception cref="ZabbixNotFoundException">The host, or the item, does not exist, or the item has never collected a value.</exception>
    public virtual async Task<ZabbixValue> GetLastValueAsync(CancellationToken cancellationToken = default)
        => await FindLastValueAsync(cancellationToken).ConfigureAwait(false) ?? throw ZabbixNotFoundException.NoValue(Selection.HostText, Key);

    /// <summary>
    /// The item's last value (<c>lastvalue</c>), or <see langword="null"/> when the item exists and has never collected. An item
    /// that does not exist is not a missing value, so it throws.
    /// </summary>
    /// <param name="cancellationToken">A cancellation token.</param>
    /// <returns>The value, or <see langword="null"/> when the item has never collected.</returns>
    /// <exception cref="ZabbixNotFoundException">The host, or the item, does not exist.</exception>
    public virtual async Task<ZabbixValue?> FindLastValueAsync(CancellationToken cancellationToken = default)
    {
        await foreach (ZabbixValue value in GetLatestAsync(cancellationToken).ConfigureAwait(false)) return value;
        return null;
    }
}
