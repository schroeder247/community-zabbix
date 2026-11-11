// This Source Code Form is subject to the terms of the Mozilla Public
// License, v. 2.0. Copyright (c) 2026 schroeder247. SPDX-License-Identifier: MPL-2.0
namespace Community.Zabbix.Storage;

/// <summary>The values of the <c>zabbix.store.reason</c> tag: why a store did not keep a record.</summary>
public static class ZabbixStoreDropReason
{
    /// <summary>The admit gate refused it.</summary>
    public const string Refused = "refused";

    /// <summary>The discard rule matched it, at the door or on the way out.</summary>
    public const string Discarded = "discarded";

    /// <summary>It was held longer than the policy's <c>MaxAge</c> (Zabbix's <c>ProxyOfflineBuffer</c>).</summary>
    public const string Expired = "expired";

    /// <summary>Shed as the oldest held record at the threshold.</summary>
    public const string DropOldest = "drop_oldest";

    /// <summary>Evicted as the newest held record at the threshold.</summary>
    public const string DropNewest = "drop_newest";

    /// <summary>The incoming record, refused at the threshold.</summary>
    public const string DropWrite = "drop_write";

    /// <summary>A durable store accepted it (<c>TryWrite</c>) and its backend could not commit it.</summary>
    public const string Faulted = "faulted";
}
