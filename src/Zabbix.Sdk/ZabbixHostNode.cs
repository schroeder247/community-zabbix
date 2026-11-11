// This Source Code Form is subject to the terms of the Mozilla Public
// License, v. 2.0. Copyright (c) 2026 schroeder247. SPDX-License-Identifier: MPL-2.0
using System.Runtime.CompilerServices;

namespace Community.Zabbix;

/// <summary>
/// Represents one host, by its technical name, <c>zabbix.Hosts["web-01"]</c>, or by its id, <c>zabbix.Hosts[10084]</c>: the
/// same node either way. Read it, change it, delete it, reach its items by key (<c>host.Items["system.uptime"]</c>), or ask
/// it and every item of the host answers. No call is made until it's asked.
/// </summary>
public class ZabbixHostNode : ZabbixNode
{
    // Null only in a node built for mocking, whose members are overridden.
    private readonly ZabbixItemScope? _items;

    /// <summary>Initializes a host node for mocking; every member of a node built this way is to be overridden.</summary>
    protected ZabbixHostNode()
    {
    }

    internal ZabbixHostNode(ZabbixClient client, ZabbixSelection selection) : base(client, selection)
    {
        if (!selection.HasHost) throw new ArgumentException("A host node is selected by its host.", nameof(selection));
        _items = new ZabbixItemScope(this);
    }

    /// <summary>The host's id, when the node was reached by it or by enumerating hosts; <see langword="null"/> when it was reached by name.</summary>
    public virtual ulong? Id => Selection.HostId;

    /// <summary>The host's technical name, when the node was reached by it or by enumerating hosts; <see langword="null"/> when it was reached by id.</summary>
    public virtual string? HostName => Selection.Host;

    /// <summary>
    /// The host's items: reach one by its key, <c>Items["system.uptime"]</c> or <c>Items[key]</c>, or enumerate them. No call
    /// is made until it's asked.
    /// </summary>
    public virtual ZabbixItemScope Items => _items ?? throw new InvalidOperationException("The node was built for mocking, and this member is not overridden.");

    /// <summary>Reads the host.</summary>
    /// <param name="cancellationToken">A cancellation token.</param>
    /// <returns>The host, as <c>host.get</c> returns it.</returns>
    /// <exception cref="ZabbixNotFoundException">The host does not exist.</exception>
    public virtual async Task<ZabbixHost> GetAsync(CancellationToken cancellationToken = default)
        => await FindAsync(cancellationToken).ConfigureAwait(false) ?? throw Selection.AbsentHost();

    /// <summary>Reads the host, if there is one.</summary>
    /// <param name="cancellationToken">A cancellation token.</param>
    /// <returns>The host, or <see langword="null"/> when the server has none of this name or id that the caller may read.</returns>
    public virtual async Task<ZabbixHost?> FindAsync(CancellationToken cancellationToken = default)
    {
        foreach (ZabbixHost host in await Client.Hosts.GetAsync(Selection.ToHostGet(), cancellationToken).ConfigureAwait(false))
            return host;
        return null;
    }

    /// <summary>Deletes the host.</summary>
    /// <param name="cancellationToken">A cancellation token.</param>
    /// <returns>A task that completes when the server has deleted it.</returns>
    /// <exception cref="ZabbixNotFoundException">The node was reached by name, and the host does not exist.</exception>
    /// <exception cref="ZabbixApiException">The server refused, or the node was reached by id and the server has no host with it.</exception>
    public virtual async Task DeleteAsync(CancellationToken cancellationToken = default)
        => await Client.Hosts.DeleteAsync(await IdAsync(cancellationToken).ConfigureAwait(false), cancellationToken).ConfigureAwait(false);

    /// <summary>
    /// Changes the host: names it by its id in a <c>host.update</c> request, reading the id first when the node was reached by
    /// name, and lets <paramref name="configure"/> set the fields to change. What it leaves unset isn't sent, so the server
    /// keeps it.
    /// </summary>
    /// <param name="configure">Sets the fields to change: <c>h =&gt; h.Status = ZabbixHostStatus.Disabled</c>.</param>
    /// <param name="cancellationToken">A cancellation token.</param>
    /// <returns>A task that completes when the server has changed the host.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="configure"/> is <see langword="null"/>.</exception>
    /// <exception cref="ZabbixNotFoundException">The node was reached by name, and the host does not exist.</exception>
    /// <exception cref="ZabbixApiException">The server refused the change, or the node was reached by id and the server has no host with it.</exception>
    public virtual Task UpdateAsync(Action<ZabbixHostUpdateRequest> configure, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(configure);
        return UpdateCoreAsync(configure, cancellationToken);
    }

    private async Task UpdateCoreAsync(Action<ZabbixHostUpdateRequest> configure, CancellationToken cancellationToken)
    {
        var request = new ZabbixHostUpdateRequest(await IdAsync(cancellationToken).ConfigureAwait(false));
        configure(request);
        await Client.Hosts.UpdateAsync(request, cancellationToken).ConfigureAwait(false);
    }

    // The id the node was reached by, or the one host.get answers for its name.
    private async Task<ulong> IdAsync(CancellationToken cancellationToken)
        => Selection.HostId ?? (await GetAsync(cancellationToken).ConfigureAwait(false)).HostId ?? throw Selection.AbsentHost();

    /// <summary>Reads a typed item's last value.</summary>
    /// <typeparam name="T">The item's value type.</typeparam>
    /// <param name="key">The item's key.</param>
    /// <param name="cancellationToken">A cancellation token.</param>
    /// <returns>The value.</returns>
    /// <exception cref="ZabbixNotFoundException">The host or the item doesn't exist, or the item has never collected a value.</exception>
    /// <exception cref="FormatException">The value isn't a <typeparamref name="T"/>.</exception>
    public virtual async Task<T> GetLastAsync<T>(ZabbixItemKey<T> key, CancellationToken cancellationToken = default)
        where T : ISpanParsable<T>
        => key.Parse((await Items[key.Key].GetLastValueAsync(cancellationToken).ConfigureAwait(false)).Value);

    /// <summary>Reads a typed item's last value and when it was collected, or <see langword="null"/> when it has never collected one.</summary>
    /// <typeparam name="T">The item's value type.</typeparam>
    /// <param name="key">The item's key.</param>
    /// <param name="cancellationToken">A cancellation token.</param>
    /// <returns>The value and its time, or <see langword="null"/>.</returns>
    /// <exception cref="ZabbixNotFoundException">The host or the item doesn't exist.</exception>
    /// <exception cref="FormatException">The value isn't a <typeparamref name="T"/>.</exception>
    public virtual async Task<ZabbixSample<T>?> FindLastAsync<T>(ZabbixItemKey<T> key, CancellationToken cancellationToken = default)
        where T : ISpanParsable<T>
        => await Items[key.Key].FindLastValueAsync(cancellationToken).ConfigureAwait(false) is { } value
            ? new ZabbixSample<T>(value.Time, key.Parse(value.Value))
            : null;

    /// <summary>
    /// Reads a typed item's history over the last <paramref name="window"/>, oldest first: from <paramref name="window"/> before
    /// the call, on the client's clock, up to now.
    /// </summary>
    /// <typeparam name="T">The item's value type.</typeparam>
    /// <param name="key">The item's key.</param>
    /// <param name="window">How far back to read.</param>
    /// <param name="cancellationToken">A cancellation token, which reaches the socket.</param>
    /// <returns>The values and their times.</returns>
    /// <exception cref="ZabbixNotFoundException">The host or the item doesn't exist.</exception>
    public virtual IAsyncEnumerable<ZabbixSample<T>> GetHistoryAsync<T>(ZabbixItemKey<T> key, TimeSpan window, CancellationToken cancellationToken = default)
        where T : ISpanParsable<T>
        => GetHistoryAsync(key, Client.Time.GetUtcNow() - window, null, cancellationToken);

    /// <summary>Reads a typed item's history from <paramref name="from"/> to <paramref name="to"/>, oldest first.</summary>
    /// <typeparam name="T">The item's value type.</typeparam>
    /// <param name="key">The item's key.</param>
    /// <param name="from">The earliest time to read.</param>
    /// <param name="to">The latest time to read; now when <see langword="null"/>.</param>
    /// <param name="cancellationToken">A cancellation token, which reaches the socket.</param>
    /// <returns>The values and their times.</returns>
    /// <exception cref="ZabbixNotFoundException">The host or the item doesn't exist.</exception>
    [System.Diagnostics.CodeAnalysis.SuppressMessage("Naming", "CA1716:Identifiers should not match keywords",
        Justification = "The window is from and to, as the history.get parameters' TimeFrom and TimeTo are; a Visual Basic caller writes [to].")]
    public virtual async IAsyncEnumerable<ZabbixSample<T>> GetHistoryAsync<T>(ZabbixItemKey<T> key, DateTimeOffset from, DateTimeOffset? to, [EnumeratorCancellation] CancellationToken cancellationToken = default)
        where T : ISpanParsable<T>
    {
        await foreach (ZabbixValue value in Items[key.Key].GetHistoryAsync(from, to, cancellationToken).ConfigureAwait(false))
            yield return new ZabbixSample<T>(value.Time, key.Parse(value.Value));
    }
}
