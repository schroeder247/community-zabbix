// This Source Code Form is subject to the terms of the Mozilla Public
// License, v. 2.0. Copyright (c) 2026 schroeder247. SPDX-License-Identifier: MPL-2.0
namespace Community.Zabbix.Storage;

/// <summary>
/// Specifies why a store didn't keep a record. Each reason is counted on <c>zabbix.store.dropped</c>, whose
/// <c>zabbix.store.reason</c> tag spells it in snake case: <c>refused</c>, <c>discarded</c>, <c>expired</c>,
/// <c>drop_oldest</c>, <c>drop_newest</c>, <c>drop_write</c> and <c>faulted</c>.
/// </summary>
public enum ZabbixStoreDropReason
{
    /// <summary>The admit gate refused it (<c>refused</c>).</summary>
    Refused,

    /// <summary>The discard rule matched it, at the door or on the way out (<c>discarded</c>).</summary>
    Discarded,

    /// <summary>It was held longer than the policy's <c>MaxAge</c>, Zabbix's <c>ProxyOfflineBuffer</c> (<c>expired</c>).</summary>
    Expired,

    /// <summary>Shed as the oldest held record at the threshold (<c>drop_oldest</c>).</summary>
    DropOldest,

    /// <summary>Evicted as the newest held record at the threshold (<c>drop_newest</c>).</summary>
    DropNewest,

    /// <summary>The incoming record, refused at the threshold (<c>drop_write</c>).</summary>
    DropWrite,

    /// <summary>A durable store accepted it (<c>TryWrite</c>) and its backend couldn't commit it (<c>faulted</c>).</summary>
    Faulted,
}
