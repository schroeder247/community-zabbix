// This Source Code Form is subject to the terms of the Mozilla Public
// License, v. 2.0. Copyright (c) 2026 schroeder247. SPDX-License-Identifier: MPL-2.0
using System.Security.Cryptography;
using System.Text.Json;
using Community.Zabbix.Hosting;
using Community.Zabbix.Protocol;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace Community.Zabbix;

/// <summary>
/// Speaks the Zabbix protocol, the framed messages Zabbix's own components exchange over TCP: it sends values to a server or
/// proxy as <c>zabbix_sender</c> does, asks a server for its status and queue as the web frontend does, and reads an item
/// from an agent as <c>zabbix_get</c> does. For the JSON-RPC API, the frontend's hosts, items and history, see
/// <c>ZabbixClient</c>.
/// </summary>
/// <remarks>
/// <para>
/// The client belongs to one server or proxy, <see cref="ZabbixWireClientOptions.Server"/>, and a read from an agent names the
/// agent, so the same client reads from many. Each request connects, sends one message, reads the reply and closes; methods
/// are safe to call at the same time.
/// </para>
/// <para>
/// A server is enough for the constructor. <see cref="CreateBuilder"/> builds a client with more to set, and
/// <see cref="ZabbixWireClientServiceCollectionExtensions.AddZabbixWireClient(Microsoft.Extensions.DependencyInjection.IServiceCollection, string)"/>
/// registers one in an application's container, with the same verbs.
/// </para>
/// <para>
/// A client's settings are fixed when it's built: the constructors copy the options they're given, so a later change to
/// that object doesn't reach the client. The options are checked at the first request, which is where certificate mode's
/// files are read. Options that can't be used, a certificate file that can't be read among them, throw
/// <see cref="InvalidOperationException"/> there, with the cause inside, as does a request to the server when none is set.
/// </para>
/// <para>
/// A client from a container, injected or returned by <see cref="IZabbixWireClientFactory"/>, is shared: the container owns
/// it and disposes it when it's disposed, and a caller's <see cref="Dispose()"/> or <see cref="DisposeAsync"/> leaves it
/// running, as disposing an <c>HttpClient</c> from <c>IHttpClientFactory</c> leaves its handler. A client you construct, or
/// build with <see cref="CreateBuilder"/>, is yours to dispose.
/// </para>
/// </remarks>
/// <example>
/// <code language="csharp">
/// await using var wire = new ZabbixWireClient("zbx-server-01");
/// await wire.SendAsync("web-01", pending, 42);
/// ZabbixAgentAnswer uname = await wire.GetAgentItemAsync("web-01", "system.uname");
/// </code>
/// </example>
public class ZabbixWireClient : IDisposable, IAsyncDisposable
{
    /// <summary>The port an agent listens on unless its address names another.</summary>
    public const int DefaultAgentPort = ZabbixGetter.DefaultPort;

    /// <summary>The trapper port of a server or proxy.</summary>
    public const int DefaultServerPort = ZabbixServerNode.DefaultPort;

    private readonly ZabbixExchangeCallback? _exchange;
    private readonly ILoggerFactory _loggers;
    private readonly TimeProvider _time;
    private readonly Lock _gate = new();
    private readonly List<ZabbixNodeLink> _links = [];
    private readonly Dictionary<string, ZabbixGetter> _getters = new(StringComparer.Ordinal);
    private (ZabbixSender Sender, ZabbixServerWire ServerWire)? _connected;
    private IDisposable? _container;
    private bool _checked;
    private bool _disposed;
    private bool _shared;                                                // the container's: a caller's dispose leaves it running
    private bool _released;                                              // the container's owner let it go

    /// <summary>A client with the default options, which reads from agents: it has no server to send to.</summary>
    public ZabbixWireClient()
        : this(new ZabbixWireClientOptions())
    {
    }

    /// <summary>A client of <paramref name="server"/>.</summary>
    /// <param name="server">The server or proxy: <c>host</c> or <c>host:port</c> (port 10051 when omitted, and an IPv6 address with a port is bracketed), or the nodes of one HA cluster, <c>zbx1;zbx2</c>.</param>
    /// <exception cref="ArgumentException"><paramref name="server"/> is empty, or does not name a server or proxy.</exception>
    /// <exception cref="ArgumentNullException"><paramref name="server"/> is <see langword="null"/>.</exception>
    public ZabbixWireClient(string server)
        : this(ForServer(server))
    {
    }

    /// <summary>A client as <paramref name="options"/> says.</summary>
    /// <param name="options">The client's options, copied: a later change to them doesn't reach the client.</param>
    /// <exception cref="ArgumentNullException"><paramref name="options"/> is <see langword="null"/>.</exception>
    public ZabbixWireClient(ZabbixWireClientOptions options)
        : this(options, null, null)
    {
    }

    /// <summary>A client as <paramref name="options"/> says, with the application's logging and clock.</summary>
    /// <param name="options">The client's options, copied: a later change to them doesn't reach the client.</param>
    /// <param name="loggers">Where to log; nowhere when <see langword="null"/>. The caller keeps owning it.</param>
    /// <param name="time">The clock the deadlines run on; the system's when <see langword="null"/>.</param>
    /// <exception cref="ArgumentNullException"><paramref name="options"/> is <see langword="null"/>.</exception>
    public ZabbixWireClient(ZabbixWireClientOptions options, ILoggerFactory? loggers, TimeProvider? time)
    {
        ArgumentNullException.ThrowIfNull(options);
        Options = options.Clone();
        _loggers = loggers ?? NullLoggerFactory.Instance;
        _time = time ?? TimeProvider.System;
    }

    /// <summary>
    /// A client whose every request goes over <paramref name="exchange"/>, as an <c>HttpClient</c> sends through a handler:
    /// a <see cref="ZabbixWireLink"/>'s exchange, one that decorates it, or one that stands in for it in a test.
    /// </summary>
    /// <param name="exchange">The transport. It decides where a request goes, and the caller keeps owning it.</param>
    /// <exception cref="ArgumentNullException"><paramref name="exchange"/> is <see langword="null"/>.</exception>
    public ZabbixWireClient(ZabbixExchangeCallback exchange)
        : this(exchange, new ZabbixWireClientOptions(), null, null)
    {
    }

    /// <summary>A client whose every request goes over <paramref name="exchange"/>, with the application's options, logging and clock.</summary>
    /// <param name="exchange">The transport. It decides where a request goes, and the caller keeps owning it.</param>
    /// <param name="options">The client's options, copied. <see cref="ZabbixWireClientOptions.Connection"/> doesn't apply, because the transport has its own, and <see cref="ZabbixWireClientOptions.Server"/> only names the other end in logs and failures.</param>
    /// <param name="loggers">Where to log; nowhere when <see langword="null"/>. The caller keeps owning it.</param>
    /// <param name="time">The clock the deadlines run on; the system's when <see langword="null"/>.</param>
    /// <exception cref="ArgumentNullException"><paramref name="exchange"/> or <paramref name="options"/> is <see langword="null"/>.</exception>
    public ZabbixWireClient(ZabbixExchangeCallback exchange, ZabbixWireClientOptions options, ILoggerFactory? loggers, TimeProvider? time)
        : this(options, loggers, time)
    {
        ArgumentNullException.ThrowIfNull(exchange);
        _exchange = exchange;
    }

    /// <summary>The client's own copy of its options, fixed when it was built.</summary>
    internal ZabbixWireClientOptions Options { get; }

    /// <summary>
    /// Starts a builder for a client with more than a server to set: more nodes, TLS, a token, deadlines, or logging.
    /// </summary>
    /// <returns>A builder whose <see cref="ZabbixWireClientBuilder.Build"/> returns the client.</returns>
    /// <example>
    /// <code language="csharp">
    /// using ZabbixWireClient wire = ZabbixWireClient.CreateBuilder()
    ///     .WithServer("zbx-server-01")
    ///     .WithToken(token)
    ///     .WithTimeout(TimeSpan.FromSeconds(5))
    ///     .Build();
    /// </code>
    /// </example>
    public static ZabbixWireClientBuilder CreateBuilder() => new();

    /// <summary>What sends values, for the send loop in <c>Community.Zabbix.Agent</c>.</summary>
    internal ZabbixSender Sender => Connect().Sender;

    /// <summary>The connections the client built, for what certificate mode read from their files.</summary>
    internal IReadOnlyList<ZabbixNodeLink> Links
    {
        get
        {
            lock (_gate) return [.. _links];
        }
    }

    private ZabbixServerWire ServerWire => Connect().ServerWire;

    /// <summary>
    /// Sends values and returns the server's answer. The values go in frames of at most <see cref="ZabbixWireClientOptions.BatchSize"/> values
    /// and <see cref="ZabbixWireClientOptions.MaxBatchBytes"/> bytes, in order, each sent when the one before was answered, as
    /// <c>zabbix_sender</c> sends its batches. A returned answer is a success: every frame was taken, and
    /// <see cref="ZabbixSenderResponse.Failed"/> counts the values the server refused inside them.
    /// </summary>
    /// <remarks>
    /// When the first frame fails, nothing was taken, and the failure is thrown as it is (the exceptions below). When a
    /// later frame fails, the frames before it were taken and sending stops there, as <c>zabbix_sender</c> stops at a
    /// failed batch: the failure is a <see cref="ZabbixSenderIncompleteException"/> that carries the answer for the frames
    /// taken and the values the server did not confirm, so a caller sends those again and nothing twice. A cancellation of
    /// <paramref name="cancellationToken"/> after the first frame was taken is a <see cref="ZabbixSenderCanceledException"/>
    /// carrying the same.
    /// </remarks>
    /// <param name="values">The values to send, each with its host, key, value and clock.</param>
    /// <param name="cancellationToken">A cancellation token.</param>
    /// <returns>The trapper's counted answer, summed over the frames the values took.</returns>
    /// <exception cref="ArgumentException">One item alone is larger than <see cref="ZabbixWireClientOptions.MaxBatchBytes"/> allows; nothing was sent.</exception>
    /// <exception cref="System.Net.Sockets.SocketException">No node could be reached.</exception>
    /// <exception cref="IOException">The connection was lost mid-exchange (a standby HA node resets it), or a proxy group
    /// redirected the values and the redirect could not be followed (nothing was taken).</exception>
    /// <exception cref="System.Security.Authentication.AuthenticationException">The TLS handshake was refused.</exception>
    /// <exception cref="TimeoutException">The deadline passed.</exception>
    /// <exception cref="InvalidDataException">The reply was not a frame, or not JSON.</exception>
    /// <exception cref="ZabbixTrapperRefusedException">The trapper answered with something other than a counted success.</exception>
    /// <exception cref="ZabbixSenderIncompleteException">A frame failed after earlier frames of the call were taken.</exception>
    /// <exception cref="ObjectDisposedException">The client was disposed.</exception>
    /// <exception cref="ZabbixSenderCanceledException">The token was cancelled after earlier frames of the call were taken.</exception>
    /// <exception cref="OperationCanceledException">The token was cancelled before the first frame was taken.</exception>
    /// <exception cref="InvalidOperationException"><see cref="ZabbixWireClientOptions.Server"/> is not set, or does not parse.</exception>
    public virtual Task<ZabbixSenderResponse> SendAsync(IReadOnlyCollection<ZabbixValue> values, CancellationToken cancellationToken = default) => Sender.SendAsync(values, cancellationToken);

    /// <summary>Sends one value of a typed item to the server or proxy, collected now.</summary>
    /// <typeparam name="T">The item's value type.</typeparam>
    /// <param name="hostName">The technical name of the host the value belongs to.</param>
    /// <param name="key">The typed key of the item the value belongs to.</param>
    /// <param name="value">The value to send.</param>
    /// <param name="cancellationToken">A cancellation token.</param>
    /// <returns>The trapper's counted answer.</returns>
    public virtual Task<ZabbixSenderResponse> SendAsync<T>(string hostName, ZabbixItemKey<T> key, T value, CancellationToken cancellationToken = default)
        where T : ISpanParsable<T>
        => SendAsync([ZabbixValue.From(hostName, key, value, _time.GetUtcNow())], cancellationToken);

    /// <summary>Sends several values together, in the frames <see cref="ZabbixWireClientOptions.BatchSize"/> allows.</summary>
    /// <param name="build">What adds the values to the batch.</param>
    /// <param name="cancellationToken">A cancellation token.</param>
    /// <returns>The trapper's counted answer, summed over the frames.</returns>
    public virtual Task<ZabbixSenderResponse> SendAsync(Action<ZabbixSendBatch> build, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(build);
        var batch = new ZabbixSendBatch(_time);
        build(batch);
        return SendAsync(batch.Values, cancellationToken);
    }

    /// <summary>Reads a typed item from an agent, as <c>zabbix_get</c> does.</summary>
    /// <typeparam name="T">The item's value type.</typeparam>
    /// <param name="agent">The agent: <c>host</c> or <c>host:port</c> (port <see cref="DefaultAgentPort"/> when omitted).</param>
    /// <param name="key">The typed key of the item to read.</param>
    /// <param name="cancellationToken">A cancellation token.</param>
    /// <returns>The value.</returns>
    /// <exception cref="ZabbixItemNotSupportedException">The agent doesn't support the item.</exception>
    /// <exception cref="FormatException">The value isn't a <typeparamref name="T"/>.</exception>
    public virtual async Task<T> GetAgentItemAsync<T>(string agent, ZabbixItemKey<T> key, CancellationToken cancellationToken = default)
        where T : ISpanParsable<T>
    {
        ZabbixAgentAnswer answer = await GetAgentItemAsync(agent, key.Key, cancellationToken).ConfigureAwait(false);
        return answer is { IsSupported: true, Value: { } value } ? key.Parse(value) : throw new ZabbixItemNotSupportedException(key.Key, answer.Error);
    }

    /// <summary>Reads an item from an agent, as <c>zabbix_get</c> does.</summary>
    /// <param name="agent">The agent: <c>host</c> or <c>host:port</c> (port <see cref="DefaultAgentPort"/> when omitted).</param>
    /// <param name="key">The item key, with its parameters: <c>vfs.fs.size[/,pfree]</c>.</param>
    /// <param name="cancellationToken">A cancellation token.</param>
    /// <returns>The value, or why the agent does not support the key.</returns>
    /// <inheritdoc cref="GetAgentItemAsync(string, ZabbixItemKey, CancellationToken)" path="/exception"/>
    public Task<ZabbixAgentAnswer> GetAgentItemAsync(string agent, string key, CancellationToken cancellationToken = default) => GetAgentItemAsync(agent, ZabbixItemKey.Parse(key), cancellationToken);

    /// <summary>Reads an item from an agent, as <c>zabbix_get</c> does.</summary>
    /// <param name="agent">The agent: <c>host</c> or <c>host:port</c> (port <see cref="DefaultAgentPort"/> when omitted).</param>
    /// <param name="key">The item key, with its parameters.</param>
    /// <param name="cancellationToken">A cancellation token.</param>
    /// <returns>The value, or why the agent does not support the key.</returns>
    /// <exception cref="ArgumentException"><paramref name="agent"/> is empty, or does not parse.</exception>
    /// <exception cref="ArgumentNullException"><paramref name="agent"/> is <see langword="null"/>.</exception>
    /// <exception cref="ZabbixAgentRefusedException">The agent refused the request.</exception>
    /// <exception cref="ObjectDisposedException">The client was disposed.</exception>
    public virtual Task<ZabbixAgentAnswer> GetAgentItemAsync(string agent, ZabbixItemKey key, CancellationToken cancellationToken = default) => Getter(agent).GetAsync(key, cancellationToken);

    /// <summary><c>zabbix.stats</c>: the server's internal statistics (<c>StatsAllowedIP</c> must admit this address).</summary>
    /// <param name="cancellationToken">A cancellation token.</param>
    /// <returns>The statistics object.</returns>
    /// <exception cref="ZabbixServerRefusedException">The server refused (<c>Permission denied.</c> when the address is not allowed).</exception>
    public virtual Task<JsonElement> GetStatsAsync(CancellationToken cancellationToken = default) => ServerWire.GetStatsAsync(cancellationToken);

    /// <summary>
    /// <c>zabbix.stats</c> with <c>type: queue</c>: how many items are overdue by at least <paramref name="fromDelay"/> (6 s by
    /// default) and, when given, at most <paramref name="toDelay"/>.
    /// </summary>
    /// <param name="fromDelay">The least delay counted.</param>
    /// <param name="toDelay">The most delay counted; unbounded when <see langword="null"/>.</param>
    /// <param name="cancellationToken">A cancellation token.</param>
    /// <returns>The count.</returns>
    /// <exception cref="ZabbixServerRefusedException">The server refused.</exception>
    public virtual Task<long> GetQueueSizeAsync(TimeSpan? fromDelay = null, TimeSpan? toDelay = null, CancellationToken cancellationToken = default) => ServerWire.GetQueueSizeAsync(fromDelay, toDelay, cancellationToken);

    /// <summary><c>queue.get</c> as an overview: overdue items counted by delay, grouped by item type or by proxy (super admin only).</summary>
    /// <param name="overview">How to group.</param>
    /// <param name="cancellationToken">A cancellation token.</param>
    /// <returns>One row per item type or proxy with overdue items.</returns>
    /// <exception cref="ZabbixServerRefusedException">The server refused (the session is not a super admin's).</exception>
    public virtual Task<IReadOnlyList<ZabbixQueueDelays>> GetQueueAsync(ZabbixQueueOverview overview = ZabbixQueueOverview.ByItemType, CancellationToken cancellationToken = default) => ServerWire.GetQueueAsync(overview, cancellationToken);

    /// <summary><c>queue.get</c> in detail: the most overdue items, up to <paramref name="limit"/>, and the total (super admin only).</summary>
    /// <param name="limit">How many items to list.</param>
    /// <param name="cancellationToken">A cancellation token.</param>
    /// <returns>The items and the total.</returns>
    /// <exception cref="ZabbixServerRefusedException">The server refused.</exception>
    public virtual Task<ZabbixQueueDetails> GetQueueDetailsAsync(int limit, CancellationToken cancellationToken = default) => ServerWire.GetQueueDetailsAsync(limit, cancellationToken);

    /// <summary><c>status.get</c> with <c>type: full</c>: the "System information" statistics (empty for a non-super-admin session).</summary>
    /// <param name="cancellationToken">A cancellation token.</param>
    /// <returns>The statistics object.</returns>
    /// <exception cref="ZabbixServerRefusedException">The server refused (the session is not valid).</exception>
    public virtual Task<JsonElement> GetStatusAsync(CancellationToken cancellationToken = default) => ServerWire.GetStatusAsync(cancellationToken);

    /// <summary><c>status.get</c> with <c>type: ping</c>: whether the server is running and takes this session (the frontend's <c>isRunning</c>).</summary>
    /// <param name="cancellationToken">A cancellation token.</param>
    /// <returns>A task that completes when the server answered.</returns>
    /// <exception cref="ZabbixServerRefusedException">The server refused (the session is not valid).</exception>
    public virtual Task PingAsync(CancellationToken cancellationToken = default) => ServerWire.PingAsync(cancellationToken);

    /// <summary><c>item.test</c>: the server runs one item's collection and preprocessing as the frontend's "Test" does.</summary>
    /// <param name="data">The request's <c>data</c> as the frontend sends it (<c>item</c>, <c>host</c>, <c>options</c>).</param>
    /// <param name="cancellationToken">A cancellation token.</param>
    /// <returns>The reply.</returns>
    /// <exception cref="ZabbixServerRefusedException">The server refused.</exception>
    public virtual Task<ZabbixServerReply> TestItemAsync(JsonElement data, CancellationToken cancellationToken = default) => ServerWire.TestItemAsync(data, cancellationToken);

    /// <summary>
    /// <c>alert.send</c>: a test message through a media type, as the frontend's "Test" sends it
    /// (<c>CControllerMediatypeTestSend</c>): a script media type's parameters as a list of values, a webhook's as an object
    /// of name to value (the server reads <c>parameters</c> as the brackets they are, <c>trapper_process_alert_send</c>).
    /// </summary>
    /// <param name="mediaTypeId">The media type.</param>
    /// <param name="sendTo">Who receives the message, as the media type addresses them.</param>
    /// <param name="subject">The subject of the test message.</param>
    /// <param name="message">The body of the test message.</param>
    /// <param name="parameters">A script's or a webhook's parameters; none for the other types.</param>
    /// <param name="cancellationToken">A cancellation token.</param>
    /// <returns>The reply.</returns>
    /// <exception cref="ZabbixServerRefusedException">The server refused.</exception>
    public virtual Task<ZabbixServerReply> SendAlertAsync(ulong mediaTypeId, string sendTo, string subject, string message, ZabbixAlertParameters? parameters = null, CancellationToken cancellationToken = default) => ServerWire.SendAlertAsync(mediaTypeId, sendTo, subject, message, parameters, cancellationToken);

    /// <summary><c>report.test</c>: a scheduled report generated and sent now.</summary>
    /// <param name="data">The request's <c>data</c> as the frontend sends it.</param>
    /// <param name="cancellationToken">A cancellation token.</param>
    /// <returns>The reply.</returns>
    /// <exception cref="ZabbixServerRefusedException">The server refused.</exception>
    public virtual Task<ZabbixServerReply> TestReportAsync(JsonElement data, CancellationToken cancellationToken = default) => ServerWire.TestReportAsync(data, cancellationToken);

    /// <summary><c>expressions.evaluate</c>: trigger expressions evaluated by the server.</summary>
    /// <param name="expressions">The trigger expressions to evaluate.</param>
    /// <param name="cancellationToken">A cancellation token.</param>
    /// <returns>One result per expression.</returns>
    /// <exception cref="ZabbixServerRefusedException">The server refused.</exception>
    public virtual Task<IReadOnlyList<ZabbixExpressionResult>> EvaluateExpressionsAsync(IEnumerable<string> expressions, CancellationToken cancellationToken = default) => ServerWire.EvaluateExpressionsAsync(expressions, cancellationToken);

    /// <summary>Any request whose handler reads a <c>data</c> object, in the server's own form, with the session id.</summary>
    /// <param name="request">The request name.</param>
    /// <param name="data">The request's <c>data</c> object.</param>
    /// <param name="cancellationToken">A cancellation token.</param>
    /// <returns>The reply.</returns>
    /// <exception cref="ZabbixServerRefusedException">The server refused.</exception>
    public virtual Task<ZabbixServerReply> RequestAsync(string request, JsonElement data, CancellationToken cancellationToken = default) => ServerWire.RequestAsync(request, data, cancellationToken);

    /// <summary>
    /// Releases what the client built: its connections, what certificate mode read from their files, and the container a
    /// <see cref="ZabbixWireClientBuilder"/> built it in. A given exchange stays the caller's. A shared client, one a
    /// container handed out, is left running: the container disposes it.
    /// </summary>
    public void Dispose()
    {
        Dispose(disposing: true);
        GC.SuppressFinalize(this);
    }

    /// <summary>
    /// Releases what the client built, as <see cref="Dispose()"/> does. A shared client, one a container handed out, is left
    /// running: the container disposes it.
    /// </summary>
    /// <returns>A task that completes when the client is released.</returns>
    public async ValueTask DisposeAsync()
    {
        await DisposeAsyncCore().ConfigureAwait(false);
        Dispose(disposing: false);
        GC.SuppressFinalize(this);
    }

    /// <summary>
    /// Releases what the client holds, as <see cref="Dispose(bool)"/> does: nothing it holds needs to be awaited. A shared
    /// client is released only by its container.
    /// </summary>
    /// <returns>A task that completes when the client is released.</returns>
    protected virtual ValueTask DisposeAsyncCore()
    {
        ReleaseHeld();
        return ValueTask.CompletedTask;
    }

    /// <summary>Releases what the client holds; see <see cref="Dispose()"/>. A shared client is released only by its container.</summary>
    /// <param name="disposing">Whether this is <see cref="Dispose()"/>, rather than the end of <see cref="DisposeAsync"/>.</param>
    protected virtual void Dispose(bool disposing)
    {
        if (disposing) ReleaseHeld();
    }

    // The connections, the getters, the links and the builder's container, once, and not while a container shares the client.
    private void ReleaseHeld()
    {
        IDisposable? container;
        lock (_gate)
        {
            if (_disposed || (_shared && !_released)) return;
            _disposed = true;
            if (_connected is { } connected)
            {
                connected.Sender.Dispose();
                connected.ServerWire.Dispose();
            }
            foreach (ZabbixGetter getter in _getters.Values) getter.Dispose();
            foreach (ZabbixNodeLink link in _links) link.Dispose();
            (container, _container) = (_container, null);
        }

        // Outside the gate: the container disposes this client too, since it built it, and that call returns at once.
        container?.Dispose();
    }

    /// <summary>Whether a container shares the client: it owns it, and a caller's dispose leaves it running.</summary>
    internal bool IsShared
    {
        get { lock (_gate) return _shared; }
    }

    /// <summary>Makes the client the container's, which <see cref="ZabbixWireClientOwner"/> releases when the container is disposed.</summary>
    /// <returns>This client.</returns>
    internal ZabbixWireClient Share()
    {
        lock (_gate) _shared = true;
        return this;
    }

    /// <summary>Releases the client, shared or not: the container's disposal, through <see cref="ZabbixWireClientOwner"/>.</summary>
    internal void Release()
    {
        lock (_gate) _released = true;
        Dispose();
    }

    /// <summary>
    /// Gives the client the container a <see cref="ZabbixWireClientBuilder"/> built it in, to dispose with itself. The client
    /// is the caller's from then on, not shared.
    /// </summary>
    /// <param name="container">The builder's container, which built this client.</param>
    internal void OwnContainer(IDisposable container)
    {
        lock (_gate)
        {
            _container = container;
            _shared = false;
        }
    }

    private static ZabbixWireClientOptions ForServer(string server)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(server);
        if (!ZabbixWireClientOptions.TryParseServer(server, out _, out string? reason)) throw new ArgumentException(reason, nameof(server));
        return new ZabbixWireClientOptions { Server = server };
    }

    // The options are read here, at the first request: what is wrong by then is the client's state, not an argument.
    private void CheckOptions()
    {
        if (_checked) return;
        ZabbixOptionsCheck.RequireValid(nameof(ZabbixWireClient), Options, new ZabbixWireClientOptionsValidator());
        _checked = true;
    }

    private IReadOnlyList<ZabbixServerNode> ServerNodes()
    {
        if (string.IsNullOrWhiteSpace(Options.Server))
            throw new InvalidOperationException("This request goes to a server or proxy, and none is set. You must give the client one: pass it to the constructor, or set it with WithServer.");
        return ZabbixWireClientOptions.TryParseServer(Options.Server, out IReadOnlyList<ZabbixServerNode>? nodes, out string? reason) ? nodes : throw new InvalidOperationException(reason);
    }

    // Thrown at the call, as a usage error is (TAP): a file the options name is what is wrong, not the request that read it.
    private ZabbixNodeLink Open(IReadOnlyList<ZabbixServerNode> nodes, ILogger logger)
    {
        ZabbixNodeLink link;
        try
        {
            link = new ZabbixNodeLink(nodes, Options.Connection, _time, logger, telemetry: null);
        }
        catch (Exception unusable) when (unusable is IOException or InvalidDataException or UnauthorizedAccessException or CryptographicException or ArgumentException)
        {
            throw new InvalidOperationException($"Options.Connection cannot be used: {unusable.Message}", unusable);
        }
        _links.Add(link);
        return link;
    }

    // Built once, at the first request that needs the server: the link's certificate files are read here, and the sends and
    // the server's requests share its node list. Sends follow a proxy group's redirects as zabbix_sender does; a server never
    // sends one to the frontend's requests.
    private (ZabbixSender Sender, ZabbixServerWire ServerWire) Connect()
    {
        lock (_gate)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            if (_connected is { } connected) return connected;
            CheckOptions();
            ILogger logger = _loggers.CreateLogger<ZabbixWireClient>();
            ZabbixExchangeCallback sending, asking;
            IReadOnlyList<ZabbixServerNode>? nodes;
            if (_exchange is { } exchange)
            {
                nodes = string.IsNullOrWhiteSpace(Options.Server) ? null : ServerNodes();
                (sending, asking) = (exchange, exchange);
            }
            else
            {
                nodes = ServerNodes();
                ZabbixNodeLink link = Open(nodes, logger);
                (sending, asking) = (link.Exchange(followRedirects: true), link.Exchange(followRedirects: false));
            }
            (ZabbixSender, ZabbixServerWire) built = (
                new ZabbixSender(sending, nodes is null ? "the given exchange" : string.Join(", ", nodes), logger) { BatchSize = Options.BatchSize, MaxBatchBytes = Options.MaxBatchBytes },
                new ZabbixServerWire(asking, Options.SessionId, logger, nodes?[0]));
            _connected = built;
            return built;
        }
    }

    private ZabbixGetter Getter(string agent)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(agent);
        lock (_gate)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            if (_getters.TryGetValue(agent, out ZabbixGetter? known)) return known;
            CheckOptions();
            ILogger logger = _loggers.CreateLogger<ZabbixWireClient>();
            ZabbixGetter getter;
            if (_exchange is { } exchange)
            {
                getter = new ZabbixGetter(exchange, agent, logger) { CheckTimeout = Options.CheckTimeout };
            }
            else
            {
                ZabbixServerNode node = ZabbixServerNode.Parse(agent, DefaultAgentPort, nameof(agent));
                getter = new ZabbixGetter(Open([node], logger).Exchange(followRedirects: false), node.ToString(), logger) { CheckTimeout = Options.CheckTimeout };
            }
            _getters.Add(agent, getter);
            return getter;
        }
    }
}
