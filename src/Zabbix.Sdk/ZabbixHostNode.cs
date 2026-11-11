// This Source Code Form is subject to the terms of the Mozilla Public
// License, v. 2.0. Copyright (c) 2026 schroeder247. SPDX-License-Identifier: MPL-2.0
using System.Globalization;
using System.Runtime.CompilerServices;

namespace Community.Zabbix;

/// <summary>
/// Represents one host: a scope of one whose items are the key axis (<see cref="Item(ZabbixItemKey)"/>); asking it asks
/// them all.
/// </summary>
public class ZabbixHostNode : ZabbixNode, IAsyncEnumerable<ZabbixItemNode>
{
    /// <summary>Reads a typed item's last value.</summary>
    /// <typeparam name="T">The item's value type.</typeparam>
    /// <param name="item">The item to read.</param>
    /// <param name="cancellationToken">A cancellation token.</param>
    /// <returns>The value.</returns>
    /// <exception cref="ZabbixNotFoundException">The host or the item doesn't exist, or the item has never collected a value.</exception>
    /// <exception cref="FormatException">The value isn't a <typeparamref name="T"/>.</exception>
    public virtual async Task<T> GetLastAsync<T>(ZabbixItemKey<T> item, CancellationToken cancellationToken = default)
        where T : ISpanParsable<T>
        => await FindLastAsync(item, cancellationToken).ConfigureAwait(false) is { } sample
            ? sample.Value
            : throw new ZabbixNotFoundException($"The item on host '{Name}' has never collected a value.");

    /// <summary>Reads a typed item's last value and when it was collected, or <see langword="null"/> when it has never collected one.</summary>
    /// <typeparam name="T">The item's value type.</typeparam>
    /// <param name="item">The item to read.</param>
    /// <param name="cancellationToken">A cancellation token.</param>
    /// <returns>The value and its time, or <see langword="null"/>.</returns>
    /// <exception cref="ZabbixNotFoundException">The host or the item doesn't exist.</exception>
    /// <exception cref="FormatException">The value isn't a <typeparamref name="T"/>.</exception>
    public virtual async Task<ZabbixSample<T>?> FindLastAsync<T>(ZabbixItemKey<T> item, CancellationToken cancellationToken = default)
        where T : ISpanParsable<T>
        => await Item(item.Key).GetLastValueAsync(cancellationToken).ConfigureAwait(false) is { } value
            ? new ZabbixSample<T>(value.Time, item.Parse(value.Value))
            : null;

    /// <summary>
    /// Reads a typed item's history over the last <paramref name="window"/>, oldest first, as
    /// <see cref="GetHistoryAsync{T}(ZabbixItemKey{T}, TimeSpan, CancellationToken)"/> does.
    /// </summary>
    /// <typeparam name="T">The item's value type.</typeparam>
    /// <param name="item">The item to read.</param>
    /// <param name="window">How far back to read.</param>
    /// <returns>The values and their times.</returns>
    /// <exception cref="ZabbixNotFoundException">The host or the item doesn't exist.</exception>
    public IAsyncEnumerable<ZabbixSample<T>> GetHistoryAsync<T>(ZabbixItemKey<T> item, TimeSpan window)
        where T : ISpanParsable<T>
        => GetHistoryAsync(item, window, CancellationToken.None);

    /// <summary>
    /// Reads a typed item's history over the last <paramref name="window"/>, oldest first: from <paramref name="window"/> before
    /// the call, on the client's clock, up to now.
    /// </summary>
    /// <typeparam name="T">The item's value type.</typeparam>
    /// <param name="item">The item to read.</param>
    /// <param name="window">How far back to read.</param>
    /// <param name="cancellationToken">A cancellation token, which reaches the socket.</param>
    /// <returns>The values and their times.</returns>
    /// <exception cref="ZabbixNotFoundException">The host or the item doesn't exist.</exception>
    public virtual IAsyncEnumerable<ZabbixSample<T>> GetHistoryAsync<T>(ZabbixItemKey<T> item, TimeSpan window, CancellationToken cancellationToken)
        where T : ISpanParsable<T>
        => GetHistoryAsync(item, Client.Time.GetUtcNow() - window, null, cancellationToken);

    /// <summary>
    /// Reads a typed item's history from <paramref name="from"/> to <paramref name="till"/>, oldest first, as
    /// <see cref="GetHistoryAsync{T}(ZabbixItemKey{T}, DateTimeOffset, DateTimeOffset?, CancellationToken)"/> does.
    /// </summary>
    /// <typeparam name="T">The item's value type.</typeparam>
    /// <param name="item">The item to read.</param>
    /// <param name="from">The earliest time to read.</param>
    /// <param name="till">The latest time to read; now when <see langword="null"/>.</param>
    /// <returns>The values and their times.</returns>
    /// <exception cref="ZabbixNotFoundException">The host or the item doesn't exist.</exception>
    public IAsyncEnumerable<ZabbixSample<T>> GetHistoryAsync<T>(ZabbixItemKey<T> item, DateTimeOffset from, DateTimeOffset? till)
        where T : ISpanParsable<T>
        => GetHistoryAsync(item, from, till, CancellationToken.None);

    /// <summary>Reads a typed item's history from <paramref name="from"/> to <paramref name="till"/>, oldest first.</summary>
    /// <typeparam name="T">The item's value type.</typeparam>
    /// <param name="item">The item to read.</param>
    /// <param name="from">The earliest time to read.</param>
    /// <param name="till">The latest time to read; now when <see langword="null"/>.</param>
    /// <param name="cancellationToken">A cancellation token, which reaches the socket.</param>
    /// <returns>The values and their times.</returns>
    /// <exception cref="ZabbixNotFoundException">The host or the item doesn't exist.</exception>
    public virtual async IAsyncEnumerable<ZabbixSample<T>> GetHistoryAsync<T>(ZabbixItemKey<T> item, DateTimeOffset from, DateTimeOffset? till, [EnumeratorCancellation] CancellationToken cancellationToken)
        where T : ISpanParsable<T>
    {
        await foreach (ZabbixValue value in Item(item.Key).GetHistoryAsync(from, till, cancellationToken).ConfigureAwait(false))
            yield return new ZabbixSample<T>(value.Time, item.Parse(value.Value));
    }

    /// <summary>Initializes a host node for mocking; every member of a node built this way is to be overridden.</summary>
    protected ZabbixHostNode()
    {
        Name = string.Empty;
    }

    internal ZabbixHostNode(ZabbixClient client, ZabbixSelection selection) : base(client, selection)
    {
        Name = selection.Host ?? throw new ArgumentException("A host node is selected by its host.", nameof(selection));
    }

    /// <summary>The host's technical name.</summary>
    public virtual string Name { get; }

    /// <summary>Reads the host.</summary>
    /// <param name="cancellationToken">A cancellation token.</param>
    /// <returns>The host, as <c>host.get</c> returns it.</returns>
    /// <exception cref="ZabbixNotFoundException">The host does not exist.</exception>
    public virtual async Task<ZabbixHost> GetAsync(CancellationToken cancellationToken = default)
        => await FindAsync(cancellationToken).ConfigureAwait(false) ?? throw new ZabbixNotFoundException("host", Name);

    /// <summary>Reads the host, if there is one.</summary>
    /// <param name="cancellationToken">A cancellation token.</param>
    /// <returns>The host, or <see langword="null"/> when the server has none of this name that the caller may read.</returns>
    public virtual async Task<ZabbixHost?> FindAsync(CancellationToken cancellationToken = default)
    {
        foreach (ZabbixHost host in await Client.Hosts.GetAsync(new ZabbixHostGetParameters
        {
            Filter = new() { ["host"] = Name },
            Limit = 1,
        }, cancellationToken).ConfigureAwait(false))
        {
            return host;
        }
        return null;
    }

    /// <summary>Deletes the host.</summary>
    /// <param name="cancellationToken">A cancellation token.</param>
    /// <returns>A task that completes when the server has deleted it.</returns>
    /// <exception cref="ZabbixNotFoundException">The host does not exist.</exception>
    public virtual async Task DeleteAsync(CancellationToken cancellationToken = default)
    {
        ZabbixHost host = await GetAsync(cancellationToken).ConfigureAwait(false);
        await Client.Hosts.DeleteAsync(host.HostId ?? throw new ZabbixNotFoundException("host", Name), cancellationToken).ConfigureAwait(false);
    }

    /// <summary>
    /// Changes the host: reads its id, names it in a <c>host.update</c> request, and lets <paramref name="configure"/> set
    /// the fields to change. What it leaves unset isn't sent, so the server keeps it.
    /// </summary>
    /// <param name="configure">Sets the fields to change: <c>h =&gt; h.Status = ZabbixHostStatus.Disabled</c>.</param>
    /// <param name="cancellationToken">A cancellation token.</param>
    /// <returns>A task that completes when the server has changed the host.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="configure"/> is <see langword="null"/>.</exception>
    /// <exception cref="ZabbixNotFoundException">The host does not exist.</exception>
    public virtual Task UpdateAsync(Action<ZabbixHostUpdateRequest> configure, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(configure);
        return UpdateCoreAsync(configure, cancellationToken);
    }

    private async Task UpdateCoreAsync(Action<ZabbixHostUpdateRequest> configure, CancellationToken cancellationToken)
    {
        ZabbixHost host = await GetAsync(cancellationToken).ConfigureAwait(false);
        var request = new ZabbixHostUpdateRequest(host.HostId ?? throw new ZabbixNotFoundException("host", Name));
        configure(request);
        await Client.Hosts.UpdateAsync(request, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>One item of this host, by key; no call is made until it is asked.</summary>
    /// <param name="key">The item key.</param>
    /// <returns>The item's node.</returns>
    public virtual ZabbixItemNode Item(ZabbixItemKey key) => new(Client, Selection with { Key = key });

    /// <inheritdoc cref="Item(ZabbixItemKey)"/>
    public virtual ZabbixItemNode Item(string key) => Item(ZabbixItemKey.Parse(key, CultureInfo.InvariantCulture));

    /// <inheritdoc/>
    /// <exception cref="ZabbixNotFoundException">The host does not exist.</exception>
    public virtual async IAsyncEnumerator<ZabbixItemNode> GetAsyncEnumerator(CancellationToken cancellationToken = default)
    {
        var keys = new ZabbixItemKeyReport(Logger);
        await foreach (ZabbixItem item in GetItemsAsync(cancellationToken).ConfigureAwait(false))
            if (keys.TryRead(item, out ZabbixItemKey key)) yield return new ZabbixItemNode(Client, Selection with { Key = key });
    }
}
