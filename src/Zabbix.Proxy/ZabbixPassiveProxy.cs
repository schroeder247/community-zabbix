// This Source Code Form is subject to the terms of the Mozilla Public
// License, v. 2.0. Copyright (c) 2026 schroeder247. SPDX-License-Identifier: MPL-2.0
using System.Diagnostics;
using System.Diagnostics.Metrics;
using System.Security.Cryptography;
using System.Text.Json;
using System.Threading.RateLimiting;
using Community.Zabbix.Diagnostics;
using Community.Zabbix.Hosting;
using Community.Zabbix.Listening;
using Community.Zabbix.Proxy;
using Community.Zabbix.Storage;
using Microsoft.AspNetCore.Connections;
using Microsoft.Extensions.Logging;

namespace Community.Zabbix;

/// <summary>
/// Hosts a passive proxy: the server connects to it, and each visit is a conversation on its own connection.
/// </summary>
/// <remarks>
/// One listener answers (<c>proxypoller.c</c>, <c>trapper_proxy.c</c>, <c>proxyconfigwrite.c</c>):
/// <list type="bullet">
/// <item><c>proxy config</c>: the revisions held out, the configuration in, merged, then <c>success</c>;</item>
/// <item><c>proxy data</c>: a page leased from the store out; only the server's <c>success</c> acknowledges it, and an
/// unacknowledged page is the next pull's with the same ids, so the server's <c>last_id</c> de-duplicates it. The turn is the
/// one the active proxy runs, one exchange per pull: a refused page is taken
/// apart over the next pulls and <c>upload: disabled</c> holds the pages until a pull's empty probe is taken;</item>
/// <item><c>proxy tasks</c>: the task results, version and clock out, the server's
/// tasks in;</item>
/// <item><c>sender data</c> / <c>agent data</c>: the trapper, admitting by Zabbix's rules (<see cref="ZabbixProxyAdmission"/>).</item>
/// </list>
/// A failed pull keeps its page in memory rather than calling <see cref="ZabbixStore{T}.Release"/> and re-leasing, since
/// leasing it again would map it against the configuration at retry time and could change its values.
/// <para><see cref="CreateBuilder"/> builds one, and <c>AddZabbixPassiveProxy</c> registers one in an application's host,
/// with the same verbs (<see cref="ZabbixPassiveProxyBuilderExtensions"/>, <see cref="ZabbixProxyBuilderExtensions"/>, the
/// store's and the listener's). A proxy constructed directly reads its <see cref="Options"/> when it starts.</para>
/// </remarks>
/// <example>
/// <code language="csharp">
/// await using ZabbixPassiveProxy proxy = ZabbixPassiveProxy.CreateBuilder("proxy-dmz")
///     .WithVersion("7.4")
///     .AllowPeer(IPNetwork.Parse("10.0.0.0/8"))
///     .Build();
/// await proxy.StartAsync();
/// </code>
/// </example>
public class ZabbixPassiveProxy : ZabbixComponent
{
    private int _released;
    private readonly Func<ZabbixStorePolicy<ZabbixValue>, ZabbixStoreServices, ZabbixStore<ZabbixValue>>? _history;
    private readonly IConnectionListenerFactory? _transport;
    private readonly ILogger _logger;
    private readonly IMeterFactory? _meters;
    private readonly ZabbixProxyTelemetry _telemetry;
    private readonly ZabbixAgentSessions _agentSessions;                // agents' value ids, de-duplicated per session
    // A session per process, as the upstream proxy's token: 32 hex digits, the only length the server accepts.
    private readonly string _session = Convert.ToHexStringLower(RandomNumberGenerator.GetBytes(16));
    private ZabbixProxyAdmission _admission = ZabbixProxyAdmission.Empty;
    private ZabbixProxyStores? _stores;
    private ZabbixListener? _listener;
    // The server pulls one page at a time; a second pull waits for the first, as upstream's LOCK_PROXY_HISTORY makes it.
    // The queue is bounded: a server pulls one page at a time, so more than a handful waiting is not a server; past it a
    // pull is closed unanswered (the server tries again on its next ProxyDataFrequency tick).
    private readonly ConcurrencyLimiter _paging = new(new ConcurrencyLimiterOptions { PermitLimit = 1, QueueLimit = MaxPullsWaiting, QueueProcessingOrder = QueueProcessingOrder.OldestFirst });

    /// <summary>How many <c>proxy data</c> or <c>proxy tasks</c> pulls may wait for the one in progress.</summary>
    internal const int MaxPullsWaiting = 16;
    // What the builder's OnConfig and OnStats gave, read with the rest of the options at start.
    private ZabbixPassiveProxyConfigCallback? _onConfig;
    private ZabbixProxyStatsCallback? _onStats;
    private int _configuring;

    /// <summary>A proxy to set up through <see cref="Options"/>; <see cref="ZabbixProxyOptions.Name"/> is required by the start.</summary>
    public ZabbixPassiveProxy()
        : this(new ZabbixProxyOptions())
    {
    }

    /// <summary>A proxy as <paramref name="options"/> says.</summary>
    /// <param name="options">The proxy's options, read when it starts; <see cref="ZabbixProxyOptions.Name"/> is required by the start.</param>
    /// <exception cref="ArgumentNullException"><paramref name="options"/> is <see langword="null"/>.</exception>
    public ZabbixPassiveProxy(ZabbixProxyOptions options)
        : this(options, null, null, null)
    {
    }

    /// <summary>A proxy as <paramref name="options"/> says, with the application's logging, clock and meters.</summary>
    /// <param name="options">The proxy's options, read when it starts; <see cref="ZabbixProxyOptions.Name"/> is required by the start.</param>
    /// <param name="loggers">Where to log; nowhere when <see langword="null"/>. The caller keeps owning it.</param>
    /// <param name="time">The clock; the system's when <see langword="null"/>.</param>
    /// <param name="meters">The meter factory to count with; the proxy owns its meter when <see langword="null"/>.</param>
    /// <exception cref="ArgumentNullException"><paramref name="options"/> is <see langword="null"/>.</exception>
    public ZabbixPassiveProxy(ZabbixProxyOptions options, ILoggerFactory? loggers, TimeProvider? time, IMeterFactory? meters)
        : this(null, options, null, null, loggers, time, meters)
    {
    }

    // Over a transport a test gave it.
    internal ZabbixPassiveProxy(string? name, ZabbixProxyOptions options, IConnectionListenerFactory transport, ILoggerFactory loggers, TimeProvider time, IMeterFactory? meters = null)
        : this(name, options, history: null, transport, loggers, time, meters)
    {
    }

    /// <summary>A registration's proxy: its name and the container's transport and store.</summary>
    /// <param name="name">The proxy's name as the server knows it.</param>
    /// <param name="options">The proxy's options.</param>
    /// <param name="history">Builds the history store; the in-memory store when <see langword="null"/>.</param>
    /// <param name="transport">The connection layer.</param>
    /// <param name="loggers">Where to log.</param>
    /// <param name="time">The clock.</param>
    /// <param name="meters">Where to count.</param>
    internal ZabbixPassiveProxy(string? name, ZabbixProxyOptions options, Func<ZabbixStorePolicy<ZabbixValue>, ZabbixStoreServices, ZabbixStore<ZabbixValue>>? history,
        IConnectionListenerFactory? transport, ILoggerFactory? loggers, TimeProvider? time, IMeterFactory? meters)
        : base(name, loggers, time)
    {
        ArgumentNullException.ThrowIfNull(options);
        Options = options;
        _history = history;
        _transport = transport;
        _logger = Loggers.CreateLogger<ZabbixPassiveProxy>();
        _meters = meters;
        _telemetry = new ZabbixProxyTelemetry(meters, Time);
        _agentSessions = new ZabbixAgentSessions(Time);
    }

    /// <summary>
    /// Starts a builder for the passive proxy named <paramref name="name"/>: the server's version, who may reach it, its store,
    /// what answers the server's requests, and its logging.
    /// </summary>
    /// <param name="name">The proxy's name as the server knows it.</param>
    /// <returns>A builder whose <see cref="ZabbixPassiveProxyBuilder.Build"/> returns the proxy.</returns>
    /// <exception cref="ArgumentException"><paramref name="name"/> is empty.</exception>
    /// <exception cref="ArgumentNullException"><paramref name="name"/> is <see langword="null"/>.</exception>
    /// <example>
    /// <code language="csharp">
    /// await using ZabbixPassiveProxy proxy = ZabbixPassiveProxy.CreateBuilder("proxy-dmz")
    ///     .WithVersion("7.4")
    ///     .AllowPeer(IPNetwork.Parse("10.0.0.0/8"))
    ///     .WithPsk("proxy-dmz", key)
    ///     .Build();
    /// </code>
    /// </example>
    public static ZabbixPassiveProxyBuilder CreateBuilder(string name) => new(name);

    private ZabbixComponentVersion ProtocolVersion => Options.ProtocolVersion;

    /// <summary>What the proxy reads when it starts.</summary>
    public ZabbixProxyOptions Options { get; }

    /// <inheritdoc/>
    private protected override string DefaultName => Options.Name ?? string.Empty;

    /// <inheritdoc/>
    private protected override void CheckReady()
    {
        if (string.IsNullOrWhiteSpace(Name)) throw new InvalidOperationException("The proxy cannot start without a name: set Options.Name to its name as the server knows it.");
        if (Options.Version is null) throw new InvalidOperationException($"Passive proxy '{Name}': {ZabbixProxyOptions.PassiveVersionRequired}");
        ZabbixOptionsCheck.RequireValid(Name, Options, new ZabbixProxyOptionsValidator(), new ZabbixProxyProtocolValidator());
    }

    /// <summary>The session the server de-duplicates this process's pages on.</summary>
    public string Session => _session;

    /// <summary>The configuration held now.</summary>
    public ZabbixProxyConfig Config => Volatile.Read(ref _admission).Config;

    /// <summary>The admission rule in force.</summary>
    public ZabbixProxyAdmission Admission => Volatile.Read(ref _admission);

    /// <summary>The listener; <see langword="null"/> before start.</summary>
    public ZabbixListener? Listener => _listener;

    /// <summary>The store; <see langword="null"/> before start and after stop.</summary>
    public ZabbixStore<ZabbixValue>? Store => _stores?.History;

    /// <summary>Where a caller writes discovery check results to be sent as <c>discovery data</c>; <see langword="null"/> before start and after stop.</summary>
    public ZabbixStore<ZabbixProxyDiscoveryItem>? DiscoveryStore => _stores?.Discovery;

    /// <summary>Where a caller writes unknown active-agent hosts to be sent as <c>auto registration</c>; <see langword="null"/> before start and after stop.</summary>
    public ZabbixStore<ZabbixProxyAutoregistrationItem>? AutoregistrationStore => _stores?.Autoregistration;

    /// <summary>Where a caller writes interface availability changes to be sent as <c>interface availability</c>; <see langword="null"/> before start and after stop.</summary>
    public ZabbixStore<ZabbixProxyAvailabilityItem>? AvailabilityStore => _stores?.Availability;

    /// <summary>Where a caller writes task results to be sent as <c>tasks</c>; <see langword="null"/> before start and after stop.</summary>
    public ZabbixStore<ZabbixProxyTaskResult>? TasksStore => _stores?.Tasks;

    private protected override Task OnStartingAsync(CancellationToken cancellationToken) => PrepareAsync(cancellationToken);

    private async Task<ZabbixListener> PrepareAsync(CancellationToken cancellationToken)
    {
        if (_listener is { } prepared) return prepared;
        ZabbixStorePolicy<ZabbixValue> policy = Options.StoragePolicy(value => Admission.Knows(value));
        ZabbixProxyStores stores = ZabbixProxyStores.Create(Name, Options, _history, policy, Loggers, _meters, Time);
        _stores = stores;
        // What the builder's AddProvider, OnTask, OnConfig and OnStats gave, read with the rest of the options at start.
        IReadOnlyList<ZabbixProxyDataCallback> providers = [.. Options.Providers];
        ZabbixProxyTaskCallback? onTask = Options.TaskHandler;
        _onConfig = Options.ConfigHandler;
        _onStats = Options.StatsHandler;
        var sender = new ZabbixProxyDataSender(Name, stores, Options.PageSize, () => Config, () => providers, () => onTask, _telemetry, _logger, Time, status: null);
        // The stores are handed to the conversation, never read back from a field that stop may have cleared.
        _listener = new ZabbixListener(Options.Trapper, (conversation, cancellation) => ConverseAsync(stores, sender, conversation, cancellation), _transport ?? ZabbixSocketTransport.Create(Loggers), Time,
            Loggers.CreateLogger<ZabbixListener>(), _meters);
        ZabbixListener listener = _listener;
        await listener.StartingAsync(cancellationToken).ConfigureAwait(false);
        return listener;
    }

    private protected override async Task OnStartAsync(CancellationToken cancellationToken)
    {
        ZabbixListener listener = await PrepareAsync(cancellationToken).ConfigureAwait(false);
        await listener.StartAsync(cancellationToken).ConfigureAwait(false);
    }

    private protected override Task OnStartedAsync(CancellationToken cancellationToken) => _listener?.StartedAsync(cancellationToken) ?? Task.CompletedTask;

    private protected override Task OnStoppingAsync(CancellationToken cancellationToken) => _listener?.StoppingAsync(cancellationToken) ?? Task.CompletedTask;

    private protected override async Task OnStopAsync(CancellationToken cancellationToken)
    {
        if (_listener is not null) await _listener.StopAsync(cancellationToken).ConfigureAwait(false);
        _stores?.Complete();
    }

    private protected override async Task OnStoppedAsync(CancellationToken cancellationToken)
    {
        if (_listener is not null) await _listener.StoppedAsync(cancellationToken).ConfigureAwait(false);
        if (Interlocked.Exchange(ref _stores, null) is { } stores) await stores.DisposeAsync().ConfigureAwait(false);
    }

    /// <inheritdoc/>
#pragma warning disable IDE1006 // DisposeAsyncCore is the name the dispose pattern gives this method
    protected override async ValueTask DisposeAsyncCore()
#pragma warning restore IDE1006
    {
        await base.DisposeAsyncCore().ConfigureAwait(false);          // stops a running component first, so it drains
        if (Interlocked.Exchange(ref _released, 1) != 0) return;
        if (_listener is not null) await _listener.DisposeAsync().ConfigureAwait(false);
        if (_stores is not null) await _stores.DisposeAsync().ConfigureAwait(false);
        await _paging.DisposeAsync().ConfigureAwait(false);
        _telemetry.Dispose();
    }

    /// <inheritdoc/>
    protected override void Dispose(bool disposing)
    {
        if (disposing && Interlocked.Exchange(ref _released, 1) == 0)
        {
            _listener?.Dispose();
            _stores?.Dispose();
            _paging.Dispose();
            _telemetry.Dispose();
        }
        base.Dispose(disposing);
    }


    private async ValueTask ConverseAsync(ZabbixProxyStores stores, ZabbixProxyDataSender sender, ZabbixConversationContext conversation, CancellationToken cancellationToken)
    {
        string kind = ZabbixProxyPaging.KindOf(conversation.Request.Payload);
        switch (kind)
        {
            case "proxy config":
                await ConfigureAsync(conversation, cancellationToken).ConfigureAwait(false);
                break;
            case "proxy data":
                await PageAsync(sender, conversation, cancellationToken).ConfigureAwait(false);
                break;
            case "proxy tasks":
                await TasksAsync(sender, conversation, cancellationToken).ConfigureAwait(false);
                break;
            case "active checks":
                await conversation.WriteAsync(ZabbixProxyPaging.ActiveChecks(conversation.Request, Config), cancellationToken).ConfigureAwait(false);
                break;
            case "zabbix.stats":
                await conversation.WriteAsync(await StatsAsync(conversation.Request, cancellationToken).ConfigureAwait(false), cancellationToken).ConfigureAwait(false);
                break;
            default:
                ReadOnlyMemory<byte> reply = await ZabbixProxyPaging.TrapAsync(conversation.Request, kind, Admission, _agentSessions, stores.History, Name, Time, Loggers, _meters, _logger, _telemetry, cancellationToken).ConfigureAwait(false);
                if (reply.Length > 0) await conversation.WriteAsync(reply, cancellationToken).ConfigureAwait(false);
                break;
        }
    }

    // ----- proxy config: revisions out, configuration in, success out ---------------------------------------------------

    private async ValueTask ConfigureAsync(ZabbixConversationContext conversation, CancellationToken cancellationToken)
    {
        string? peer = conversation.Request.Peer?.ToString();
        if (Interlocked.Exchange(ref _configuring, 1) != 0)
        {
            await conversation.WriteAsync(Failed("configuration sync is already in progress"), cancellationToken).ConfigureAwait(false);   // proxyconfigwrite.c's answer
            return;
        }
        ZabbixOperation operation = _telemetry.StartConfig(Name, passive: true, conversation.Request.Peer);
        try
        {
            ZabbixProxyAdmission held = Admission;
            string version = ProtocolVersion.ToString();
            var state = new ZabbixProxyConfigState(version, _session, held.Config.ConfigRevision, 0, held.Config.HostmapRevision is 0 ? null : held.Config.HostmapRevision);
            await conversation.WriteAsync(JsonSerializer.SerializeToUtf8Bytes(state, ZabbixProxyJson.Default.ZabbixProxyConfigState), cancellationToken).ConfigureAwait(false);

            ReadOnlyMemory<byte> frame = await conversation.ReadLargeAsync(cancellationToken).ConfigureAwait(false);   // zbx_tcp_recv_ext(…, ZBX_TCP_LARGE)
            ZabbixProxyConfig merged;
            bool fullSync;
            try
            {
                using JsonDocument document = JsonDocument.Parse(frame);
                merged = held.Config.Merge(document.RootElement);
                fullSync = ZabbixProxyConfig.IsFullSync(document.RootElement);
            }
            catch (JsonException invalid)
            {
                // Logged before the reply is written: a test (or a dashboard tailing the log) that observes the "failed"
                // ack over the wire must already be able to see why (no race between the peer's own
                // observation and the log it is waiting on).
                ProxyLog.ProxyConversationFailed(_logger, Name, "proxy config", peer, $"the configuration is not valid: {invalid.Message}", invalid);
                await conversation.WriteAsync(Failed(invalid.Message), cancellationToken).ConfigureAwait(false);
                _telemetry.Finish(operation, invalid);
                return;
            }
            if (!ReferenceEquals(merged, held.Config))
            {
                if (_onConfig is { } onConfig)
                {
                    string? veto = await onConfig(merged, held.Config, cancellationToken).ConfigureAwait(false);
                    if (veto is not null)
                    {
                        ProxyLog.ProxyConfigVetoed(_logger, Name, merged.ConfigRevision, veto);
                        await conversation.WriteAsync(Failed(veto), cancellationToken).ConfigureAwait(false);
                        _telemetry.Finish(operation, errorType: "vetoed");
                        return;
                    }
                }
                Volatile.Write(ref _admission, await ZabbixProxyAdmission.BuildAsync(merged, ProtocolVersion, Options.Resolve, cancellationToken).ConfigureAwait(false));
                ProxyLog.ProxyConfigReceived(_logger, Name, merged.ConfigRevision, fullSync, merged.Hosts.Count, merged.Items.Count);
            }
            await conversation.WriteAsync(JsonSerializer.SerializeToUtf8Bytes(new ZabbixProxyResponse("success", null, null, version, null), ZabbixProxyJson.Default.ZabbixProxyResponse), cancellationToken).ConfigureAwait(false);
            _telemetry.Finish(operation, extra: new TagList { { "zabbix.proxy.config_revision", merged.ConfigRevision } });
        }
        catch (Exception failure) when (ZabbixProxyPaging.IsTransient(failure, cancellationToken))
        {
            ProxyLog.ProxyConversationFailed(_logger, Name, "proxy config", peer, failure.Message, failure);
            _telemetry.Finish(operation, failure);
        }
        finally
        {
            Volatile.Write(ref _configuring, 0);
        }
    }

    private static byte[] Failed(string info) => JsonSerializer.SerializeToUtf8Bytes(new ZabbixProxyResponse("failed", info, null, null, null), ZabbixProxyJson.Default.ZabbixProxyResponse);

    // ----- zabbix.stats: the data object out, whatever the registered provider answers ---------------------------------

    /// <summary>
    /// Answers <c>zabbix.stats</c> (<c>zbxtrapper/trapper.c</c>'s stats family). This SDK keeps no internal counters of
    /// its own to report — unlike the upstream C proxy, whose <c>zbx_zabbix_stats_get</c> reads process-table gauges this
    /// managed host has no equivalent of — so the reply's <c>data</c> object is entirely what the builder's <c>OnStats</c>
    /// answers; with none the proxy still answers <c>success</c> with an empty object, as a real proxy does for a
    /// permitted caller it has nothing to say to.
    /// </summary>
    private async ValueTask<byte[]> StatsAsync(ZabbixRequest request, CancellationToken cancellationToken)
    {
        IReadOnlyDictionary<string, JsonElement>? data = null;
        if (_onStats is { } provider)
        {
            try
            {
                data = await provider(request.Payload, cancellationToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                throw;
            }
            catch (Exception failure) when (failure is not OutOfMemoryException)
            {
                ProxyLog.ProxyStatsProviderFailed(_logger, Name, failure);
            }
        }
        using var stream = new MemoryStream();
        using (var writer = new Utf8JsonWriter(stream))
        {
            writer.WriteStartObject();
            writer.WriteString("response"u8, "success"u8);
            writer.WritePropertyName("data"u8);
            writer.WriteStartObject();
            if (data is not null)
            {
                foreach ((string key, JsonElement value) in data)
                {
                    writer.WritePropertyName(key);
                    value.WriteTo(writer);
                }
            }
            writer.WriteEndObject();
            writer.WriteEndObject();
        }
        return stream.ToArray();
    }

    // ----- proxy data: a page out, the acknowledgement in ------------------------------------------------------------

    private async ValueTask PageAsync(ZabbixProxyDataSender sender, ZabbixConversationContext conversation, CancellationToken cancellationToken)
    {
        string? peer = conversation.Request.Peer?.ToString();
        using RateLimitLease turn = await _paging.AcquireAsync(1, cancellationToken).ConfigureAwait(false);
        if (!turn.IsAcquired)
        {
            ProxyLog.ProxyConversationFailed(_logger, Name, ZabbixProxyPaging.KindOf(conversation.Request.Payload), peer, $"more than {MaxPullsWaiting} pulls are waiting for the one in progress");
            return;
        }
        ZabbixOperation operation = _telemetry.StartData(Name, passive: true, conversation.Request.Peer);
        ZabbixProxyResponse? acknowledged = null;
        int values = 0;
        try
        {
            await sender.PullAsync(async (page, more, delay, cancellation) =>
            {
                DateTimeOffset now = Time.GetUtcNow();
                var request = new ZabbixProxyDataRequest(null, null, _session, page.History, page.Discovery, page.Autoregistration, page.Availability, page.Tasks,
                    more ? 1 : null, ProtocolVersion.ToString(), now, delay > 0 ? delay : null);
                await conversation.WriteAsync(JsonSerializer.SerializeToUtf8Bytes(request, ZabbixProxyJson.Default.ZabbixProxyDataRequest), cancellation).ConfigureAwait(false);
                ZabbixProxyResponse ack = JsonSerializer.Deserialize((await conversation.ReadAsync(cancellation).ConfigureAwait(false)).Span, ZabbixProxyJson.Default.ZabbixProxyResponse)
                    ?? throw new JsonException("The server acknowledged proxy data with null.");
                values = page.History?.Count ?? 0;
                if (_logger.IsEnabled(LogLevel.Debug))
                    ProxyLog.ProxyDataSent(_logger, Name, values, page.History is [.., var last] ? last.Sequence : 0, more, ack.Response ?? "none", ack.Upload ?? "unset");
                acknowledged = ack;
                return ack;
            }, cancellationToken).ConfigureAwait(false);
            if (acknowledged is { IsSuccess: true }) _telemetry.Finish(operation, extra: new TagList { { "zabbix.proxy.values", values } });
            else _telemetry.Finish(operation, errorType: "refused");
        }
        catch (Exception failure) when (ZabbixProxyPaging.IsTransient(failure, cancellationToken))
        {
            ProxyLog.ProxyConversationFailed(_logger, Name, "proxy data", peer, failure.Message, failure);   // the page stays leased for the next pull
            _telemetry.Finish(operation, failure);
        }
    }

    // ----- proxy tasks: the task results and the version out, the acknowledgement and its tasks in -------------------------

    /// <summary>
    /// Answers <c>proxy tasks</c> as <c>send_task_data</c> does (<c>trapper_proxy.c</c> 8.0:387): the results leased from the
    /// tasks store (the same lease a <c>proxy data</c> page uses, under the same turn, so a result goes out once), the
    /// version and the clock, without a session; only the server's <c>success</c> takes the results out of the store
    /// (upstream marks its tasks done only then), and the tasks the acknowledgement carries are handed to what the builder's
    /// <c>OnTask</c> gave (<c>zbx_tm_json_deserialize_tasks</c>, <c>zbx_tm_save_tasks</c>).
    /// </summary>
    private async ValueTask TasksAsync(ZabbixProxyDataSender sender, ZabbixConversationContext conversation, CancellationToken cancellationToken)
    {
        string? peer = conversation.Request.Peer?.ToString();
        using RateLimitLease turn = await _paging.AcquireAsync(1, cancellationToken).ConfigureAwait(false);
        if (!turn.IsAcquired)
        {
            ProxyLog.ProxyConversationFailed(_logger, Name, ZabbixProxyPaging.KindOf(conversation.Request.Payload), peer, $"more than {MaxPullsWaiting} pulls are waiting for the one in progress");
            return;
        }
        try
        {
            ZabbixProxyResponse ack = await sender.TaskResultsAsync(async (tasks, cancellation) =>
            {
                ZabbixClock now = Time.GetUtcNow();
                var answer = new ZabbixProxyTasksAnswer(tasks, ProtocolVersion.ToString(), now.Seconds, now.Nanoseconds);
                await conversation.WriteAsync(JsonSerializer.SerializeToUtf8Bytes(answer, ZabbixProxyJson.Default.ZabbixProxyTasksAnswer), cancellation).ConfigureAwait(false);
                return JsonSerializer.Deserialize((await conversation.ReadAsync(cancellation).ConfigureAwait(false)).Span, ZabbixProxyJson.Default.ZabbixProxyResponse)
                    ?? throw new JsonException("The server acknowledged proxy tasks with null.");
            }, cancellationToken).ConfigureAwait(false);
            if (!ack.IsSuccess) ProxyLog.ProxyConversationFailed(_logger, Name, "proxy tasks", peer, ack.Info ?? "the server did not acknowledge the task results");
        }
        catch (Exception failure) when (ZabbixProxyPaging.IsTransient(failure, cancellationToken))
        {
            ProxyLog.ProxyConversationFailed(_logger, Name, "proxy tasks", peer, failure.Message, failure);   // the results stay leased for the next poll
        }
    }
}
