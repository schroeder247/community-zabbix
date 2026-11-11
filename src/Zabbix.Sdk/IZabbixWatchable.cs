// This Source Code Form is subject to the terms of the Mozilla Public
// License, v. 2.0. Copyright (c) 2026 schroeder247. SPDX-License-Identifier: MPL-2.0
namespace Community.Zabbix;

/// <summary>
/// Represents a node observed on a cadence: the watcher reads what the node's items collected since its last read,
/// on its own heartbeat, whether or not anyone is reading, and yields every value in clock order.
/// </summary>
public interface IZabbixWatchable
{
    /// <summary>Observe this node under <paramref name="plan"/>; the watcher runs from construction.</summary>
    /// <param name="plan">How often, and how a reader is buffered.</param>
    /// <returns>The running watcher.</returns>
    IZabbixWatcher Watch(ZabbixWatchPlan plan);
}
