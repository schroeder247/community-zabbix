// This Source Code Form is subject to the terms of the Mozilla Public
// License, v. 2.0. Copyright (c) 2026 schroeder247. SPDX-License-Identifier: MPL-2.0
using System.Buffers;
using System.Collections.Concurrent;
using System.Diagnostics.Metrics;
using System.IO.Pipelines;
using System.Net;
using System.Runtime.CompilerServices;
using System.Text.Json;
using System.Threading.RateLimiting;
using Community.Zabbix.Diagnostics;
using Community.Zabbix.Protocol;
using Microsoft.AspNetCore.Connections;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

namespace Community.Zabbix.Listening;

/// <summary>
/// Accepts Zabbix connections and serves one framed request per connection through a handler.
/// </summary>
/// <remarks>
/// <see cref="CreateBuilder"/> builds one on Kestrel's socket transport. The constructors take a transport of the caller's:
/// <see cref="IConnectionListenerFactory"/>, an in-memory one in a test. It refuses a peer outside <see cref="ZabbixListenerOptions.AllowedPeers"/>, leases a slot from a per-peer
/// partition and a global concurrency limiter, admits the connection's protection against
/// <see cref="ZabbixListenerOptions.TlsAccept"/>, then hands the first frame to the handler and writes the reply; a
/// handler fault is answered <c>failed</c>. <c>StartingAsync</c> binds, <c>StoppingAsync</c> unbinds and
/// <c>StopAsync</c> waits for the connections in flight.
/// <para>It owns its accept loop rather than using Kestrel's connection pipeline because <c>KestrelServer</c> needs a
/// web host, which every trapper, passive agent and passive proxy consumer would then have to host. It publishes the
/// equivalent instruments itself, on the <see cref="Diagnostics.ZabbixTelemetryNames.Listener"/> meter.</para>
/// </remarks>
public sealed class ZabbixListener : IHostedLifecycleService, IAsyncDisposable, IDisposable
{
    /// <summary>
    /// The service key the listeners' <see cref="IConnectionListenerFactory"/> is registered under
    /// (<see cref="ZabbixSocketTransportServiceCollectionExtensions.AddZabbixSocketTransport"/>), apart from a web
    /// application's own; an application registers another transport under this key to replace it.
    /// </summary>
    public const string TransportKey = "Zabbix";

    private readonly ZabbixListenerOptions _options;
    private readonly ZabbixConversationCallback _conversation;
    private readonly IConnectionListenerFactory _transport;
    private readonly TimeProvider _time;
    private readonly ILogger _logger;
    private readonly ZabbixListenerTelemetry _telemetry;
    private readonly ConcurrentDictionary<ConnectionContext, Task> _inFlight = new();      // each connection being handled, until its handling ends
    private readonly CancellationTokenSource _stopping = new();
    private readonly ZabbixLogOnce<(IPAddress Peer, string Rejection)> _refusals;       // 6003: each peer and code once an hour
    private readonly ZabbixLogOnce<(IPAddress Peer, Type Failure)> _failures;           // 6002: each peer and failure kind once an hour
    private readonly ZabbixLogOnce<string> _unresolved;                                 // 6006: each allowed name that did not resolve once an hour
    private Admission? _admission;                                                         // what StartingAsync built; null before it
    private int _active;
    private int _started;
    private int _disposed;
    private IConnectionListener? _listener;
    private IDisposable? _container;                                                    // the builder's, disposed with the listener
    private Task _accepting = Task.CompletedTask;

    /// <summary>
    /// Starts a builder for a listener on Kestrel's socket transport: where it binds, who may connect, and what answers a
    /// request.
    /// </summary>
    /// <returns>A builder whose <see cref="ZabbixListenerBuilder.Build"/> returns the listener.</returns>
    /// <example>
    /// <code language="csharp">
    /// await using ZabbixListener listener = ZabbixListener.CreateBuilder()
    ///     .UseSocketTransport(10051)
    ///     .AllowPeer(IPNetwork.Parse("10.0.0.0/8"))
    ///     .OnRequest(trapper.Handler)
    ///     .Build();
    /// </code>
    /// </example>
    public static ZabbixListenerBuilder CreateBuilder() => new();

    /// <summary>A listener that answers with <paramref name="handler"/>, on the system clock, silent, counting on an owned meter.</summary>
    /// <param name="options">Where and how; validated when the listener starts, not here.</param>
    /// <param name="handler">What a request gets back.</param>
    /// <param name="transport">The connection layer; Kestrel's socket transport in a host.</param>
    public ZabbixListener(ZabbixListenerOptions options, ZabbixRequestCallback handler, IConnectionListenerFactory transport)
        : this(options, handler, transport, loggers: null, time: null, meters: null)
    {
    }

    /// <summary>A listener that answers with <paramref name="handler"/>, with the application's logging, clock and meters.</summary>
    /// <param name="options">Where and how; validated when the listener starts, not here.</param>
    /// <param name="handler">What a request gets back.</param>
    /// <param name="transport">The connection layer; Kestrel's socket transport in a host.</param>
    /// <param name="loggers">Where to log; nowhere when <see langword="null"/>. The caller keeps owning it.</param>
    /// <param name="time">The clock deadlines run on; the system clock by default.</param>
    /// <param name="meters">Where to count; an owned meter when <see langword="null"/>.</param>
    public ZabbixListener(ZabbixListenerOptions options, ZabbixRequestCallback handler, IConnectionListenerFactory transport, ILoggerFactory? loggers, TimeProvider? time, IMeterFactory? meters)
        : this(options, ZabbixConversationContext.Reply(handler), transport, loggers, time, meters)
    {
    }

    /// <summary>A listener that hands each connection, after its first frame, to <paramref name="conversation"/>, on the system clock, silent, counting on an owned meter.</summary>
    /// <param name="options">Where and how.</param>
    /// <param name="conversation">What a connection is: the frames after the first, both ways.</param>
    /// <param name="transport">The connection layer; Kestrel's socket transport in a host.</param>
    public ZabbixListener(ZabbixListenerOptions options, ZabbixConversationCallback conversation, IConnectionListenerFactory transport)
        : this(options, conversation, transport, loggers: null, time: null, meters: null)
    {
    }

    /// <summary>
    /// A listener that hands each connection, after its first frame, to <paramref name="conversation"/>, with the
    /// application's logging, clock and meters.
    /// </summary>
    /// <param name="options">Where and how.</param>
    /// <param name="conversation">What a connection is: the frames after the first, both ways.</param>
    /// <param name="transport">The connection layer; Kestrel's socket transport in a host.</param>
    /// <param name="loggers">Where to log; nowhere when <see langword="null"/>. The caller keeps owning it.</param>
    /// <param name="time">The clock deadlines run on; the system clock by default.</param>
    /// <param name="meters">Where to count; an owned meter when <see langword="null"/>.</param>
    public ZabbixListener(ZabbixListenerOptions options, ZabbixConversationCallback conversation, IConnectionListenerFactory transport, ILoggerFactory? loggers, TimeProvider? time, IMeterFactory? meters)
    {
        ArgumentNullException.ThrowIfNull(options);
        ArgumentNullException.ThrowIfNull(conversation);
        ArgumentNullException.ThrowIfNull(transport);
        _options = options;
        _conversation = conversation;
        _transport = transport;
        _time = time ?? TimeProvider.System;
        _logger = (ILogger?)loggers?.CreateLogger<ZabbixListener>() ?? NullLogger<ZabbixListener>.Instance;
        _telemetry = new ZabbixListenerTelemetry(meters, _time);
        _refusals = new ZabbixLogOnce<(IPAddress, string)>(1024, TimeSpan.FromHours(1), _time);
        _failures = new ZabbixLogOnce<(IPAddress, Type)>(1024, TimeSpan.FromHours(1), _time);
        _unresolved = new ZabbixLogOnce<string>(1024, TimeSpan.FromHours(1), _time);
    }

    /// <summary>Where the listener is bound; <see langword="null"/> before <see cref="StartingAsync"/>.</summary>
    public EndPoint? LocalEndPoint => _listener?.EndPoint;

    /// <summary>Connections being handled now.</summary>
    public int InFlight => Volatile.Read(ref _active);

    /// <summary>Whether the listener is bound and its accept loop runs: started, and neither stopped nor ended by a fault.</summary>
    internal bool IsAccepting => _listener is not null && Volatile.Read(ref _started) == 1 && !_accepting.IsCompleted;

    /// <summary>What certificate mode trusts and presents, once started; <see langword="null"/> before or without certificates.</summary>
    internal Protocol.ZabbixCertificateTrust? CertificateTrust => Volatile.Read(ref _admission)?.Acceptor.Certificate;

    /// <summary>How an allowed-peer name is resolved; the system resolver, asynchronously. Tests substitute it.</summary>
    internal Func<string, CancellationToken, Task<IPAddress[]>> Resolver { get; init; } = Dns.GetHostAddressesAsync;

    /// <summary>Validates the options (<see cref="OptionsValidationException"/> when they are wrong) and binds the port.</summary>
    /// <inheritdoc/>
    public async Task StartingAsync(CancellationToken cancellationToken = default)
    {
        ValidateOptionsResult valid = new ZabbixListenerOptionsValidator().Validate(null, _options);
        if (valid.Failed) throw new OptionsValidationException(Options.DefaultName, typeof(ZabbixListenerOptions), valid.Failures);
        var admission = new Admission(_options, _logger, _time);
        if (Interlocked.CompareExchange(ref _admission, admission, null) is not null)
        {
            admission.Dispose();                                                           // a second StartingAsync keeps the first's
            return;
        }
        _listener = await _transport.BindAsync(_options.ToEndPoint(), cancellationToken).ConfigureAwait(false);
        if (_logger.IsEnabled(LogLevel.Information))
        {
            string? bound = _listener.EndPoint.ToString();
            ListenerLog.ListenerBound(_logger, bound, _options.TlsAccept, admission.Allowed);
        }
    }

    /// <inheritdoc/>
    /// <remarks>A second call does nothing: one listener runs one accept loop.</remarks>
    public Task StartAsync(CancellationToken cancellationToken = default)
    {
        if (_listener is null || _admission is null) throw new InvalidOperationException("The listener is not bound: StartingAsync runs first.");
        if (Interlocked.Exchange(ref _started, 1) != 0) return Task.CompletedTask;
        _accepting = AcceptAsync(_listener, _admission, _stopping.Token);
        return Task.CompletedTask;
    }

    /// <inheritdoc/>
    public Task StartedAsync(CancellationToken cancellationToken = default) => Task.CompletedTask;

    /// <inheritdoc/>
    public async Task StoppingAsync(CancellationToken cancellationToken = default)
    {
        if (_listener is not null) await _listener.UnbindAsync(cancellationToken).ConfigureAwait(false);   // no more accepts; the loop ends
        try { await _accepting.WaitAsync(cancellationToken).ConfigureAwait(false); }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { /* the host gave up waiting */ }
    }

    /// <inheritdoc/>
    public async Task StopAsync(CancellationToken cancellationToken = default)
    {
        // What is in flight finishes within the host's shutdown timeout; after that it is cut.
        try { await Task.WhenAll(_inFlight.Values).WaitAsync(cancellationToken).ConfigureAwait(false); }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { await _stopping.CancelAsync().ConfigureAwait(false); }
    }

    /// <inheritdoc/>
    public async Task StoppedAsync(CancellationToken cancellationToken = default)
    {
        if (_listener is not null) await _listener.DisposeAsync().ConfigureAwait(false);
        _listener = null;
        ListenerLog.ListenerStopped(_logger);
    }

    /// <inheritdoc/>
    /// <remarks>
    /// The accept loop and every connection still being handled end before the admission (certificate trust, limiters)
    /// and the telemetry they use are released: each connection is cut at once by the stop and bounded by its own deadline.
    /// </remarks>
    public async ValueTask DisposeAsync()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0) return;
        await _stopping.CancelAsync().ConfigureAwait(false);
        if (Interlocked.Exchange(ref _listener, null) is { } listener) await listener.DisposeAsync().ConfigureAwait(false);
        await _accepting.ConfigureAwait(ConfigureAwaitOptions.SuppressThrowing);
        await Task.WhenAll(_inFlight.Values).ConfigureAwait(ConfigureAwaitOptions.SuppressThrowing);   // faults were logged where they happened
        Release();
    }

    /// <summary>
    /// The synchronous form, for a container disposed synchronously: it releases the port too. The transport's unbind is
    /// started here; Kestrel's socket listener closes its socket inside that call, so the port is free when this returns
    /// (<c>A_disposed_listener_releases_its_port</c>); a transport that unbinds asynchronously finishes just after.
    /// </summary>
    public void Dispose()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0) return;
        _stopping.Cancel();
        if (Interlocked.Exchange(ref _listener, null) is { } listener)
        {
            ValueTask unbinding = listener.DisposeAsync();
            if (!unbinding.IsCompletedSuccessfully) _ = ObserveUnbindAsync(unbinding, _logger);   // its fault is logged, never unobserved
        }
        Release();
    }

    private static async Task ObserveUnbindAsync(ValueTask unbinding, ILogger logger)
    {
        try { await unbinding.ConfigureAwait(false); }
        catch (Exception failure) when (failure is not OutOfMemoryException) { ListenerLog.UnbindFailed(logger, failure); }
    }

    private void Release()
    {
        _stopping.Dispose();
        _admission?.Dispose();
        _telemetry.Dispose();
        Interlocked.Exchange(ref _container, null)?.Dispose();
    }

    /// <summary>Gives the listener the container a <see cref="ZabbixListenerBuilder"/> built it from, to dispose with itself.</summary>
    /// <param name="container">The builder's container.</param>
    internal void OwnContainer(IDisposable container) => _container = container;

    private async Task AcceptAsync(IConnectionListener listener, Admission admission, CancellationToken stopping)
    {
        int failedInARow = 0;
        while (!stopping.IsCancellationRequested)
        {
            ConnectionContext? connection;
            try
            {
                connection = await listener.AcceptAsync(stopping).ConfigureAwait(false);
                failedInARow = 0;
            }
            catch (OperationCanceledException) when (stopping.IsCancellationRequested) { break; }
            catch (ObjectDisposedException) { break; }                                        // disposed while accepting
            catch (Exception failure) when (failure is not OutOfMemoryException)
            {
                // An accept that fails (out of descriptors, a reset before accept) must not end the loop silently: it is
                // logged and the loop goes on, backing off while the failures continue.
                ListenerLog.AcceptFailed(_logger, ++failedInARow, failure);
                try { await Task.Delay(TimeSpan.FromMilliseconds(Math.Min(1000, 10 << Math.Min(failedInARow, 7))), _time, stopping).ConfigureAwait(false); }
                catch (OperationCanceledException) when (stopping.IsCancellationRequested) { break; }
                continue;
            }
            if (connection is null) break;                                                    // unbound

            // Held before the handling starts, so a handling that ends at once still finds its entry to remove.
            _inFlight[connection] = Task.CompletedTask;
            Task handling = TrackAsync(connection, admission, stopping);
            _inFlight.TryUpdate(connection, handling, Task.CompletedTask);
        }
    }

    private async Task TrackAsync(ConnectionContext connection, Admission admission, CancellationToken stopping)
    {
        try
        {
            await HandleAsync(connection, admission, stopping).ConfigureAwait(false);
        }
        finally
        {
            _inFlight.TryRemove(connection, out _);
        }
    }

    private async Task HandleAsync(ConnectionContext connection, Admission admission, CancellationToken stopping)
    {
        IPAddress peer = (connection.RemoteEndPoint as IPEndPoint)?.Address ?? IPAddress.None;
        long started = _time.GetTimestamp();
        using var expiry = new CancellationTokenSource(_options.Timeout, _time);
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(stopping, expiry.Token);
        // connection.ConnectionClosed is not linked in: Kestrel's socket transport fires it on the peer's FIN, and a peer
        // that half-closes after its request (nc -N, shutdown(SHUT_WR)) still reads the reply, as upstream reads one frame
        // and then writes (comms.c, zbx_tcp_recv then zbx_tcp_send). The FIN often arrives with the request, before the
        // read, so linking it even for the read loses the frame the pipe already holds. A peer that leaves outright ends
        // the read (the pipe completes) or the write (a reset); one queued for a slot waits out its deadline.
        // At the deadline (or when the host's patience is spent) a connection that has begun its reply is aborted, not
        // closed: a close drains the output first, which a peer that never reads never lets finish. One that has written
        // nothing is closed as usual (the abort of a socket with a receive pending is a reset).
        var conversing = new StrongBox<ZabbixConversationContext?>();
        using CancellationTokenRegistration cut = deadline.Token.UnsafeRegister(static state => CutIfWriting(((ConnectionContext, StrongBox<ZabbixConversationContext?>))state!), (connection, conversing));
        Interlocked.Increment(ref _active);
        try
        {
            await using (connection.ConfigureAwait(false))
            {
                // The per-peer partition refuses outright (no queue), by address, before anything costs more: a flood from
                // one address never multiplies the name lookups below.
                using RateLimitLease peerLease = admission.PerPeer.AttemptAcquire(peer);
                if (!peerLease.IsAcquired)
                {
                    Refuse(peer, ZabbixListenerRejection.PeerLimit);
                    return;
                }
                // Upstream checks the peer first (zbx_tcp_check_allowed_peers), then serves. A name is resolved here, per
                // connection, never on the accept loop.
                if (!await admission.Allowed.ContainsAsync(peer, Resolver, LogUnresolved, deadline.Token).ConfigureAwait(false))
                {
                    Refuse(peer, ZabbixListenerRejection.PeerNotAllowed);
                    return;
                }
                // The global limiter queues up to its limit — a slot freeing is what admits the next connection, so a
                // flood waits or is refused, never handled unbounded.
                using RateLimitLease lease = await admission.Global.AcquireAsync(1, deadline.Token).ConfigureAwait(false);
                if (!lease.IsAcquired)
                {
                    Refuse(peer, ZabbixListenerRejection.ListenerFull);
                    return;
                }
                await ServeAsync(connection, admission.Acceptor, peer, started, expiry, conversing, deadline.Token).ConfigureAwait(false);
                if (deadline.IsCancellationRequested) CutIfWriting((connection, conversing));        // a write begun after the deadline fired
            }
        }
        catch (OperationCanceledException) when (expiry.IsCancellationRequested)
        {
            LogRefused(peer, ZabbixListenerRejection.Timeout);                                // before it was served (a slow name, a full listener)
        }
        catch (OperationCanceledException cancelled) when (stopping.IsCancellationRequested || cancelled is ConnectionAbortedException)
        {
            // the host stopping, or the connection cut: not an error
        }
        catch (Exception failure) when (failure is not OutOfMemoryException)
        {
            LogFailed(peer, failure);                                                         // a cancellation nobody here asked for too
        }
        finally
        {
            Interlocked.Decrement(ref _active);
        }
    }

    private static void CutIfWriting((ConnectionContext Connection, StrongBox<ZabbixConversationContext?> Conversing) state)
    {
        if (Volatile.Read(ref state.Conversing.Value) is { Writing: true })
            state.Connection.Abort(new ConnectionAbortedException("the connection's deadline passed while its reply was unread"));
    }

    // A peer can repeat a refusal or a failure at will (a scanner, a flood, a misconfigured agent): each peer and code is
    // logged once an hour, and every one is still counted on the listener's meters (rejected_connections, the span).
    private void LogRefused(IPAddress peer, string rejection)
    {
        if (_logger.IsEnabled(LogLevel.Warning) && _refusals.ShouldLog((peer, rejection))) ListenerLog.PeerRefused(_logger, peer, rejection);
    }

    // A name that does not resolve fails every connection while the outage lasts: logged once an hour per name.
    private void LogUnresolved(string name, Exception failure)
    {
        if (_logger.IsEnabled(LogLevel.Warning) && _unresolved.ShouldLog(name)) ListenerLog.AllowedPeerUnresolved(_logger, name, failure);
    }

    private void LogFailed(IPAddress peer, Exception failure)
    {
        if (_logger.IsEnabled(LogLevel.Warning) && _failures.ShouldLog((peer, failure.GetType()))) ListenerLog.ConnectionFailed(_logger, peer, failure);
    }

    private void Refuse(IPAddress peer, string rejection)
    {
        LogRefused(peer, rejection);
        _telemetry.Rejected(rejection);
    }

    private async Task ServeAsync(ConnectionContext connection, ZabbixTlsAcceptor acceptor, IPAddress peer, long started, CancellationTokenSource expiry, StrongBox<ZabbixConversationContext?> conversing, CancellationToken deadline)
    {
        ZabbixOperation operation = _telemetry.Accepted(connection.LocalEndPoint, connection.RemoteEndPoint);
        ZabbixTlsAcceptModes? security = null;
        Exception? failure = null;
        string? errorType = null;
        Stream? owned = null;
        try
        {
            ZabbixAcceptedConnection accepted = await acceptor.AcceptAsync(connection.Transport, connection.RemoteEndPoint?.ToString() ?? "an unknown peer", deadline).ConfigureAwait(false);
            security = accepted.Security;
            owned = accepted.Owned;
            PipeReader reader = accepted.Transport.Input;
            PipeWriter writer = accepted.Transport.Output;
            ReadOnlyMemory<byte> request = await ZabbixHeader.ReadAsync(reader, _options.MaxPayloadBytes, cancellationToken: deadline).ConfigureAwait(false);
            var conversation = new ZabbixConversationContext(new ZabbixRequest(request, connection.RemoteEndPoint), reader, writer, _options.MaxPayloadBytes, _options.Compress);
            Volatile.Write(ref conversing.Value, conversation);
            try
            {
                await ConverseAsync(conversation, peer, deadline).ConfigureAwait(false);
            }
            catch (Exception fault) when (fault is not OperationCanceledException || !deadline.IsCancellationRequested)
            {
                // Who cancelled decides, not the exception's type: a handler's own cancellation (a downstream call's
                // timeout) is a fault like any other; only the connection's deadline or the host stopping is not.
                // A handler fault: the peer is told, in the protocol's own words (unless the conversation had
                // already answered), and the fault is logged and traced.
                ListenerLog.HandlerFaulted(_logger, peer, fault);
                failure = fault;
                // The peer reads a fixed sentence: the fault's type names the application's internals (its storage, its
                // libraries), and it is already in the log and on the span.
                if (conversation.BytesWritten == 0) await conversation.WriteAsync(Failed("the request could not be processed"), deadline).ConfigureAwait(false);
            }
            await writer.CompleteAsync().ConfigureAwait(false);
            await reader.CompleteAsync().ConfigureAwait(false);
            if (_logger.IsEnabled(LogLevel.Debug))
            {
                string word = ZabbixListenerTelemetry.Word(accepted.Security);
                double elapsed = _time.GetElapsedTime(started).TotalMilliseconds;
                ListenerLog.RequestHandled(_logger, peer, word, request.Length, (int)conversation.BytesWritten, elapsed);
            }
        }
        catch (ZabbixConnectionRefusedException refused)
        {
            Refuse(peer, refused.ErrorType);
            failure = refused;
        }
        catch (OperationCanceledException cancelled) when (expiry.IsCancellationRequested)
        {
            LogRefused(peer, ZabbixListenerRejection.Timeout);
            failure = cancelled;
            errorType = ZabbixListenerRejection.Timeout;
        }
        catch (OperationCanceledException cancelled) when (deadline.IsCancellationRequested || cancelled is ConnectionAbortedException)
        {
            failure = cancelled;                                                              // stopping or closed: Unset, not Error
        }
        catch (Exception broken) when (broken is not OutOfMemoryException)
        {
            LogFailed(peer, broken);
            failure = broken;
        }
        finally
        {
            _telemetry.Closed(operation, security, failure, errorType);
            if (owned is not null) await owned.DisposeAsync().ConfigureAwait(false);
        }
    }

    // The deadline holds even for a handler that does not observe its token: the connection is released at the deadline
    // and the handler is left to finish on its own; a fault it raises after that is logged, never unobserved.
    private async Task ConverseAsync(ZabbixConversationContext conversation, IPAddress peer, CancellationToken deadline)
    {
        ValueTask conversing = _conversation(conversation, deadline);
        if (conversing.IsCompleted)
        {
            await conversing.ConfigureAwait(false);
            return;
        }
        Task running = conversing.AsTask();
        try
        {
            await running.WaitAsync(deadline).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (deadline.IsCancellationRequested && !running.IsCompleted)
        {
            _ = ObserveLateAsync(running, _logger, peer);                                   // its fault is logged, never unobserved
            throw;
        }
    }

    private static async Task ObserveLateAsync(Task running, ILogger logger, IPAddress peer)
    {
        try { await running.ConfigureAwait(false); }
        catch (Exception fault) when (!running.IsCanceled) { ListenerLog.HandlerFaultedLate(logger, peer, fault); }
    }

    /// <summary>The frame a Zabbix peer reads as a failure: <c>{"response":"failed","info":…}</c>.</summary>
    /// <param name="info">What went wrong, for the peer's log.</param>
    internal static ReadOnlyMemory<byte> Failed(string info)
    {
        var buffer = new ArrayBufferWriter<byte>();
        using (var json = new Utf8JsonWriter(buffer))
        {
            json.WriteStartObject();
            json.WriteString("response"u8, "failed"u8);
            json.WriteString("info"u8, info);
            json.WriteEndObject();
        }
        return buffer.WrittenMemory;
    }

    // What StartingAsync builds before the port opens: the peer list, the TLS acceptor and the two limiters. Held as one
    // object, handed to the accept loop, so nothing after start reads a field that might not be set.
    private sealed class Admission : IDisposable
    {
        public Admission(ZabbixListenerOptions options, ILogger logger, TimeProvider time)
        {
            Allowed = options.Allowed();
            Acceptor = new ZabbixTlsAcceptor(options, logger, time);
            Global = new ConcurrencyLimiter(new ConcurrencyLimiterOptions { PermitLimit = options.MaxConcurrentConnections, QueueLimit = options.MaxConcurrentConnections, QueueProcessingOrder = QueueProcessingOrder.OldestFirst });
            int perPeer = options.MaxConnectionsPerPeer;
            PerPeer = PartitionedRateLimiter.Create<IPAddress, IPAddress>(peer => RateLimitPartition.GetConcurrencyLimiter(peer, _ => new ConcurrencyLimiterOptions { PermitLimit = perPeer, QueueLimit = 0 }));
        }

        public ZabbixAllowedPeers Allowed { get; }

        public ZabbixTlsAcceptor Acceptor { get; }

        public ConcurrencyLimiter Global { get; }

        public PartitionedRateLimiter<IPAddress> PerPeer { get; }

        public void Dispose()
        {
            Acceptor.Dispose();
            Global.Dispose();
            PerPeer.Dispose();
        }
    }
}
