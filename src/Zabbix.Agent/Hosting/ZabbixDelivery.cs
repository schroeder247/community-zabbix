// This Source Code Form is subject to the terms of the Mozilla Public
// License, v. 2.0. Copyright (c) 2026 schroeder247. SPDX-License-Identifier: MPL-2.0
namespace Community.Zabbix.Hosting;

/// <summary>What a destination said about a batch: the contract a send loop uses to acknowledge, retry, hold or drop.</summary>
/// <remarks>
/// A verdict on one attempt, so it has the first four members of <see cref="ZabbixSendOutcome"/>, under the same names and
/// values; the loop reports the verdict as that outcome, and adds the two only a stopping loop has (<see
/// cref="ZabbixSendOutcome.Returned"/>, <see cref="ZabbixSendOutcome.Abandoned"/>).
/// </remarks>
public enum ZabbixDelivery
{
    /// <summary>The destination took it: the batch is acknowledged and leaves the buffer.</summary>
    Delivered = ZabbixSendOutcome.Delivered,

    /// <summary>The destination refused it for good: the batch is dropped, because sending it again cannot help.</summary>
    Rejected = ZabbixSendOutcome.Rejected,

    /// <summary>A transient failure (unreachable, timed out): the batch is kept and sent again after a backoff.</summary>
    Retried = ZabbixSendOutcome.Retried,

    /// <summary>
    /// The destination asked the sender to hold its data for now (Zabbix's <c>"upload":"disabled"</c>, a server whose
    /// history cache is full): the batch is kept and offered again after the backoff. It is back-pressure, not a failure,
    /// so it is not logged as one.
    /// </summary>
    Held = ZabbixSendOutcome.Held,
}
