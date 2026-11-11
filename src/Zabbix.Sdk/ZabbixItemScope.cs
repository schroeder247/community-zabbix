// This Source Code Form is subject to the terms of the Mozilla Public
// License, v. 2.0. Copyright (c) 2026 schroeder247. SPDX-License-Identifier: MPL-2.0
namespace Community.Zabbix;

/// <summary>
/// Represents a host's items, <c>host.Items</c>: reach one by its key, <c>host.Items["system.uptime"]</c>, at no cost, or
/// enumerate them (one <c>item.get</c>, read whole and then yielded). A typed read takes its key on the host,
/// <c>host.GetLastAsync(ZabbixItemKeys.Cpu.Load())</c>, because C# has no generic indexers.
/// </summary>
public class ZabbixItemScope : IAsyncEnumerable<ZabbixItemNode>
{
    // Null only in a scope built for mocking, whose members are overridden.
    private readonly ZabbixHostNode? _host;

    /// <summary>Initializes a scope for mocking; every member of a scope built this way is to be overridden.</summary>
    protected ZabbixItemScope()
    {
    }

    internal ZabbixItemScope(ZabbixHostNode host) => _host = host;

    private ZabbixHostNode Host => _host ?? throw new InvalidOperationException("The scope was built for mocking, and this member is not overridden.");

    /// <summary>Gets one item of the host, by its key's text: <c>Items["system.cpu.load[all,avg1]"]</c>. No call is made until it's asked.</summary>
    /// <param name="key">The item's key, as Zabbix writes it.</param>
    /// <returns>The item's node.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="key"/> is <see langword="null"/>.</exception>
    /// <exception cref="FormatException"><paramref name="key"/> isn't an item key.</exception>
    public virtual ZabbixItemNode this[string key] => this[ZabbixItemKey.Parse(key)];

    /// <summary>Gets one item of the host, by its key: <c>Items[ZabbixItemKeys.Cpu.Load().Key]</c>. No call is made until it's asked.</summary>
    /// <param name="key">The item's key.</param>
    /// <returns>The item's node.</returns>
    [System.Diagnostics.CodeAnalysis.SuppressMessage("Design", "CA1043:Use Integral Or String Argument For Indexers",
        Justification = "The key is the item's identity on its host, as a dictionary's key is; the indexer beside it takes the key's text.")]
    public virtual ZabbixItemNode this[ZabbixItemKey key]
    {
        get
        {
            ZabbixHostNode host = Host;
            host.Client.ThrowIfDisposed();
            return new ZabbixItemNode(host.Client, host.Selection with { Key = key }, host);
        }
    }

    /// <inheritdoc/>
    /// <exception cref="ZabbixNotFoundException">The host does not exist.</exception>
    public virtual async IAsyncEnumerator<ZabbixItemNode> GetAsyncEnumerator(CancellationToken cancellationToken = default)
    {
        ZabbixHostNode host = Host;
        var keys = new ZabbixItemKeyReport(host.Logger);
        await foreach (ZabbixItem item in host.GetItemsAsync(cancellationToken).ConfigureAwait(false))
            if (keys.TryRead(item, out ZabbixItemKey key)) yield return new ZabbixItemNode(host.Client, host.Selection with { Key = key }, host);
    }
}
