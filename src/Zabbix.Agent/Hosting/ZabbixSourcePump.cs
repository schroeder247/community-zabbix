// This Source Code Form is subject to the terms of the Mozilla Public
// License, v. 2.0. Copyright (c) 2026 schroeder247. SPDX-License-Identifier: MPL-2.0
using System.Threading.Channels;
using Community.Zabbix.Storage;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace Community.Zabbix.Hosting;

/// <summary>
/// Merges an agent's sources into its store. Each source is an async stream (a <see cref="ZabbixPoll"/>, a trap,
/// anything that yields records); the agent pumps every one into the one store, and when every source ends it
/// completes the store so the send loop draining it (<see cref="ZabbixSendLoop"/>) delivers what is left and stops.
/// One source faulting never stops the others. It is a <see cref="BackgroundService"/>, so the host is its lifetime; without
/// a host, call <see cref="BackgroundService.StartAsync"/> and <see cref="BackgroundService.StopAsync"/> on it by hand. An
/// agent does not preprocess: in Zabbix that is the server's or the proxy's.
/// </summary>
/// <typeparam name="T">The record.</typeparam>
internal sealed class ZabbixSourcePump<T> : BackgroundService
{
    private readonly ChannelWriter<T> _storage;
    private readonly IReadOnlyList<IAsyncEnumerable<T>> _sources;
    private readonly ILogger _logger;

    /// <summary>An agent over <paramref name="storage"/>, fed by <paramref name="sources"/>.</summary>
    /// <param name="storage">The writer the sources feed: a store, or several behind one writer (every destination gets every value).</param>
    /// <param name="sources">What produces records.</param>
    /// <param name="logger">Where to log; silent when <see langword="null"/>.</param>
    public ZabbixSourcePump(ChannelWriter<T> storage, IEnumerable<IAsyncEnumerable<T>> sources, ILogger<ZabbixSourcePump<T>>? logger = null)
    {
        ArgumentNullException.ThrowIfNull(storage);
        ArgumentNullException.ThrowIfNull(sources);
        _storage = storage;
        _sources = [.. sources];
        _logger = logger ?? NullLogger<ZabbixSourcePump<T>>.Instance;
    }

    /// <summary>The writer the sources feed.</summary>
    public ChannelWriter<T> Storage => _storage;

    /// <summary>The sources.</summary>
    public IReadOnlyList<IAsyncEnumerable<T>> Sources => _sources;

    /// <summary>Pump every source into the store until each ends or <paramref name="cancellationToken"/> stops it, then complete the store.</summary>
    /// <param name="cancellationToken">A cancellation token.</param>
    public Task RunAsync(CancellationToken cancellationToken = default) => RunAsync(null, null, cancellationToken, cancellationToken);

    /// <summary>
    /// The two-phase form a host stops with. <paramref name="stopping"/> ends the sources that produce for as long as they
    /// are read (a poll); a source that ends when its producer stops (a trapper's edge) is not stopped by it but read to its end, because what
    /// it holds was already acknowledged to someone (a trapper's senders) and it ends by itself when its producer stops.
    /// <paramref name="abandoning"/> bounds both that read and every write, so the value in hand is written, not dropped,
    /// unless the host gives up; what it then leaves behind is counted (event 3008) and told to <paramref name="abandoned"/>.
    /// A source that faults is told to <paramref name="faulted"/>.
    /// </summary>
    internal async Task RunAsync(Action<Exception>? faulted, Action<int>? abandoned, CancellationToken stopping, CancellationToken abandoning)
    {
        AgentLog.AgentStarted(_logger, _sources.Count);
        try
        {
            await Task.WhenAll(_sources.Select(source => PumpAsync(source, faulted, abandoned, source is ZabbixCompletingSource<T> ? abandoning : stopping, abandoning))).ConfigureAwait(false);
        }
        finally
        {
            AgentLog.AgentSourcesDrained(_logger);
            _storage.Complete();
        }
    }

    /// <inheritdoc/>
    protected override Task ExecuteAsync(CancellationToken stoppingToken) => RunAsync(stoppingToken);

    // A store is offered a record without waiting first: a durable store commits everything taken while a commit is under
    // way in the next one, where a write that waits for its own commit would cost one commit per record. Only a store full
    // under Wait is written with a wait; any other writer (several destinations' fan-out, a caller's channel) is written
    // with its own WriteAsync, which offers first itself.
    private async Task PumpAsync(IAsyncEnumerable<T> source, Action<Exception>? faulted, Action<int>? abandoned, CancellationToken reading, CancellationToken writing)
    {
        bool holding = false;
        var taking = _storage as IZabbixTakingWriter<T>;
        try
        {
            await foreach (T record in source.WithCancellation(reading).ConfigureAwait(false))
            {
                holding = true;
                writing.ThrowIfCancellationRequested();                                     // a source that ignores its token still stops here
                if (taking is null || !taking.TryTake(record)) await _storage.WriteAsync(record, writing).ConfigureAwait(false);
                holding = false;
            }
        }
        catch (OperationCanceledException) when (reading.IsCancellationRequested || writing.IsCancellationRequested)
        {
            // Stopping. What the drain window closed on never reaches the store: the value in hand, and what a source that
            // ends by completion still held (each of those was reported processed to its sender).
            int left = (holding ? 1 : 0) + (writing.IsCancellationRequested && source is ZabbixCompletingSource<T> completing ? completing.Discard() : 0);
            if (left > 0)
            {
                AgentLog.AgentAbandoned(_logger, left);
                abandoned?.Invoke(left);
            }
        }
        catch (Exception failure) when (!(failure is OperationCanceledException && (reading.IsCancellationRequested || writing.IsCancellationRequested)))
        {
            AgentLog.AgentSourceFailed(_logger, failure);                                   // a source's own cancellation (a timeout) too
            faulted?.Invoke(failure);
        }
    }
}
