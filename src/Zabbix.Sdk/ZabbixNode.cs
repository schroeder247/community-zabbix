// This Source Code Form is subject to the terms of the Mozilla Public
// License, v. 2.0. Copyright (c) 2026 schroeder247. SPDX-License-Identifier: MPL-2.0
using System.Globalization;
using System.Runtime.CompilerServices;
using Microsoft.Extensions.Logging;

namespace Community.Zabbix;

/// <summary>
/// What every node shares: the client it asks through, and the reads compiled from its selection. Its members are
/// virtual and a derived type has a protected constructor, so a test can stand in for any node of the tree.
/// </summary>
public abstract class ZabbixNode : IZabbixNode
{
    private static readonly ZabbixOutput<ZabbixItem> s_itemHostIdField = [ZabbixItemFields.HostId];
    private static readonly ZabbixOutput<ZabbixHost> s_hostIdField = [ZabbixHostFields.HostId];
    private static readonly ZabbixOutput<ZabbixHostGroup> s_groupIdField = [ZabbixHostGroupFields.GroupId];

    // Null only in a node built for mocking, whose members are overridden.
    private readonly ZabbixClient? _client;

    /// <summary>Initializes a node for mocking; every member of a node built this way is to be overridden.</summary>
    protected ZabbixNode()
    {
        Selection = new ZabbixSelection();
    }

    internal ZabbixNode(ZabbixClient client, ZabbixSelection selection)
    {
        _client = client;
        Selection = selection;
    }

    /// <summary>The server this node belongs to.</summary>
    public virtual ZabbixClient Client => _client ?? throw new InvalidOperationException("The node was built for mocking, and this member is not overridden.");

    internal ZabbixSelection Selection { get; }

    internal ILogger Logger => Client.NodeLogger;

    /// <summary>The item definitions beneath this node, with their latest values.</summary>
    /// <param name="cancellationToken">A cancellation token.</param>
    /// <returns>The items, as <c>item.get</c> returns them.</returns>
    /// <exception cref="ZabbixNotFoundException">This node's host, group or item does not exist.</exception>
    public virtual async IAsyncEnumerable<ZabbixItem> GetItemsAsync([EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        IReadOnlyList<ZabbixItem> items = await ReadItemsAsync(cancellationToken).ConfigureAwait(false);
        if (items.Count == 0) await ThrowIfAbsentAsync(cancellationToken).ConfigureAwait(false);
        foreach (ZabbixItem item in items) yield return item;
    }

    /// <summary>The items this node selects, as <c>item.get</c> returns them; empty when there are none, whatever the reason.</summary>
    internal async Task<IReadOnlyList<ZabbixItem>> ReadItemsAsync(CancellationToken cancellationToken)
    {
        IReadOnlyList<ZabbixItem> items = await Client.Items.GetAsync(Selection.ToItemGet(), cancellationToken).ConfigureAwait(false);
        return items.Count == 0 && Selection.Key is { Parameters.Count: > 0 } key ? await SameKeyAsync(key, cancellationToken).ConfigureAwait(false) : items;
    }

    // A key's text is what the API filters on, and a template may quote what a caller doesn't: net.if.in["eth0"] and
    // net.if.in[eth0] are one key. So a key with parameters that wasn't found as written is matched by its parts.
    private async Task<IReadOnlyList<ZabbixItem>> SameKeyAsync(ZabbixItemKey key, CancellationToken cancellationToken)
    {
        IReadOnlyList<ZabbixItem> named = await Client.Items.GetAsync(Selection.ToKeySearch(), cancellationToken).ConfigureAwait(false);
        return [.. named.Where(item => ZabbixItemKey.TryParse(item.Key, out ZabbixItemKey other) && other.Name == key.Name && other.Parameters.SequenceEqual(key.Parameters))];
    }

    /// <summary>
    /// Whether this node's host, group or item exists on the server (Tester-Doer beside the reads, which throw
    /// <see cref="ZabbixNotFoundException"/> for a missing host, group or item). The server itself always exists.
    /// </summary>
    /// <param name="cancellationToken">A cancellation token.</param>
    /// <returns>Whether it exists.</returns>
    public virtual async Task<bool> ExistsAsync(CancellationToken cancellationToken = default)
    {
        if (Selection.Key is { } itemKey)
        {
            foreach (ZabbixItem _ in await Client.Items.GetAsync(Selection.ToItemGet() with { Output = s_itemHostIdField, SelectHosts = null, Limit = 1 }, cancellationToken).ConfigureAwait(false))
                return true;
            return itemKey.Parameters.Count > 0 && (await SameKeyAsync(itemKey, cancellationToken).ConfigureAwait(false)).Count > 0;
        }
        if (Selection.HasHost) return await HostExistsAsync(cancellationToken).ConfigureAwait(false);
        if (Selection.HasGroup) return (await GroupIdsAsync(cancellationToken).ConfigureAwait(false)).Count > 0;
        return true;
    }

    /// <summary>
    /// After a read that found nothing: nothing because the host, group or item is absent throws; nothing on a present host
    /// or group is an answer. An item node's read filters on its key, so an empty answer on a present host means the key is
    /// not there.
    /// </summary>
    private async Task ThrowIfAbsentAsync(CancellationToken cancellationToken)
    {
        if (Selection.HasHost && !await HostExistsAsync(cancellationToken).ConfigureAwait(false))
            throw Selection.AbsentHost();
        if (Selection.HasGroup && (await GroupIdsAsync(cancellationToken).ConfigureAwait(false)).Count == 0)
            throw Selection.AbsentGroup();
        if (Selection is { Key: { } key, HasHost: true })
            throw ZabbixNotFoundException.NoItem(Selection.HostText, key);
    }

    // The selection's host, whichever way it was asked for: its technical name, its id, or both.
    private async Task<bool> HostExistsAsync(CancellationToken cancellationToken)
    {
        foreach (ZabbixHost _ in await Client.Hosts.GetAsync(Selection.ToHostGet() with { Output = s_hostIdField }, cancellationToken).ConfigureAwait(false))
            return true;
        return false;
    }

    /// <summary>The ids of the selection's group, by its name or its id: empty when there is none.</summary>
    internal async Task<IReadOnlyList<ulong>> GroupIdsAsync(CancellationToken cancellationToken)
    {
        List<ulong> ids = [];
        foreach (ZabbixHostGroup found in await Client.HostGroups.GetAsync(Selection.ToGroupGet() with { Output = s_groupIdField }, cancellationToken).ConfigureAwait(false))
        {
            if (found.GroupId is { } id) ids.Add(id);
        }
        return ids;
    }

    /// <inheritdoc/>
    /// <exception cref="ZabbixNotFoundException">This node's host, group or item does not exist.</exception>
    public virtual async IAsyncEnumerable<ZabbixValue> GetLatestAsync([EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        var keys = new ZabbixItemKeyReport(Logger);
        await foreach (ZabbixItem item in GetItemsAsync(cancellationToken).ConfigureAwait(false))
            if (ToValue(item, keys) is { } value) yield return value;
    }

    /// <inheritdoc/>
    /// <remarks>
    /// One <c>history.get</c> cursor per value type the items have, merged into one time line in clock order; each cursor
    /// reads a saturated second whole, so nothing is truncated. Rows outside <c>[from, to]</c> to the nanosecond are
    /// left out (the API's bounds are whole seconds).
    /// </remarks>
    /// <exception cref="ZabbixNotFoundException">This node's host, group or item does not exist.</exception>
    [System.Diagnostics.CodeAnalysis.SuppressMessage("Naming", "CA1716:Identifiers should not match keywords",
        Justification = "The window is from and to, as the history.get parameters' TimeFrom and TimeTo are; a Visual Basic caller writes [to].")]
    public virtual async IAsyncEnumerable<ZabbixValue> GetHistoryAsync(DateTimeOffset from, DateTimeOffset? to = null, [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        var keys = new ZabbixItemKeyReport(Logger);
        Dictionary<ulong, (string Host, ZabbixItemKey Key, ZabbixItemValueType Type)> items = [];
        await foreach (ZabbixItem item in GetItemsAsync(cancellationToken).ConfigureAwait(false))
            if (item is { ItemId: { } id, ValueType: { } type, HostName: { } host } && keys.TryRead(item, out ZabbixItemKey key)) items[id] = (host, key, type);

        ZabbixClock start = from;
        ZabbixClock? end = to is { } t ? t : null;
        await foreach (ZabbixValue value in ReadHistoryAsync(items, start.Seconds, end?.Seconds, cancellationToken).ConfigureAwait(false))
            if (value.Clock >= start && (end is not { } last || value.Clock <= last)) yield return value;
    }

    /// <summary>The history of <paramref name="items"/> from second <paramref name="from"/>, one cursor per value type, merged in clock order.</summary>
    internal IAsyncEnumerable<ZabbixValue> ReadHistoryAsync(IReadOnlyDictionary<ulong, (string Host, ZabbixItemKey Key, ZabbixItemValueType Type)> items, long from, long? to, CancellationToken cancellationToken)
    {
        ILogger logger = Logger;
        List<IAsyncEnumerable<ZabbixValue>> cursors = [.. items
            .GroupBy(item => item.Value.Type)
            .OrderBy(group => group.Key)
            .Select(group => ZabbixHistoryRead.ReadAsync(Client, Client.GetApiVersionAsync, Client.TreeTelemetry, logger, group.Key, [.. group.Select(item => item.Key)], from, to, cancellationToken)
                .Select(row => ToValue(items, row)))];
        return ZabbixHistoryRead.MergeAsync(cursors, static value => value.Clock, cancellationToken);

        // A row for an item that was not asked about, or with no value, is not history.get's answer to this read: the
        // reply is the server's fault (invalid_response), never a KeyNotFoundException or an empty value read as data.
        static ZabbixValue ToValue(IReadOnlyDictionary<ulong, (string Host, ZabbixItemKey Key, ZabbixItemValueType Type)> items, (ZabbixHistoryRecord Record, ZabbixClock Clock) row)
            => row.Record.ItemId is { } id && items.TryGetValue(id, out (string Host, ZabbixItemKey Key, ZabbixItemValueType Type) item)
                ? new(item.Host, item.Key, row.Record.Value ?? throw new InvalidDataException($"The history.get reply carries a row of item {id} without a value."), row.Clock)
                : throw new InvalidDataException($"The history.get reply carries a row of item {row.Record.ItemId?.ToString(CultureInfo.InvariantCulture) ?? "(none)"}, which was not asked for.");
    }

    /// <inheritdoc/>
    public virtual IZabbixWatcher Watch(ZabbixWatchPlan plan)
    {
        ArgumentNullException.ThrowIfNull(plan);
        return new ZabbixWatcher(this, plan, Client.Loggers.CreateLogger<ZabbixWatcher>());
    }

    internal static ZabbixValue? ToValue(ZabbixItem item, ZabbixItemKeyReport keys)
        => item is { LastClockExact: { Seconds: > 0 } collected, LastValue: { } value, HostName: { } host } && keys.TryRead(item, out ZabbixItemKey key)
            ? new ZabbixValue(host, key, value, collected)
            : null;
}
