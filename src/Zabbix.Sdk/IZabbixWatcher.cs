// This Source Code Form is subject to the terms of the Mozilla Public
// License, v. 2.0. Copyright (c) 2026 schroeder247. SPDX-License-Identifier: MPL-2.0
namespace Community.Zabbix;

/// <summary>
/// Represents a running observation that yields every value the watched items collect, as it is read. Each enumeration is
/// a reader on its own bounded buffer, so a slow reader costs itself dropped values (counted) and never a missed poll.
/// <see cref="Current"/> is the materialised latest state. A watcher stops when it is disposed or when its client is; its
/// readers then end. A reader is registered when its enumerator is
/// created and removed only when that enumerator is disposed (<c>await foreach</c> does both): an enumerator abandoned
/// undisposed keeps its buffer — up to <see cref="ZabbixWatchPlan.MaxItems"/> values — and adds to
/// <see cref="DroppedCount"/> until the watcher is disposed; it never delays a poll.
/// </summary>
public interface IZabbixWatcher : IAsyncEnumerable<ZabbixValue>, IAsyncDisposable
{
    /// <summary>The plan being observed.</summary>
    ZabbixWatchPlan Plan { get; }

    /// <summary>The latest value of every item seen so far, by host and key — what a shell shows. A snapshot, replaced once per tick that changed it.</summary>
    IReadOnlyDictionary<(string Host, ZabbixItemKey Key), ZabbixValue> Current { get; }

    /// <summary>How many ticks read what they had to read.</summary>
    long SucceededPolls { get; }

    /// <summary>How many ticks failed (each logged and skipped; the cadence kept).</summary>
    long FailedPolls { get; }

    /// <summary>How many values readers have lost to their buffers' bounds, in total.</summary>
    long DroppedCount { get; }
}
