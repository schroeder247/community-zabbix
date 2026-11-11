// This Source Code Form is subject to the terms of the Mozilla Public
// License, v. 2.0. Copyright (c) 2026 schroeder247. SPDX-License-Identifier: MPL-2.0
namespace Community.Zabbix.Storage;

/// <summary>
/// A writer that tells a record it takes and drops by its policy (refused, discarded, shed) from one it cannot take now (full
/// under <see cref="System.Threading.Channels.BoundedChannelFullMode.Wait"/>, completed). A producer that offers without
/// waiting and waits only when the writer is full writes through this, so a refused record is not offered twice.
/// </summary>
/// <typeparam name="T">The record.</typeparam>
internal interface IZabbixTakingWriter<in T>
{
    /// <summary>Offers <paramref name="item"/> without waiting: <see langword="true"/> when it was taken (kept, or dropped and counted by the policy), <see langword="false"/> when the writer is full or completed.</summary>
    bool TryTake(T item);
}
