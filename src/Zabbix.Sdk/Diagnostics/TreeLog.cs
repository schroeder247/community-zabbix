// This Source Code Form is subject to the terms of the Mozilla Public
// License, v. 2.0. Copyright (c) 2026 schroeder247. SPDX-License-Identifier: MPL-2.0
using Microsoft.Extensions.Logging;

namespace Community.Zabbix.Diagnostics;

/// <summary>
/// Provides the pull plane's log events. Ids:
/// <list type="bullet">
/// <item><c>50xx</c> cadences and reads: <c>5002</c>–<c>5004</c> a watcher's life, <c>5005</c>–<c>5009</c> what a read had to
/// leave out or read again. <c>5000</c>, <c>5001</c>, <c>5010</c> and <c>5011</c> are unassigned.</item>
/// </list>
/// </summary>
internal static partial class TreeLog
{
    /// <summary>A watcher started its cadence.</summary>
    [LoggerMessage(EventId = 5002, Level = LogLevel.Debug, Message = "Watcher on the {Scope} started (interval {IntervalSeconds} s)")]
    public static partial void WatcherStarted(ILogger logger, string scope, double intervalSeconds);

    /// <summary>One watcher tick failed; it is skipped, counted, and the cadence kept.</summary>
    [LoggerMessage(EventId = 5003, Level = LogLevel.Error, Message = "Watcher tick on the {Scope} failed ({ErrorType}); skipping this tick")]
    public static partial void WatcherTickFailed(ILogger logger, string scope, string errorType, Exception exception);

    /// <summary>A watcher stopped: it was disposed, or its client was.</summary>
    [LoggerMessage(EventId = 5004, Level = LogLevel.Information, Message = "Watcher on the {Scope} stopped: {Reason}")]
    public static partial void WatcherStopped(ILogger logger, string scope, string reason);

    /// <summary>An item's key is not one this SDK can read; the item is left out, and the others are answered.</summary>
    [LoggerMessage(EventId = 5005, Level = LogLevel.Warning, Message = "Item {ItemId} on {Host} has a key that is not an item key, {Key}; the item is left out")]
    public static partial void ItemKeyRefused(ILogger logger, string itemId, string host, [ZabbixItemKeyData] string key);

    /// <summary>A page saturated inside one second, and that second was read whole before the cursor moved on.</summary>
    [LoggerMessage(EventId = 5006, Level = LogLevel.Debug, Message = "history.get page of value type {ValueType} saturated inside second {Second}; read it whole: {Count} value(s)")]
    public static partial void HistorySecondRead(ILogger logger, int valueType, long second, int count);

    /// <summary>A second read whole held fewer values than the saturated page already showed: history changed under the read.</summary>
    [LoggerMessage(EventId = 5007, Level = LogLevel.Warning, Message = "history.get gave {Count} value(s) for second {Second} of value type {ValueType}, fewer than the {Seen} a page already held; that second may be incomplete")]
    public static partial void HistorySecondShrank(ILogger logger, int valueType, long second, int count, int seen);

    /// <summary>Rows came back without a readable item id, clock or ns; they are left out.</summary>
    [LoggerMessage(EventId = 5008, Level = LogLevel.Warning, Message = "history.get of value type {ValueType} returned {Count} row(s) without a readable itemid, clock or ns; they are left out")]
    public static partial void HistoryRowsUnreadable(ILogger logger, int valueType, int count);

    /// <summary>A watcher's readers are losing values to their bounds: logged at the 1st, 10th, 100th, … drop.</summary>
    [LoggerMessage(EventId = 5009, Level = LogLevel.Warning, Message = "Watcher on the {Scope} has dropped {Dropped} value(s): a reader is slower than the cadence")]
    public static partial void WatcherDropping(ILogger logger, string scope, long dropped);
}
