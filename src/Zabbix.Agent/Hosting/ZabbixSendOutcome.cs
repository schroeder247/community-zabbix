// This Source Code Form is subject to the terms of the Mozilla Public
// License, v. 2.0. Copyright (c) 2026 schroeder247. SPDX-License-Identifier: MPL-2.0
namespace Community.Zabbix.Hosting;

/// <summary>What became of values a send loop took off its source.</summary>
public enum ZabbixSendOutcome
{
    /// <summary>The destination took them.</summary>
    Delivered,

    /// <summary>The destination refused them for good; they are dropped.</summary>
    Rejected,

    /// <summary>An attempt failed (unreachable, a failure reply, a fault); the loop keeps them for another attempt.</summary>
    Retried,

    /// <summary>The destination paused uploads; the loop keeps them for another attempt.</summary>
    Held,

    /// <summary>The loop stopped before it could deliver them and handed them back to its source, which keeps them.</summary>
    Returned,

    /// <summary>The loop stopped before it could deliver them and the source could not take them back: lost.</summary>
    Abandoned,
}
