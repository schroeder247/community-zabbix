// This Source Code Form is subject to the terms of the Mozilla Public
// License, v. 2.0. Copyright (c) 2026 schroeder247. SPDX-License-Identifier: MPL-2.0
namespace Community.Zabbix.Hosting;

/// <summary>
/// Specifies what became of values a send loop took off its source: what a destination answers for a batch
/// (<see cref="Delivered"/>, <see cref="Rejected"/>, <see cref="Retried"/> or <see cref="Held"/>), and, in the progress the loop
/// reports, what a stopping loop did with what it still held (<see cref="Returned"/> or <see cref="Abandoned"/>).
/// </summary>
public enum ZabbixSendOutcome
{
    /// <summary>The destination took them: the batch is acknowledged and leaves the buffer.</summary>
    Delivered,

    /// <summary>The destination refused them for good: the batch is dropped, because sending it again can't help.</summary>
    Rejected,

    /// <summary>An attempt failed (unreachable, timed out, a failure reply, a fault): the loop keeps them and sends them again after a backoff.</summary>
    Retried,

    /// <summary>
    /// The destination asked the sender to hold its data for now (Zabbix's <c>"upload":"disabled"</c>, a server whose history
    /// cache is full): the loop keeps them and offers them again after the backoff. It's back-pressure, not a failure, so it
    /// isn't logged as one.
    /// </summary>
    Held,

    /// <summary>The loop stopped before it could deliver them and handed them back to its source, which keeps them.</summary>
    Returned,

    /// <summary>The loop stopped before it could deliver them and the source could not take them back: lost.</summary>
    Abandoned,
}
