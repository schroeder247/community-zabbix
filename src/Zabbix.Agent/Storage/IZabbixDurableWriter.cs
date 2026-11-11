// This Source Code Form is subject to the terms of the Mozilla Public
// License, v. 2.0. Copyright (c) 2026 schroeder247. SPDX-License-Identifier: MPL-2.0
namespace Community.Zabbix.Storage;

/// <summary>
/// A writer that can say when a record is as safe as it makes it: a store, or a writer in front of one. A receiver that
/// answers its peer per value (the trapper) writes through this, so "processed" means "committed" on a durable store.
/// </summary>
/// <typeparam name="T">The record.</typeparam>
internal interface IZabbixDurableWriter<in T>
{
    /// <summary>Writes without waiting for room; answers once the record is kept (committed, on a durable store), or <see langword="false"/> when it was not kept.</summary>
    ValueTask<bool> TryWriteDurablyAsync(T item, CancellationToken cancellationToken);
}
