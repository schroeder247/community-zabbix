// This Source Code Form is subject to the terms of the Mozilla Public
// License, v. 2.0. Copyright (c) 2026 schroeder247. SPDX-License-Identifier: MPL-2.0
using System.IO.Pipelines;
using System.Net.Sockets;
using System.Security.Authentication;
using Community.Zabbix.Diagnostics;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace Community.Zabbix.Protocol;

/// <summary>
/// Represents one node list with its connection words, the node that last answered and the redirect it follows. The
/// failover is a bounded walk over a list with no delay, as <c>zbx_tcp_connect_failover</c> is, and it must know which
/// phase failed. A Polly retry cannot do this: its <c>ShouldHandle</c> sees only the exception, which cannot tell a
/// connect-phase <see cref="IOException"/> from a reply-phase one, so it would re-send written requests, and its attempt
/// number is not a node once the first node moves. Delay, backoff and jitter — what a resilience pipeline is for — belong
/// to the caller's retry (the send loop), never to a node walk. The list is upstream's <c>addrs</c> vector: ordered
/// entries, the first is where the next call starts, a failed first entry moves behind the others
/// (<c>zbx_addrs_failover</c>), and a followed redirect is an entry with a revision in front (<c>comms_check_redirect</c>).
/// It is a <see cref="List{T}"/> under a <see cref="Lock"/>: the agent's refresh, upload and heartbeat share one list, as
/// upstream's active-checks thread shares its <c>addrs</c> between the three.
/// </summary>
/// <remarks>
/// The link owns what certificate mode read from its files (<see cref="ZabbixCertificateTrust"/>: the private key and the
/// CA, chain and revocation certificates), so whoever builds a link disposes it: a sender, getter or server wire built
/// from nodes, a host for the links it runs on. An exchange or notification taken from a disposed link throws
/// <see cref="ObjectDisposedException"/>.
/// </remarks>
internal sealed class ZabbixNodeLink : IDisposable
{
    /// <summary>What upstream says when a redirect is older than the one followed.</summary>
    internal const string StaleRedirect = "connection was reset because of service being offline";

    /// <summary>What upstream says when one exchange is redirected twice.</summary>
    internal const string SequentialRedirects = "sequential redirect responses detected";

    private readonly List<Entry> _entries;
    private readonly Lock _gate = new();
    private readonly ZabbixConnectionOptions _options;
    private readonly TimeProvider _time;
    private readonly ILogger _logger;
    private readonly ZabbixWireTelemetry _telemetry;
    private readonly ZabbixTlsMode _tls;
    private readonly ZabbixCertificateTrust? _trust;
    private volatile bool _disposed;

    public ZabbixNodeLink(IReadOnlyList<ZabbixServerNode> nodes, ZabbixConnectionOptions? options, TimeProvider? time, ILogger? logger, ZabbixWireTelemetry? telemetry)
    {
        ArgumentNullException.ThrowIfNull(nodes);
        if (nodes.Count == 0) throw new ArgumentException("An exchange needs at least one node.", nameof(nodes));
        if (nodes.Any(node => node.Port == 0)) throw new ArgumentException("The node list holds an empty (default) node.", nameof(nodes));
        _entries = [.. nodes.Select(node => new Entry(node, 0))];
        _options = options ?? new ZabbixConnectionOptions();
        if (_options.Timeout <= TimeSpan.Zero) throw new ArgumentException("Timeout must be positive.", nameof(options));
        if (_options.MaxPayloadBytes is <= 0) throw new ArgumentException("MaxPayloadBytes must be positive.", nameof(options));
        if (_options.Tls is { } tls) ZabbixTls.Validate(tls);
        _tls = _options.Tls?.Mode ?? ZabbixTlsMode.Unencrypted;
        _time = time ?? TimeProvider.System;
        // Certificate mode's files are read once, here, as upstream reads them once per process: a missing or unreadable
        // file fails the composition, not every exchange. The trust lives as long as this link, and Dispose releases it.
        _trust = _tls == ZabbixTlsMode.Certificate && _options.Tls is { } certificate ? ZabbixCertificateTrust.Load(certificate, _time) : null;
        _logger = logger ?? NullLogger.Instance;
        _telemetry = telemetry ?? ZabbixWireTelemetry.Shared;
    }

    /// <summary>The node the next call starts at.</summary>
    public ZabbixServerNode First
    {
        get { lock (_gate) return _entries[0].Node; }
    }

    /// <summary>The list as it stands, in order: a followed redirect (revision above 0) first until a failure moves it.</summary>
    public IReadOnlyList<(ZabbixServerNode Node, ulong Revision)> Entries
    {
        get { lock (_gate) return [.. _entries.Select(entry => (entry.Node, entry.Revision))]; }
    }

    /// <summary>What certificate mode trusts and presents; <see langword="null"/> in any other mode.</summary>
    internal ZabbixCertificateTrust? Trust => _trust;

    /// <summary>This link as an exchange delegate.</summary>
    public ZabbixExchangeCallback Exchange(bool followRedirects, bool allowLarge = false)
        => (request, cancellationToken) => ExchangeAsync(request, followRedirects, allowLarge, cancellationToken);

    public async ValueTask<ReadOnlyMemory<byte>> ExchangeAsync(ReadOnlyMemory<byte> request, bool followRedirects, bool allowLarge, CancellationToken cancellationToken)
    {
        ZabbixOperation operation = _telemetry.StartExchange(notify: false, _tls, request.Span);
        Attempt? attempt = null;
        bool redirected = false;
        try
        {
            attempt = await ConnectAsync(operation, only: null, cancellationToken).ConfigureAwait(false);
            while (true)
            {
                WireLog.FrameSending(_logger, attempt.Node, request.Length, _tls);
                ReadOnlyMemory<byte> reply = await RoundTripAsync(attempt, request, allowLarge).ConfigureAwait(false);
                if (_logger.IsEnabled(LogLevel.Debug))
                {
                    double elapsed = _time.GetElapsedTime(operation.StartTimestamp).TotalSeconds;
                    WireLog.FrameReceived(_logger, attempt.Node, reply.Length, elapsed);
                }
                if (!followRedirects || !ZabbixRedirect.TryParse(reply.Span, out ZabbixRedirect? redirect))
                {
                    _telemetry.Finish(operation, extra: ZabbixWireTelemetry.Peer(attempt.Node));
                    return reply;
                }
                Entry? target = Follow(operation, attempt, redirect, ref redirected);
                await attempt.DisposeAsync().ConfigureAwait(false);
                attempt = null;
                attempt = await ConnectAsync(operation, target, cancellationToken).ConfigureAwait(false);
            }
        }
        catch (Exception failure) when (attempt is not null)
        {
            Exception surfaced = Translate(failure, attempt, cancellationToken);
            Fail(operation, attempt, surfaced);
            if (surfaced == failure) throw;
            throw surfaced;
        }
        catch (Exception failure)
        {
            _telemetry.Finish(operation, failure, extra: ZabbixWireTelemetry.Peer(First));
            throw;
        }
        finally
        {
            if (attempt is not null) await attempt.DisposeAsync().ConfigureAwait(false);
        }
    }

    public async ValueTask NotifyAsync(ReadOnlyMemory<byte> notification, CancellationToken cancellationToken)
    {
        ZabbixOperation operation = _telemetry.StartExchange(notify: true, _tls, notification.Span);
        Attempt? attempt = null;
        bool redirected = false;
        try
        {
            attempt = await ConnectAsync(operation, only: null, cancellationToken).ConfigureAwait(false);
            while (true)
            {
                PipeWriter writer = PipeWriter.Create(attempt.Stream, new StreamPipeWriterOptions(leaveOpen: true));
                try { await ZabbixHeader.WriteAsync(writer, notification, _options.Compress, attempt.Deadline.Token).ConfigureAwait(false); }
                finally { await writer.CompleteAsync().ConfigureAwait(false); }
                WireLog.NotificationSent(_logger, attempt.Node, notification.Length, _tls);

                // zbx_comms_exchange_data with out == NULL: a receive that fails is the peer closing as expected.
                ReadOnlyMemory<byte>? reply = await ListenAsync(attempt, cancellationToken).ConfigureAwait(false);
                if (reply is not { } answer || !ZabbixRedirect.TryParse(answer.Span, out ZabbixRedirect? redirect))
                {
                    if (reply is { } ignored) WireLog.NotificationAnswered(_logger, attempt.Node, ignored.Length);
                    _telemetry.Finish(operation, extra: ZabbixWireTelemetry.Peer(attempt.Node));
                    return;
                }
                Entry? target = Follow(operation, attempt, redirect, ref redirected);
                await attempt.DisposeAsync().ConfigureAwait(false);
                attempt = null;
                attempt = await ConnectAsync(operation, target, cancellationToken).ConfigureAwait(false);
            }
        }
        catch (Exception failure) when (attempt is not null)
        {
            Exception surfaced = Translate(failure, attempt, cancellationToken);
            Fail(operation, attempt, surfaced);
            if (surfaced == failure) throw;
            throw surfaced;
        }
        catch (Exception failure)
        {
            _telemetry.Finish(operation, failure, extra: ZabbixWireTelemetry.Peer(First));
            throw;
        }
        finally
        {
            if (attempt is not null) await attempt.DisposeAsync().ConfigureAwait(false);
        }
    }

    private async ValueTask<ReadOnlyMemory<byte>> RoundTripAsync(Attempt attempt, ReadOnlyMemory<byte> request, bool allowLarge)
    {
        PipeWriter writer = PipeWriter.Create(attempt.Stream, new StreamPipeWriterOptions(leaveOpen: true));
        PipeReader reader = PipeReader.Create(attempt.Stream, new StreamPipeReaderOptions(bufferSize: ZabbixHeader.ReadSegment, leaveOpen: true));
        try
        {
            await ZabbixHeader.WriteAsync(writer, request, _options.Compress, attempt.Deadline.Token).ConfigureAwait(false);
            return await ZabbixHeader.ReadAsync(reader, _options.ReplyCap(allowLarge), allowLarge, attempt.Deadline.Token).ConfigureAwait(false);
        }
        finally
        {
            await writer.CompleteAsync().ConfigureAwait(false);
            await reader.CompleteAsync().ConfigureAwait(false);
        }
    }

    // After a notification: one frame, or nothing when the peer closes, the deadline passes or the bytes are not a frame
    // (upstream's zbx_tcp_recv failing with out == NULL). The peer closing is the usual answer to a heartbeat, so it is the
    // Try read's null, not an exception. The caller's own cancellation is still the caller's.
    private async ValueTask<ReadOnlyMemory<byte>?> ListenAsync(Attempt attempt, CancellationToken cancellationToken)
    {
        PipeReader reader = PipeReader.Create(attempt.Stream, new StreamPipeReaderOptions(leaveOpen: true));
        try
        {
            ReadOnlyMemory<byte>? frame = await ZabbixHeader.TryReadAsync(reader, _options.ReplyCap(allowLarge: false), allowLarge: false, attempt.Deadline.Token).ConfigureAwait(false);
            if (frame is null && _logger.IsEnabled(LogLevel.Debug)) WireLog.NotifyRedirectNotOffered(_logger, attempt.Node, "closed");
            return frame;
        }
        // A single `is` type test (not ZabbixFrames.IsExchangeFailure(closed) || …), so the catch-all rule sees the filter
        // narrows; the set is the same IsExchangeFailure recognizes plus the two a disposed/cancelled attempt raises.
        catch (Exception closed) when (!cancellationToken.IsCancellationRequested
                                       && closed is SocketException or IOException or AuthenticationException or InvalidDataException or TimeoutException
                                          or OperationCanceledException or ObjectDisposedException)
        {
            // Upstream ignores whatever a heartbeat's peer sends back once the notify itself succeeded (zbx_comms_exchange_data
            // with out == NULL): the peer closing, the deadline passing, or an unparseable reply are not a failure of the
            // notification. Debug, not Warning: this is the expected shape of "no redirect", not an exchange fault.
            if (_logger.IsEnabled(LogLevel.Debug))
            {
                string errorType = ZabbixErrorType.Of(closed);
                WireLog.NotifyRedirectNotOffered(_logger, attempt.Node, errorType);
            }
            return null;
        }
        finally
        {
            await reader.CompleteAsync().ConfigureAwait(false);
        }
    }

    // comms_check_redirect, applied to the list: returns the entry to connect to alone (a new target), or null to connect
    // with failover (a reset). A second redirect in one exchange and a stale revision fail the exchange.
    private Entry? Follow(in ZabbixOperation operation, Attempt attempt, ZabbixRedirect redirect, ref bool redirected)
    {
        if (redirected)
        {
            WireLog.RedirectRefused(_logger, attempt.Node, redirect.ToString(), SequentialRedirects);
            attempt.KeepList = true;                                                        // upstream returns before zbx_addrs_failover
            throw new ZabbixRedirectException(SequentialRedirects, redirect);
        }
        redirected = true;
        if (redirect.Reset || redirect.Address is not { } address)
        {
            _telemetry.Redirected(operation, attempt.Node, reset: true);
            lock (_gate)
            {
                if (_entries.Count > 1 && ReferenceEquals(_entries[0], attempt.Entry))    // "move redirected address at the end"
                {
                    _entries.RemoveAt(0);
                    _entries.Add(attempt.Entry);
                }
            }
            WireLog.RedirectReset(_logger, attempt.Node, First);
            return null;
        }
        var target = new Entry(address, redirect.Revision);
        lock (_gate)
        {
            // Upstream walks the list: any entry of a newer revision refuses the redirect; the (one) older redirect entry goes.
            if (_entries.Any(entry => entry.Revision > redirect.Revision))
            {
                WireLog.RedirectRefused(_logger, attempt.Node, redirect.ToString(), StaleRedirect);
                throw new ZabbixRedirectException(StaleRedirect, redirect);
            }
            int held = _entries.FindIndex(entry => entry.Revision != 0);
            if (held >= 0) _entries.RemoveAt(held);
            _entries.Insert(0, target);
        }
        _telemetry.Redirected(operation, attempt.Node, reset: false);
        WireLog.Redirected(_logger, attempt.Node, target.Node, target.Revision);
        return target;
    }

    // The connect phase: TCP, then TLS. A node that fails here is logged and the next is tried, in order from the node
    // that last answered; the first node moves past the failed one. The last node's failure is the caller's. With `only`,
    // that one entry is tried and nothing else (a redirect's target, "without failover logic"), and the list stays.
    private async ValueTask<Attempt> ConnectAsync(ZabbixOperation operation, Entry? only, CancellationToken cancellationToken)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        Entry[] order;
        if (only is not null) order = [only];
        else lock (_gate) order = [.. _entries];
        for (int step = 0; ; step++)
        {
            Entry entry = order[step];
            var attempt = new Attempt(entry, _options.Timeout, _time, cancellationToken);
            try
            {
                await attempt.Socket.ConnectAsync(attempt.Node.ToDnsEndPoint(), attempt.Deadline.Token).ConfigureAwait(false);
                attempt.Stream = new NetworkStream(attempt.Socket, ownsSocket: true);
                attempt.Stream = await ZabbixTls.WrapClientAsync(attempt.Stream, attempt.Node.Host, _options.Tls, _trust, _logger, attempt.Deadline.Token).ConfigureAwait(false);
                return attempt;
            }
            catch (Exception failure) when (!cancellationToken.IsCancellationRequested)
            {
                Exception surfaced = attempt.Expired ? Deadline(attempt, failure) : failure;
                await attempt.DisposeAsync().ConfigureAwait(false);
                if (only is null) Rotate(entry);
                if (step + 1 >= order.Length)
                {
                    if (surfaced == failure) throw;
                    throw surfaced;
                }
                string errorType = ZabbixErrorType.Of(surfaced);
                WireLog.NodeFailed(_logger, attempt.Node, errorType, order[step + 1].Node, surfaced);
                _telemetry.Failover(operation, attempt.Node, errorType);
            }
            catch (Exception failure)
            {
                await attempt.DisposeAsync().ConfigureAwait(false);
                if (failure is OperationCanceledException) throw;
                throw new OperationCanceledException("The connect was cancelled.", failure, cancellationToken);
            }
        }
    }

    // After the write: the caller's cancellation stays an OperationCanceledException; the deadline becomes a
    // TimeoutException whatever the socket threw when it was closed; anything else is the exchange's own failure.
    private Exception Translate(Exception failure, Attempt attempt, CancellationToken cancellationToken)
    {
        if (cancellationToken.IsCancellationRequested)
            return failure is OperationCanceledException ? failure : new OperationCanceledException("The exchange was cancelled.", failure, cancellationToken);
        if (attempt.Expired) return Deadline(attempt, failure);
        if (failure is InvalidDataException refused) WireLog.FrameRefused(_logger, attempt.Node, refused.Message);
        return failure;
    }

    private TimeoutException Deadline(Attempt attempt, Exception failure)
    {
        WireLog.DeadlineExpired(_logger, attempt.Node, _options.Timeout.TotalSeconds);
        return failure as TimeoutException ?? new TimeoutException($"The exchange with {attempt.Node} passed its {_options.Timeout.TotalSeconds:F1} s deadline.", failure);
    }

    private void Fail(in ZabbixOperation operation, Attempt attempt, Exception failure)
    {
        if (failure is TimeoutException) ZabbixLayerTelemetry.Event(operation, ZabbixWireTelemetry.DeadlineEvent);
        else if (failure is InvalidDataException) ZabbixLayerTelemetry.Event(operation, ZabbixWireTelemetry.FrameRefusedEvent);
        if (failure is not OperationCanceledException)
        {
            if (!attempt.KeepList) Rotate(attempt.Entry);
            if (_logger.IsEnabled(LogLevel.Debug))
            {
                string errorType = ZabbixErrorType.Of(failure);
                ZabbixServerNode next = First;
                WireLog.ExchangeFailed(_logger, attempt.Node, errorType, next, failure);
            }
        }
        _telemetry.Finish(operation, failure, extra: ZabbixWireTelemetry.Peer(attempt.Node));
    }

    /// <summary>Releases the certificate trust; an exchange already connected finishes on the stream it has.</summary>
    public void Dispose()
    {
        _disposed = true;
        _trust?.Dispose();
    }

    // commshigh.c zbx_addrs_failover: the failed entry goes behind the others — only while it is still first, so two
    // calls failing on the same node move the list once.
    private void Rotate(Entry failed)
    {
        lock (_gate)
        {
            if (_entries.Count < 2 || !ReferenceEquals(_entries[0], failed)) return;
            _entries.RemoveAt(0);
            _entries.Add(failed);
        }
    }

    /// <summary>One element of the address list: a node, and the revision of the redirect that put it there (0 for a configured node).</summary>
    private sealed class Entry(ZabbixServerNode node, ulong revision)
    {
        public ZabbixServerNode Node { get; } = node;

        public ulong Revision { get; } = revision;
    }

    /// <summary>One node's connection and its deadline: the socket is closed when the deadline or the caller's token fires,
    /// which is what stops a synchronous (BouncyCastle) read as well as an asynchronous one.</summary>
    private sealed class Attempt : IAsyncDisposable
    {
        private readonly CancellationTokenSource _expiry;
        private readonly CancellationTokenRegistration _close;

        public Attempt(Entry entry, TimeSpan timeout, TimeProvider time, CancellationToken cancellationToken)
        {
            Entry = entry;
            _expiry = new CancellationTokenSource(timeout, time);                           // the deadline, on the injected clock
            Deadline = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, _expiry.Token);
            Socket = new Socket(SocketType.Stream, ProtocolType.Tcp) { NoDelay = true };
            _close = Deadline.Token.UnsafeRegister(static state => (state as Socket)?.Dispose(), Socket);
        }

        public Entry Entry { get; }

        public ZabbixServerNode Node => Entry.Node;

        public CancellationTokenSource Deadline { get; }

        public Socket Socket { get; }

        public Stream Stream { get; set; } = Stream.Null;

        public bool Expired => _expiry.IsCancellationRequested;

        /// <summary>Whether a failure leaves the list as it is (upstream's sequential-redirect path skips the failover).</summary>
        public bool KeepList { get; set; }

        // A close that cannot say goodbye (the deadline already closed the socket under a TLS stream that sends
        // close_notify on dispose) is not a failure of the exchange: the exchange's own outcome is already decided.
        public async ValueTask DisposeAsync()
        {
            await _close.DisposeAsync().ConfigureAwait(false);
            try { await Stream.DisposeAsync().ConfigureAwait(false); }
            catch (IOException) { }
            catch (ObjectDisposedException) { }
            Socket.Dispose();
            Deadline.Dispose();
            _expiry.Dispose();
        }
    }
}
