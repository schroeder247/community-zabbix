// This Source Code Form is subject to the terms of the Mozilla Public
// License, v. 2.0. Copyright (c) 2026 schroeder247. SPDX-License-Identifier: MPL-2.0
using System.Diagnostics.Metrics;
using System.Threading.Channels;
using Community.Zabbix.Storage;
using Microsoft.AspNetCore.Connections;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace Community.Zabbix.Listening;

/// <summary>
/// Provides a trapper as a push agent's source: a listener whose handler is a <see cref="ZabbixTrapper"/> writing into one
/// bounded channel (the intake); the agent pumps the intake into its stores. The intake has one reader
/// (<see cref="ReadAllAsync"/> may be called once). Hosted with the listener's phases, so the port is bound after the agent
/// starts and released before it stops.
/// </summary>
/// <remarks>
/// A push agent's <c>AddTrapper</c> adds one, and the agent starts it. To take values on a listener of your own, build one
/// with <see cref="ZabbixListener.CreateBuilder"/> and answer with a <see cref="ZabbixTrapper"/> over a channel you read.
/// </remarks>
public sealed class ZabbixTrapperSource : IHostedLifecycleService, IAsyncDisposable, IDisposable
{
    private readonly Channel<ZabbixValue> _edge;
    private readonly ZabbixListener _listener;
    private int _read;

    /// <summary>
    /// A trapper source listening as <paramref name="options"/> says, on <paramref name="transport"/>: silent, on the system
    /// clock, and uncounted. Its intake holds <see cref="ZabbixStorePolicy{T}.DefaultMaxItems"/> values.
    /// </summary>
    /// <param name="options">The listener's options.</param>
    /// <param name="transport">The connection layer.</param>
    /// <exception cref="ArgumentNullException">An argument is <see langword="null"/>.</exception>
    public ZabbixTrapperSource(ZabbixListenerOptions options, IConnectionListenerFactory transport)
        : this(options, transport, null, null, null)
    {
    }

    /// <summary>
    /// A trapper source listening as <paramref name="options"/> says, on <paramref name="transport"/>, with the application's
    /// logging, clock and meters. Its intake holds <see cref="ZabbixStorePolicy{T}.DefaultMaxItems"/> values.
    /// </summary>
    /// <param name="options">The listener's options.</param>
    /// <param name="transport">The connection layer.</param>
    /// <param name="loggers">Where to log; nowhere when <see langword="null"/>. The caller keeps owning it.</param>
    /// <param name="time">The clock; the system's when <see langword="null"/>.</param>
    /// <param name="meters">Where to count; uncounted by the trapper, and on an owned meter by the listener, when <see langword="null"/>.</param>
    /// <exception cref="ArgumentNullException"><paramref name="options"/> or <paramref name="transport"/> is <see langword="null"/>.</exception>
    public ZabbixTrapperSource(ZabbixListenerOptions options, IConnectionListenerFactory transport, ILoggerFactory? loggers, TimeProvider? time, IMeterFactory? meters)
        : this(options, ZabbixStorePolicy<ZabbixValue>.DefaultMaxItems, transport, loggers, time, meters)
    {
    }

    /// <summary>A push agent's trapper: its intake holds as many values as each of the agent's stores, <paramref name="maxItems"/>.</summary>
    /// <param name="options">The listener's options.</param>
    /// <param name="maxItems">How many values the intake holds; beyond it a sender is told "failed" for the values that don't fit.</param>
    /// <param name="transport">The connection layer.</param>
    /// <param name="loggers">Where to log.</param>
    /// <param name="time">The clock.</param>
    /// <param name="meters">Where to count.</param>
    internal ZabbixTrapperSource(ZabbixListenerOptions options, int maxItems, IConnectionListenerFactory transport, ILoggerFactory? loggers, TimeProvider? time, IMeterFactory? meters)
    {
        ArgumentNullException.ThrowIfNull(options);
        ArgumentNullException.ThrowIfNull(transport);
        ArgumentOutOfRangeException.ThrowIfLessThan(maxItems, 1);
        // Wait, not a drop mode: the trapper offers without waiting, and a full intake answers false, which the trapper
        // counts as failed in its answer. A drop mode's TryWrite answers true for the value it drops, and the sender would be
        // told a lost value was processed.
        _edge = Channel.CreateBounded<ZabbixValue>(new BoundedChannelOptions(maxItems) { FullMode = BoundedChannelFullMode.Wait, SingleReader = true });
        Trapper = new ZabbixTrapper(_edge.Writer, loggers, time, meters);
        _listener = new ZabbixListener(options, Trapper.Handler, transport, time, loggers?.CreateLogger<ZabbixListener>(), meters);
    }

    /// <summary>The trapper behind the listener.</summary>
    public ZabbixTrapper Trapper { get; }

    /// <summary>The listener.</summary>
    public ZabbixListener Listener => _listener;

    /// <summary>
    /// The values as they arrive: the agent's source. The edge is a single-reader channel, so this may be called once;
    /// a second call throws <see cref="InvalidOperationException"/> rather than split the values between two readers.
    /// </summary>
    /// <param name="cancellationToken">Stops reading.</param>
    public IAsyncEnumerable<ZabbixValue> ReadAllAsync(CancellationToken cancellationToken = default)
        => Interlocked.Exchange(ref _read, 1) == 0
            ? _edge.Reader.ReadAllAsync(cancellationToken)
            : throw new InvalidOperationException("A trapper source's edge has one reader, and ReadAllAsync was already called.");

    /// <summary>
    /// The edge itself, for the agent's registration: a source that ends when <see cref="StopAsync"/> completes it, which
    /// the agent reads to its end at stop, because every value in it was reported processed to its sender. Taken once,
    /// with <see cref="ReadAllAsync"/>.
    /// </summary>
    internal ChannelReader<ZabbixValue> TakeEdge()
        => Interlocked.Exchange(ref _read, 1) == 0
            ? _edge.Reader
            : throw new InvalidOperationException("A trapper source's edge has one reader, and ReadAllAsync was already called.");

    /// <inheritdoc/>
    public Task StartingAsync(CancellationToken cancellationToken = default) => _listener.StartingAsync(cancellationToken);

    /// <inheritdoc/>
    public Task StartAsync(CancellationToken cancellationToken = default) => _listener.StartAsync(cancellationToken);

    /// <inheritdoc/>
    public Task StartedAsync(CancellationToken cancellationToken = default) => _listener.StartedAsync(cancellationToken);

    /// <inheritdoc/>
    public Task StoppingAsync(CancellationToken cancellationToken = default) => _listener.StoppingAsync(cancellationToken);

    /// <inheritdoc/>
    public async Task StopAsync(CancellationToken cancellationToken = default)
    {
        await _listener.StopAsync(cancellationToken).ConfigureAwait(false);
        _ = _edge.Writer.TryComplete();                                               // nothing more arrives: the agent's source ends (false: already ended)
    }

    /// <inheritdoc/>
    public Task StoppedAsync(CancellationToken cancellationToken = default) => _listener.StoppedAsync(cancellationToken);

    /// <inheritdoc/>
    public async ValueTask DisposeAsync()
    {
        _ = _edge.Writer.TryComplete();
        await _listener.DisposeAsync().ConfigureAwait(false);
    }

    /// <summary>The synchronous form, for a container disposed synchronously; the port is released (see <see cref="ZabbixListener.Dispose"/>).</summary>
    public void Dispose()
    {
        _ = _edge.Writer.TryComplete();
        _listener.Dispose();
    }
}
