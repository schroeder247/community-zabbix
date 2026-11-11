// This Source Code Form is subject to the terms of the Mozilla Public
// License, v. 2.0. Copyright (c) 2026 schroeder247. SPDX-License-Identifier: MPL-2.0
using Microsoft.Extensions.Logging;

namespace Community.Zabbix.Storage;

/// <summary>
/// The store layer's log events. Ids 2000–2099 are the backend,
/// 2100–2199 what the store did not keep.
/// </summary>
internal static partial class StoreLog
{
    /// <summary>The store's backend failed; the consumer sees the exception.</summary>
    [LoggerMessage(EventId = 2000, Level = LogLevel.Error, Message = "Store {Store}: the backend failed; the consumer sees the fault")]
    public static partial void StoragePumpFaulted(ILogger logger, string store, Exception exception);

    /// <summary>
    /// A store is not keeping records: logged at the 1st, 10th, 100th, … drop of each reason, so a steady loss is visible
    /// without a line per record. The exact count is the <c>zabbix.store.dropped</c> counter.
    /// </summary>
    [LoggerMessage(EventId = 2100, Level = LogLevel.Warning, Message = "Store {Store} has dropped {Dropped} record(s) ({Reason})")]
    public static partial void StorageDropping(ILogger logger, string store, string reason, long dropped);
}
