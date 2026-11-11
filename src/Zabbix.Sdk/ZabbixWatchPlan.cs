// This Source Code Form is subject to the terms of the Mozilla Public
// License, v. 2.0. Copyright (c) 2026 schroeder247. SPDX-License-Identifier: MPL-2.0
namespace Community.Zabbix;

/// <summary>
/// What a watcher does: how often it reads, how often it re-reads the item set, and how each reader is buffered. A closed
/// policy record — the buffer strategy is the BCL's own vocabulary
/// (<see cref="System.Threading.Channels.BoundedChannelFullMode"/>), and <c>Wait</c> is refused because a reader must never
/// pace the poll.
/// </summary>
/// <param name="Interval">How often to read what the items collected (every value since the last read, whatever each item's own <c>delay</c>).</param>
public sealed record ZabbixWatchPlan(TimeSpan Interval)
{
    /// <summary>How many values each reader holds before its buffer sheds: the most a reader may fall behind.</summary>
    public int MaxItems { get; init; } = 4096;

    /// <summary>What a full reader buffer does; the oldest is shed by default.</summary>
    public System.Threading.Channels.BoundedChannelFullMode WhenFull { get; init; } = System.Threading.Channels.BoundedChannelFullMode.DropOldest;

    /// <summary>
    /// How often the watched item set itself is re-read (<c>item.get</c>), to pick up items created since; between
    /// re-reads each tick asks only for the history of the items it knows. One minute by default.
    /// </summary>
    public TimeSpan RefreshInterval { get; init; } = TimeSpan.FromMinutes(1);

    /// <summary>
    /// How late a value may reach the server's history, after a newer value of the same node was read, and still be
    /// yielded: each tick reads from the newest clock it has seen minus this. History is read by clock and the API has no
    /// insertion cursor, so a value that arrives later than this (a proxy delivering a backlog) is not seen by the
    /// watcher; <see cref="IZabbixReadable.GetHistoryAsync"/> over the window reads it. One <see cref="Interval"/> when
    /// <see langword="null"/>.
    /// </summary>
    public TimeSpan? AllowedLateness { get; init; }
}
