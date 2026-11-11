// This Source Code Form is subject to the terms of the Mozilla Public
// License, v. 2.0. Copyright (c) 2026 schroeder247. SPDX-License-Identifier: MPL-2.0
using System.Threading.Channels;

namespace Community.Zabbix.Storage;

/// <summary>
/// Represents a store's policy. What the store does at its threshold is the platform's own vocabulary,
/// <see cref="BoundedChannelFullMode"/>; which records are admitted or discarded is a selection only the caller can
/// state, so those two are predicates. Immutable: a store keeps the policy it was built with, and a variant is <c>policy with { … }</c>.
/// </summary>
/// <typeparam name="T">The record.</typeparam>
public sealed record ZabbixStorePolicy<T>
{
    /// <summary>
    /// The item cap: <see cref="DefaultMaxItems"/> unless set, <see langword="null"/> for no cap. The store's bound is its own
    /// count, never the machine's memory load: a busy neighbour must not make an agent drop its samples (the runtime's
    /// high-memory-load figure is system-wide, and a store gated on it would lose everything on a loaded host).
    /// </summary>
    public int? MaxItems { get; init; } = DefaultMaxItems;

    /// <summary>
    /// The cap a store carries unless told otherwise. One sample a second for a day is 86,400 records; 100,000 is that
    /// rounded up, about 28 hours, so an agent or proxy cut off from its server overnight loses nothing at the default
    /// cadence. A value of a few hundred bytes (host, key, value, clock) makes it some tens of megabytes held in memory,
    /// less than the runtime's own working set; upstream's proxy defaults its memory buffer to none and its database to
    /// unbounded, so a cap is the SDK's own choice and <see langword="null"/> restores upstream's.
    /// </summary>
    public const int DefaultMaxItems = 100_000;

    /// <summary>
    /// What happens at the threshold, in the channel's own words: <see cref="BoundedChannelFullMode.DropOldest"/> sheds the
    /// oldest held record (the monitoring default), <see cref="BoundedChannelFullMode.DropWrite"/> refuses the incoming one,
    /// <see cref="BoundedChannelFullMode.DropNewest"/> evicts the newest held one, and <see cref="BoundedChannelFullMode.Wait"/>
    /// drops nothing and paces the writer. A store fed by a cadence refuses <c>Wait</c>: a slow reader must never stretch
    /// the schedule.
    /// </summary>
    public BoundedChannelFullMode Overflow { get; init; } = BoundedChannelFullMode.DropOldest;

    /// <summary>The ingress gate: a record it answers <see langword="false"/> for is refused at the door (counted).</summary>
    public Func<T, bool>? Admit { get; init; }

    /// <summary>The discard rule, typically staleness: a record it answers <see langword="true"/> for is refused at the door and dropped on the way out (both counted).</summary>
    public Func<T, bool>? Discard { get; init; }

    /// <summary>
    /// How long a record may be held before it is dropped on the way out (counted <c>expired</c>), measured from when it
    /// was stored; <see langword="null"/> for no limit. Zabbix's <c>ProxyOfflineBuffer</c>: the proxy's housekeeper removes
    /// history older than it by <c>write_clock</c>, uploaded or not (<c>housekeeper_proxy.c</c>).
    /// </summary>
    public TimeSpan? MaxAge { get; init; }
}
