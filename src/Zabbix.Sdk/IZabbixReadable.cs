// This Source Code Form is subject to the terms of the Mozilla Public
// License, v. 2.0. Copyright (c) 2026 schroeder247. SPDX-License-Identifier: MPL-2.0
namespace Community.Zabbix;

/// <summary>
/// Reads the latest values and history of the items beneath a node of the pull plane (the client, a group, a host, an
/// item). The depth you call at is the scope: the node compiles itself to the <c>item.get</c> filter, so there is no
/// targeting parameter anywhere. Every read is a stream: history is paged and yields
/// page by page, so a long history never sits in memory whole. The latest values are one <c>item.get</c> reply (the API has
/// no cursor for <c>item.get</c>: no offset and no range filter on <c>itemid</c>), read whole and then yielded — so the depth
/// you call at is also the size of that reply. The streams are cold: each enumeration reads the server again (two
/// <c>await foreach</c> over one <see cref="GetHistoryAsync"/> sequence are two full reads), so materialise a result to share it.
/// </summary>
public interface IZabbixReadable
{
    /// <summary>The latest value of every item beneath this node (<c>item.get</c> with <c>lastvalue</c>); an item that has never collected yields nothing.</summary>
    /// <param name="cancellationToken">A cancellation token, which reaches the socket.</param>
    /// <returns>Each collected item's last value, stamped with its host.</returns>
    IAsyncEnumerable<ZabbixValue> GetLatestAsync(CancellationToken cancellationToken = default);

    /// <summary>
    /// The history of every item beneath this node between two instants, in clock order across every item and value type
    /// (<c>history.get</c>, paged; a second that fills a page is read whole, so nothing is truncated).
    /// </summary>
    /// <param name="from">The start, inclusive.</param>
    /// <param name="till">The end, inclusive; now when <see langword="null"/>.</param>
    /// <param name="cancellationToken">A cancellation token, which reaches the socket.</param>
    /// <returns>The values collected in the window, in clock order across every item beneath this node.</returns>
    IAsyncEnumerable<ZabbixValue> GetHistoryAsync(DateTimeOffset from, DateTimeOffset? till = null, CancellationToken cancellationToken = default);
}
