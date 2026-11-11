// This Source Code Form is subject to the terms of the Mozilla Public
// License, v. 2.0. Copyright (c) 2026 schroeder247. SPDX-License-Identifier: MPL-2.0
using System.Net.Sockets;
using System.Security.Authentication;
using Community.Zabbix.Hosting;
using Community.Zabbix.Storage;

namespace Community.Zabbix.Protocol;

/// <summary>Sends the values of a store or a stream through a <see cref="ZabbixWireClient"/>, batched, retried and acknowledged by the send loop.</summary>
public static class ZabbixWireClientExtensions
{
    /// <summary>Drains a stream of values into the server or proxy with the default batching, retry and clock, silently, until the stream ends.</summary>
    /// <param name="wire">The client.</param>
    /// <param name="source">The values — a store, a watcher, any stream.</param>
    /// <returns>How many values were taken off the stream.</returns>
    public static Task<long> DrainAsync(this ZabbixWireClient wire, IAsyncEnumerable<ZabbixValue> source)
        => wire.DrainAsync(source, CancellationToken.None);

    /// <summary>Drains a stream of values into the server or proxy with the default batching, retry and clock, silently.</summary>
    /// <param name="wire">The client.</param>
    /// <param name="source">The values — a store, a watcher, any stream.</param>
    /// <param name="cancellationToken">Stops reading; the batch in flight gets the drain window.</param>
    /// <returns>How many values were taken off the stream.</returns>
    public static Task<long> DrainAsync(this ZabbixWireClient wire, IAsyncEnumerable<ZabbixValue> source, CancellationToken cancellationToken)
        => wire.DrainAsync(source, new ZabbixSendLoopOptions(), cancellationToken);

    /// <summary>Drains a store into the server or proxy through its acknowledged face with the default batching, retry and clock, silently, until the store is complete and empty.</summary>
    /// <param name="wire">The client.</param>
    /// <param name="store">The store.</param>
    /// <returns>How many values reached a final answer.</returns>
    public static Task<long> DrainAsync(this ZabbixWireClient wire, ZabbixStore<ZabbixValue> store)
        => wire.DrainAsync(store, CancellationToken.None);

    /// <summary>Drains a store into the server or proxy through its acknowledged face with the default batching, retry and clock, silently.</summary>
    /// <param name="wire">The client.</param>
    /// <param name="store">The store.</param>
    /// <param name="cancellationToken">Stops leasing; the batch in flight gets the drain window.</param>
    /// <returns>How many values reached a final answer.</returns>
    public static Task<long> DrainAsync(this ZabbixWireClient wire, ZabbixStore<ZabbixValue> store, CancellationToken cancellationToken)
        => wire.DrainAsync(store, new ZabbixSendLoopOptions(), cancellationToken);

    /// <summary>Drains a stream of values into the server or proxy, as <see cref="DrainAsync(ZabbixWireClient, IAsyncEnumerable{ZabbixValue}, ZabbixSendLoopOptions, CancellationToken)"/> does, until the stream ends.</summary>
    /// <param name="wire">The client.</param>
    /// <param name="source">The values — a store, a watcher, any stream.</param>
    /// <param name="options">Batching and retry, the clock, the logger and the progress the drain reports to.</param>
    /// <returns>How many values were taken off the stream.</returns>
    public static Task<long> DrainAsync(this ZabbixWireClient wire, IAsyncEnumerable<ZabbixValue> source, ZabbixSendLoopOptions options)
        => wire.DrainAsync(source, options, CancellationToken.None);

    /// <summary>Drains a store into the server or proxy, as <see cref="DrainAsync(ZabbixWireClient, ZabbixStore{ZabbixValue}, ZabbixSendLoopOptions, CancellationToken)"/> does, until the store is complete and empty.</summary>
    /// <param name="wire">The client.</param>
    /// <param name="store">The store.</param>
    /// <param name="options">Batching and retry, the clock, the logger and the progress the drain reports to.</param>
    /// <returns>How many values reached a final answer.</returns>
    public static Task<long> DrainAsync(this ZabbixWireClient wire, ZabbixStore<ZabbixValue> store, ZabbixSendLoopOptions options)
        => wire.DrainAsync(store, options, CancellationToken.None);

    /// <summary>
    /// Drains a stream of values into the server or proxy: batched by size or cadence, each batch retried until acknowledged,
    /// one at a time in order. Runs until the stream ends (a completed, emptied store) or the token stops it.
    /// </summary>
    /// <param name="wire">The client.</param>
    /// <param name="source">The values — a store, a watcher, any stream.</param>
    /// <param name="options">Batching and retry, the clock, the logger and the progress the drain reports to.</param>
    /// <param name="cancellationToken">Stops reading; the batch in flight gets the drain window.</param>
    /// <returns>How many values were taken off the stream.</returns>
    public static Task<long> DrainAsync(this ZabbixWireClient wire, IAsyncEnumerable<ZabbixValue> source, ZabbixSendLoopOptions options, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(wire);
        return ZabbixSendLoop.RunAsync(source, wire.DeliverAsync, options, cancellationToken);
    }

    /// <summary>
    /// Drains a store into the server or proxy through its acknowledged face: each batch is leased, delivered and acknowledged once
    /// the server answered, so a durable store keeps a batch in flight until the server took it and a restart loses
    /// nothing. Runs until the store is complete and empty, or the token stops it.
    /// </summary>
    /// <param name="wire">The client.</param>
    /// <param name="store">The store.</param>
    /// <param name="options">Batching and retry, the clock, the logger and the progress the drain reports to.</param>
    /// <param name="cancellationToken">Stops leasing; the batch in flight gets the drain window, and is released back to the store if it is not delivered in it.</param>
    /// <returns>How many values reached a final answer.</returns>
    public static Task<long> DrainAsync(this ZabbixWireClient wire, ZabbixStore<ZabbixValue> store, ZabbixSendLoopOptions options, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(wire);
        return ZabbixSendLoop.RunAsync(store, wire.DeliverAsync, options, cancellationToken);
    }

    /// <summary>
    /// The client as a destination for a send loop: a counted success is <see cref="ZabbixDelivery.Delivered"/> (a value
    /// the server refused inside it is a permanent, per-value fact it reported); a refusal is
    /// <see cref="ZabbixDelivery.Rejected"/>, since sending the same frame again gets the same answer, except a server
    /// that paused data collection (<see cref="ZabbixTrapperRefusedException.IsPaused"/>: it took nothing), which is a retry; an
    /// exchange that failed (a redirect that could not be followed among them) is <see cref="ZabbixDelivery.Retried"/>, so
    /// the batch is kept — nothing is ever delivered by mistake. The batch goes as one frame whatever
    /// <see cref="ZabbixWireOptions.BatchSize"/> says (the loop's <see cref="ZabbixSendOptions.BatchSize"/> sized it), so one
    /// answer covers all of it and a retry never sends again what an earlier frame delivered.
    /// </summary>
    /// <param name="wire">The client.</param>
    /// <param name="batch">The batch.</param>
    /// <param name="cancellationToken">Stops the exchange: the batch is then neither delivered nor rejected.</param>
    /// <returns>What became of the batch.</returns>
    public static ValueTask<ZabbixDelivery> DeliverAsync(this ZabbixWireClient wire, IReadOnlyList<ZabbixValue> batch, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(wire);
        ArgumentNullException.ThrowIfNull(batch);
        return wire.Sender.DeliverAsync(batch, cancellationToken);
    }

    // The send loop in a push agent, which holds what sends rather than a client.
    internal static async ValueTask<ZabbixDelivery> DeliverAsync(this ZabbixSender sender, IReadOnlyList<ZabbixValue> batch, CancellationToken cancellationToken)
    {
        try
        {
            await sender.SendFrameAsync(batch, cancellationToken).ConfigureAwait(false);   // the loop's batch is one frame
            return ZabbixDelivery.Delivered;
        }
        catch (ZabbixTrapperRefusedException paused) when (paused.IsPaused)
        {
            return ZabbixDelivery.Retried;                                                    // nothing was taken: held, sent again
        }
        catch (ZabbixTrapperRefusedException)
        {
            return ZabbixDelivery.Rejected;                                                 // the sender logged it (1007)
        }
        // Spelled out (not ZabbixFrames.IsExchangeFailure(failure)) so the filter is a type test that analyzer CA1031 (no catch-all)
        // reads as narrowing, not a method call it cannot see through; keep this set equal to IsExchangeFailure's (a test pins it).
        catch (Exception failure) when (failure is SocketException or IOException or AuthenticationException or InvalidDataException or TimeoutException)
        {
            return ZabbixDelivery.Retried;                                                    // the sender logged it (1003, 1008)
        }
    }
}
