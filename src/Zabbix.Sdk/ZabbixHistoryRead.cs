// This Source Code Form is subject to the terms of the Mozilla Public
// License, v. 2.0. Copyright (c) 2026 schroeder247. SPDX-License-Identifier: MPL-2.0
using System.Runtime.CompilerServices;
using Community.Zabbix.Diagnostics;
using Microsoft.Extensions.Logging;

namespace Community.Zabbix;

/// <summary>
/// Reads <c>history.get</c> for the pull plane: one value type per call (the API takes one), sorted
/// <c>clock, ns</c> ascending, paged by a second cursor — and never truncated. <c>time_from</c>/<c>time_till</c> are
/// whole, inclusive seconds and the API has no cursor inside a second (<c>ns</c> filters only by equality,
/// <c>CHistory.php</c>), so when a page saturates the second it ended in is read again whole
/// (<c>time_from = time_till = that second</c>, no limit) before the cursor moves past it. The types are then merged
/// in clock order (a k-way merge over <see cref="PriorityQueue{TElement, TPriority}"/>), so a reader sees one time line
/// across value types, holding at most one page per type.
/// </summary>
internal static class ZabbixHistoryRead
{
    /// <summary>
    /// The rows a cursor asks for at once. A choice, not a protocol limit: <c>history.get</c> takes any <c>limit</c> from 1
    /// to 2^31-1 (<c>CHistory.php</c>). 5,000 keeps one numeric reply to a few hundred kilobytes of JSON; it is not a
    /// bound on what is read, because a page that saturates is followed by its last second read whole.
    /// </summary>
    internal const int PageSize = 5000;

    private static readonly ZabbixOutput s_output = ZabbixOutput.Of("itemid", "clock", "ns", "value");
    private static readonly IReadOnlyList<string> s_sort = ["clock", "ns"];
    private static readonly IReadOnlyList<System.ComponentModel.ListSortDirection> s_ascending = [System.ComponentModel.ListSortDirection.Ascending];

    /// <summary>The first version whose <c>history.get</c> cuts binary and JSON values unless told not to (<c>CHistory.php</c> 8.0:120).</summary>
    private static readonly Version s_uncutFrom = new(8, 0);

    /// <summary>
    /// The one place a <c>history.get</c> is composed. Version-gated parameters join here and nowhere else: 8.0's
    /// <c>maxValueSize</c> for binary and JSON values is set to <see cref="ZabbixOptional{T}.Null"/> by <c>PageAsync</c>.
    /// </summary>
    internal static ZabbixHistoryGetParameters Page(ZabbixItemValueType type, IReadOnlyList<ulong> itemIds, long from, long? till, int? limit) => new()
    {
        History = type,
        ItemIds = itemIds,
        Output = s_output,
        TimeFrom = DateTimeOffset.FromUnixTimeSeconds(from),
        TimeTill = till is long t ? DateTimeOffset.FromUnixTimeSeconds(t) : null,
        SortField = s_sort,
        SortOrder = s_ascending,
        Limit = limit,
    };

    /// <summary>One value type's rows with a readable clock, oldest first, from second <paramref name="from"/> to <paramref name="till"/> inclusive.</summary>
    internal static async IAsyncEnumerable<(ZabbixHistoryRecord Record, ZabbixClock Clock)> ReadAsync(
        ZabbixClient client, Func<CancellationToken, Task<Version>> apiVersion, ZabbixTreeTelemetry telemetry, ILogger logger, ZabbixItemValueType type,
        IReadOnlyList<ulong> itemIds, long from, long? till, [EnumeratorCancellation] CancellationToken cancellationToken)
    {
        bool uncut = type is ZabbixItemValueType.Binary or ZabbixItemValueType.Json
                     && await apiVersion(cancellationToken).ConfigureAwait(false) >= s_uncutFrom;
        long cursor = from;
        while (till is not { } end || cursor <= end)
        {
            (List<ZabbixHistoryRecord> page, List<(ZabbixHistoryRecord, ZabbixClock)> rows) = await PageAsync(client, telemetry, logger, type, Page(type, itemIds, cursor, till, PageSize), uncut, cancellationToken).ConfigureAwait(false);
            // A page answered after the caller stopped asking is not yielded: a read that was cancelled is not "nothing
            // more was there", even when the reply arrived (the transport may finish a request its caller gave up on).
            cancellationToken.ThrowIfCancellationRequested();
            if (page.Count < PageSize)
            {
                foreach ((ZabbixHistoryRecord, ZabbixClock) row in rows) yield return row;
                yield break;
            }
            if (rows.Count == 0) yield break;                                                 // a full page with no readable clock: logged by PageAsync

            long second = rows[^1].Item2.Seconds;
            int seen = 0;
            foreach ((ZabbixHistoryRecord, ZabbixClock) row in rows)
            {
                if (row.Item2.Seconds < second) yield return row;
                else seen++;
            }

            (_, List<(ZabbixHistoryRecord, ZabbixClock)> whole) = await PageAsync(client, telemetry, logger, type, Page(type, itemIds, second, second, limit: null), uncut, cancellationToken).ConfigureAwait(false);
            cancellationToken.ThrowIfCancellationRequested();
            TreeLog.HistorySecondRead(logger, (int)type, second, whole.Count);
            if (whole.Count < seen) TreeLog.HistorySecondShrank(logger, (int)type, second, whole.Count, seen);
            foreach ((ZabbixHistoryRecord, ZabbixClock) row in whole) yield return row;
            cursor = second + 1;
        }
    }

    private static async Task<(List<ZabbixHistoryRecord> Page, List<(ZabbixHistoryRecord, ZabbixClock)> Rows)> PageAsync(
        ZabbixClient client, ZabbixTreeTelemetry telemetry, ILogger logger, ZabbixItemValueType type, ZabbixHistoryGetParameters parameters, bool uncut,
        CancellationToken cancellationToken)
    {
        ZabbixOperation operation = telemetry.StartHistoryPage(type, parameters.Limit);
        var page = new List<ZabbixHistoryRecord>(parameters.Limit ?? 0);
        try
        {
            // 8.0 cuts a binary or JSON value at 64 KiB by default (the maxValueSize rule's default, CHistory.php); only an
            // explicit null reads it whole, which ZabbixOptional<int>.Null says (sent as "maxValueSize": null).
            IReadOnlyList<ZabbixHistoryRecord> reply = await client.History.GetAsync(uncut ? parameters with { MaxValueSize = ZabbixOptional<int>.Null } : parameters, cancellationToken).ConfigureAwait(false);
            page.AddRange(reply);
        }
        catch (Exception failure)
        {
            telemetry.Finish(operation, failure);
            throw;
        }
        telemetry.Finish(operation, extra: new System.Diagnostics.TagList { { "zabbix.tree.history.rows", page.Count } });

        var rows = new List<(ZabbixHistoryRecord, ZabbixClock)>(page.Count);
        foreach (ZabbixHistoryRecord record in page)
            if (record is { ItemId: not null, ClockExact: { } clock }) rows.Add((record, clock));
        if (rows.Count < page.Count) TreeLog.HistoryRowsUnreadable(logger, (int)type, page.Count - rows.Count);
        return (page, rows);
    }

    /// <summary>
    /// Sources that are each in clock order, merged into one clock order; ties keep the sources' order. Owned, not borrowed:
    /// <c>System.Linq.AsyncEnumerable</c> has <c>Concat</c> and <c>Zip</c> but no ordered merge, and Ix's <c>Merge</c>
    /// interleaves by arrival, not by key; <see cref="PriorityQueue{TElement, TPriority}"/> is the BCL piece that is the
    /// merge, and this is the dozen lines that drive it.
    /// </summary>
    internal static async IAsyncEnumerable<T> MergeAsync<T>(IReadOnlyList<IAsyncEnumerable<T>> sources, Func<T, ZabbixClock> clock, [EnumeratorCancellation] CancellationToken cancellationToken)
    {
        var readers = new List<IAsyncEnumerator<T>>(sources.Count);
        var queue = new PriorityQueue<int, (ZabbixClock Clock, int Source)>(sources.Count);
        try
        {
            for (int i = 0; i < sources.Count; i++)
            {
                IAsyncEnumerator<T> reader = sources[i].GetAsyncEnumerator(cancellationToken);
                readers.Add(reader);
                if (await reader.MoveNextAsync().ConfigureAwait(false)) queue.Enqueue(i, (clock(reader.Current), i));
            }
            while (queue.TryDequeue(out int source, out _))
            {
                IAsyncEnumerator<T> reader = readers[source];
                yield return reader.Current;
                if (await reader.MoveNextAsync().ConfigureAwait(false)) queue.Enqueue(source, (clock(reader.Current), source));
            }
        }
        finally
        {
            foreach (IAsyncEnumerator<T> reader in readers) await reader.DisposeAsync().ConfigureAwait(false);
        }
    }
}
