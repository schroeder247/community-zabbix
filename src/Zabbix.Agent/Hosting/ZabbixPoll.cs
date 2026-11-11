// This Source Code Form is subject to the terms of the Mozilla Public
// License, v. 2.0. Copyright (c) 2026 schroeder247. SPDX-License-Identifier: MPL-2.0
using System.Runtime.CompilerServices;
using System.Threading.Channels;
using Community.Zabbix.Diagnostics;
using Microsoft.Extensions.Logging;

namespace Community.Zabbix.Hosting;

/// <summary>
/// Creates sources that collect on a cadence. A poll collects on its own heartbeat — a <see cref="PeriodicTimer"/> on an
/// injected clock — into a bounded buffer its reader drains, so a slow reader never stretches the cadence (a pure pull,
/// collecting only when asked, would collapse the ticks a busy reader missed into one and silently skip cycles). The
/// buffer holds <see cref="DefaultMaxItems"/> records unless the caller names another bound, and sheds the oldest when the reader falls that far behind, counted
/// and logged at the 1st, 10th, 100th, … drop (event 3006). The iterator is hand-written because nothing in the BCL
/// makes a timer a sequence: no extension on <see cref="PeriodicTimer"/> yields one, and .NET 10's
/// <c>System.Linq.AsyncEnumerable</c> has no interval source. It collects once at once, when the first record is asked
/// for, then on every tick; a cycle that throws is logged and skipped, so a transient probe failure never ends the
/// cadence. That includes a cycle that throws <see cref="OperationCanceledException"/> on its own (an
/// <see cref="System.Net.Http.HttpClient"/> timeout): only the reader's token stops the poll. Records collected and not
/// yet read when the reader stops are counted and logged (event 3007). Enumerating it twice is two polls. Disposing the
/// enumerator stops the heartbeat.
/// </summary>
internal static class ZabbixPoll
{
    /// <summary>How many records a poll holds by default for a reader that has fallen behind before it sheds the oldest (a watcher reader's default).</summary>
    public const int DefaultMaxItems = 4096;

    /// <summary>Values from <paramref name="collect"/>, every <paramref name="interval"/> of <paramref name="time"/>.</summary>
    /// <typeparam name="T">The record.</typeparam>
    /// <param name="interval">How long between two cycles.</param>
    /// <param name="collect">What one cycle yields.</param>
    /// <param name="time">The clock the cadence runs on.</param>
    /// <param name="logger">Where the poll logs its failed cycles and drops.</param>
    /// <returns>The poll; enumerating it starts the heartbeat.</returns>
    /// <exception cref="ArgumentOutOfRangeException"><paramref name="interval"/> is not positive.</exception>
    public static IAsyncEnumerable<T> Every<T>(TimeSpan interval, Func<CancellationToken, ValueTask<IEnumerable<T>>> collect, TimeProvider time, ILogger logger)
        => Every(interval, collect, time, logger, DefaultMaxItems);

    /// <summary>Values from <paramref name="collect"/>, every <paramref name="interval"/>, held for a slow reader up to <paramref name="maxItems"/>.</summary>
    /// <typeparam name="T">The record.</typeparam>
    /// <param name="interval">How long between two cycles.</param>
    /// <param name="collect">What one cycle yields.</param>
    /// <param name="time">The clock the cadence runs on.</param>
    /// <param name="logger">Where the poll logs its failed cycles and drops.</param>
    /// <param name="maxItems">How many records the poll holds for a reader that has fallen behind before it sheds the oldest.</param>
    /// <returns>The poll; enumerating it starts the heartbeat.</returns>
    /// <exception cref="ArgumentOutOfRangeException"><paramref name="interval"/> is not positive, or <paramref name="maxItems"/> is below one.</exception>
    public static IAsyncEnumerable<T> Every<T>(TimeSpan interval, Func<CancellationToken, ValueTask<IEnumerable<T>>> collect, TimeProvider time, ILogger logger, int maxItems)
    {
        ArgumentOutOfRangeException.ThrowIfLessThanOrEqual(interval, TimeSpan.Zero);
        ArgumentNullException.ThrowIfNull(collect);
        ArgumentNullException.ThrowIfNull(time);
        ArgumentNullException.ThrowIfNull(logger);
        ArgumentOutOfRangeException.ThrowIfLessThan(maxItems, 1);
        return IterateAsync(interval, collect, time, logger, maxItems, default);
    }

    private static async IAsyncEnumerable<T> IterateAsync<T>(
        TimeSpan interval, Func<CancellationToken, ValueTask<IEnumerable<T>>> collect, TimeProvider time, ILogger logger, int maxItems,
        [EnumeratorCancellation] CancellationToken cancellationToken)
    {
        AgentLog.PollStarted(logger, interval.TotalSeconds);
        long dropped = 0;
        Channel<T> buffer = Channel.CreateBounded<T>(
            new BoundedChannelOptions(maxItems) { FullMode = BoundedChannelFullMode.DropOldest, SingleReader = true, SingleWriter = true },
            _ =>
            {
                long count = Interlocked.Increment(ref dropped);
                if (ZabbixLogSampling.IsPowerOfTen(count)) AgentLog.PollDropping(logger, count);
            });
        using var stopping = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        Task heartbeat = HeartbeatAsync(interval, collect, time, logger, buffer.Writer, stopping.Token);
        try
        {
            await foreach (T record in buffer.Reader.ReadAllAsync(cancellationToken).ConfigureAwait(false))
                yield return record;
        }
        finally
        {
            await stopping.CancelAsync().ConfigureAwait(false);
            await heartbeat.ConfigureAwait(false);                                         // it ends on the cancellation
            int unread = 0;
            while (buffer.Reader.TryRead(out _)) unread++;
            if (unread > 0) AgentLog.PollAbandoned(logger, unread);
        }
    }

    // The heartbeat: collect at once, then on every tick, whatever the reader is doing. Only the reader's stop ends it, and
    // its end completes the buffer.
    private static async Task HeartbeatAsync<T>(
        TimeSpan interval, Func<CancellationToken, ValueTask<IEnumerable<T>>> collect, TimeProvider time, ILogger logger,
        ChannelWriter<T> buffer, CancellationToken stopping)
    {
        try
        {
            using var timer = new PeriodicTimer(interval, time);
            do
            {
                try
                {
                    foreach (T record in await collect(stopping).ConfigureAwait(false))
                        _ = buffer.TryWrite(record);                                       // DropOldest: taken, the oldest shed and counted; false only once completed
                }
                catch (Exception failure) when (!(failure is OperationCanceledException && stopping.IsCancellationRequested))
                {
                    AgentLog.PollCycleFailed(logger, failure);                               // skip the tick, keep the cadence (a cycle's own timeout too)
                }
            }
            while (await timer.WaitForNextTickAsync(stopping).ConfigureAwait(false));
        }
        catch (OperationCanceledException) when (stopping.IsCancellationRequested)
        {
            // stopped: the reader is gone
        }
        finally
        {
            _ = buffer.TryComplete();                                                      // the heartbeat is its only writer
        }
    }
}
