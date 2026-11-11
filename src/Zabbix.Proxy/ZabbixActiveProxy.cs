// This Source Code Form is subject to the terms of the Mozilla Public
// License, v. 2.0. Copyright (c) 2026 schroeder247. SPDX-License-Identifier: MPL-2.0
using System.Diagnostics.Metrics;
using System.Globalization;
using Community.Zabbix.Hosting;
using Community.Zabbix.Listening;
using Community.Zabbix.Protocol;
using Community.Zabbix.Proxy;
using Community.Zabbix.Storage;
using Microsoft.AspNetCore.Connections;
using Microsoft.Extensions.Logging;

namespace Community.Zabbix;

/// <summary>
/// Hosts an active proxy: a trapper into a store, the store uploaded as <c>proxy data</c>, and the configuration
/// pulled on a cadence and merged.
/// </summary>
/// <remarks>
/// <see cref="CreateBuilder"/> builds one, and <c>AddZabbixActiveProxy</c> registers one in an application's host, with the
/// same verbs (<see cref="ZabbixActiveProxyBuilderExtensions"/>, <see cref="ZabbixProxyBuilderExtensions"/>, the store's and
/// the listener's). A proxy constructed directly reads its <see cref="Options"/> when it starts.
/// <para>A trapped value is admitted by Zabbix's own rules (<see cref="ZabbixProxyAdmission"/>) or refused with "failed". As
/// upstream's <c>datasender.c</c>, every <see cref="ZabbixProxyOptions.DataInterval"/> (and at once while there is
/// <c>more</c>) a page is leased from the store and sent with the store's sequences as ids; only <c>success</c>
/// acknowledges it, so the server's <c>last_id</c> de-duplicates a re-send. Caller rows the server can never read are
/// dropped, logged and counted; <c>upload: disabled</c> holds the page (back-pressure). The passive
/// proxy runs the same turn.</para>
/// <para>The loop is its own rather than <c>ZabbixSendLoop</c>, which loses the batch in flight on a crash and has no
/// "hold"; a failed turn is simply the next <see cref="PeriodicTimer"/> tick, as upstream retries.</para>
/// </remarks>
/// <example>
/// <code language="csharp">
/// await using ZabbixActiveProxy proxy = ZabbixActiveProxy.CreateBuilder("proxy-01")
///     .WithServer("zbx-server-01")
///     .AllowPeer(IPNetwork.Parse("10.0.0.0/8"))
///     .Build();
/// await proxy.StartAsync();
/// </code>
/// </example>
public class ZabbixActiveProxy : ZabbixComponent
{
    private int _released;
    private readonly Func<ZabbixStorePolicy<ZabbixValue>, ZabbixStoreServices, ZabbixStore<ZabbixValue>>? _history;
    private readonly IConnectionListenerFactory? _transport;
    private readonly ILogger _logger;
    private readonly IMeterFactory? _meters;
    private readonly ZabbixWireTelemetry? _wire;
    private readonly ZabbixProxyTelemetry _telemetry;
    private readonly ZabbixAgentSessions _agentSessions;                // agents' value ids, de-duplicated per session
    private readonly CancellationTokenSource _stoppingConfig = new();
    private readonly CancellationTokenSource _stoppingUpload = new();
    private ZabbixProxyAdmission _admission = ZabbixProxyAdmission.Empty;
    private Running? _running;                                                             // what StartingAsync built; null before it
    private ZabbixProxyStores? _stores;                                                    // the stores until they are disposed
    private ZabbixListener? _trapper;                                                      // owned: disposed with the host
    private ZabbixNodeLink? _link;                                                         // the uplink's connection, owned
    private Task _pullingConfig = Task.CompletedTask;
    private Task _uploading = Task.CompletedTask;
    private int _started;

    /// <summary>A proxy to set up through <see cref="Options"/>; <see cref="ZabbixProxyOptions.Name"/> is required by the start.</summary>
    public ZabbixActiveProxy()
        : this(new ZabbixProxyOptions())
    {
    }

    /// <summary>A proxy as <paramref name="options"/> says.</summary>
    /// <param name="options">The proxy's options, read when it starts; <see cref="ZabbixProxyOptions.Name"/> is required by the start.</param>
    /// <exception cref="ArgumentNullException"><paramref name="options"/> is <see langword="null"/>.</exception>
    public ZabbixActiveProxy(ZabbixProxyOptions options)
        : this(options, null, null, null)
    {
    }

    /// <summary>A proxy as <paramref name="options"/> says, with the application's logging, clock and meters.</summary>
    /// <param name="options">The proxy's options, read when it starts; <see cref="ZabbixProxyOptions.Name"/> is required by the start.</param>
    /// <param name="loggers">Where to log; nowhere when <see langword="null"/>. The caller keeps owning it.</param>
    /// <param name="time">The clock; the system's when <see langword="null"/>.</param>
    /// <param name="meters">The meter factory to count with; the proxy owns its meter when <see langword="null"/>.</param>
    /// <exception cref="ArgumentNullException"><paramref name="options"/> is <see langword="null"/>.</exception>
    public ZabbixActiveProxy(ZabbixProxyOptions options, ILoggerFactory? loggers, TimeProvider? time, IMeterFactory? meters)
        : this(null, options, null, null, loggers, time, meters)
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
    /// <param name="wire">The framed wire's telemetry from the container, which the uplink's exchanges report to; the process-wide one when <see langword="null"/>.</param>
    internal ZabbixActiveProxy(string? name, ZabbixProxyOptions options, Func<ZabbixStorePolicy<ZabbixValue>, ZabbixStoreServices, ZabbixStore<ZabbixValue>>? history, IConnectionListenerFactory? transport,
        ILoggerFactory? loggers, TimeProvider? time, IMeterFactory? meters = null, ZabbixWireTelemetry? wire = null)
        : base(name, loggers, time)
    {
        ArgumentNullException.ThrowIfNull(options);
        Options = options;
        _history = history;
        _transport = transport;
        _logger = Loggers.CreateLogger<ZabbixActiveProxy>();
        _meters = meters;
        _wire = wire;
        _telemetry = new ZabbixProxyTelemetry(meters, Time);
        _agentSessions = new ZabbixAgentSessions(Time);
        Status = new ZabbixComponentStatus(Name);
    }

    /// <summary>
    /// Starts a builder for the proxy named <paramref name="name"/>: its server, who may reach its trapper, its store, and
    /// its logging.
    /// </summary>
    /// <param name="name">The proxy's name as the server knows it.</param>
    /// <returns>A builder whose <see cref="ZabbixActiveProxyBuilder.Build"/> returns the proxy.</returns>
    /// <exception cref="ArgumentException"><paramref name="name"/> is empty.</exception>
    /// <exception cref="ArgumentNullException"><paramref name="name"/> is <see langword="null"/>.</exception>
    /// <example>
    /// <code language="csharp">
    /// await using ZabbixActiveProxy proxy = ZabbixActiveProxy.CreateBuilder("proxy-01")
    ///     .WithServer("zbx-server-01;zbx-server-02")
    ///     .AllowPeer(IPNetwork.Parse("10.0.0.0/8"))
    ///     .UseEntityFrameworkStore(db =&gt; db.UseSqlite("Data Source=proxy.db"))
    ///     .Build();
    /// </code>
    /// </example>
    public static ZabbixActiveProxyBuilder CreateBuilder(string name) => new(name);

    /// <summary>What the proxy reads when it starts.</summary>
    public ZabbixProxyOptions Options { get; }

    /// <inheritdoc/>
    private protected override string DefaultName => Options.Name ?? string.Empty;

    /// <inheritdoc/>
    private protected override void CheckReady()
    {
        if (string.IsNullOrWhiteSpace(Name)) throw new InvalidOperationException("The proxy cannot start without a name: set Options.Name to its name as the server knows it.");
        ZabbixOptionsCheck.RequireValid(Name, Options, new ZabbixProxyOptionsValidator(), new ZabbixProxyServersValidator(), new ZabbixProxyProtocolValidator());
    }

    /// <summary>The configuration held now.</summary>
    public ZabbixProxyConfig Config => Volatile.Read(ref _admission).Config;

    /// <summary>The admission rule in force: the configuration's, for the protocol version announced.</summary>
    public ZabbixProxyAdmission Admission => Volatile.Read(ref _admission);

    /// <summary>The uplink's connection, which the host owns; <see langword="null"/> before start and after disposal.</summary>
    internal ZabbixNodeLink? Link => Volatile.Read(ref _link);

    /// <summary>The uplink; <see langword="null"/> before start.</summary>
    internal ZabbixProxyUplink? Uplink => _running?.Uplink;

    /// <summary>The trapper's listener; <see langword="null"/> before start.</summary>
    internal ZabbixListener? Trapper => _running?.Trapper;

    /// <summary>The history store; <see langword="null"/> before start and after stop.</summary>
    internal ZabbixStore<ZabbixValue>? Store => _stores?.History;

    /// <summary>
    /// Where a caller writes discovery check results to be uploaded as <c>discovery data</c>; <see langword="null"/> before
    /// start and after stop. Write a result with
    /// <see cref="ZabbixProxyDiscovery.WriteAsync(System.Threading.Channels.ChannelWriter{ZabbixProxyDiscoveryItem}, ZabbixProxyDiscoveredHost, CancellationToken)"/>:
    /// a raw <see cref="ZabbixProxyDiscoveryItem"/> is one row of a protocol (an address's service rows, then the
    /// <c>dcheck</c> 0 row that closes the host), and services written without their closing row wait on the server for ever.
    /// </summary>
    public ZabbixStore<ZabbixProxyDiscoveryItem>? DiscoveryStore => _stores?.Discovery;

    /// <summary>Where a caller writes unknown active-agent hosts to be uploaded as <c>auto registration</c>; <see langword="null"/> before start and after stop.</summary>
    public ZabbixStore<ZabbixProxyAutoregistrationItem>? AutoregistrationStore => _stores?.Autoregistration;

    /// <summary>Where a caller writes interface availability changes to be uploaded as <c>interface availability</c>; <see langword="null"/> before start and after stop.</summary>
    public ZabbixStore<ZabbixProxyAvailabilityItem>? AvailabilityStore => _stores?.Availability;

    /// <summary>Where a caller writes task results (for a task <see cref="ZabbixProxyResponse.Tasks"/> handed over) to be uploaded as <c>tasks</c>; <see langword="null"/> before start and after stop.</summary>
    public ZabbixStore<ZabbixProxyTaskResult>? TasksStore => _stores?.Tasks;

    /// <summary>Whether the server has disabled uploads and the proxy is holding its history.</summary>
    public bool IsHeld => _running?.Sender.IsHeld ?? false;

    /// <summary>What the proxy is doing: its store, its uploads and its faults, as an agent reports them.</summary>
    public ZabbixComponentStatus Status { get; private set; }

    private protected override Task OnStartingAsync(CancellationToken cancellationToken) => PrepareAsync(cancellationToken);

    private async Task<Running> PrepareAsync(CancellationToken cancellationToken)
    {
        if (_running is { } prepared) return prepared;
        ZabbixStorePolicy<ZabbixValue> policy = Options.StoragePolicy(value => Admission.Knows(value));    // a host the server never named is refused at the door
        ZabbixProxyStores stores = ZabbixProxyStores.Create(Name, Options, _history, policy, Loggers, _meters, Time);
        _stores = stores;
        Status = new ZabbixComponentStatus(Name);
        Status.Attach(stores.History);
        // The one server or HA cluster: the start has checked that Server names one.
        IReadOnlyList<ZabbixServerNode> nodes = ZabbixServerActive.Parse(Options.Server!, CultureInfo.InvariantCulture)[0];
        // allowLarge: upstream's proxy receives its configuration with zbx_tcp_recv_large (commshigh.c 8.0:169), up to 16 GiB,
        // as the passive path reads it. The one exchange also carries proxy data, whose replies are small; accepting the
        // large flag on them too is lenient, not wrong, and keeps the uplink one exchange.
        // The host owns the link (a certificate's key among it) and disposes it with itself.
        var link = new ZabbixNodeLink(nodes, Options.Connection, Time, Loggers.CreateLogger<ZabbixProxyUplink>(), _wire);
        Interlocked.Exchange(ref _link, link)?.Dispose();
        var uplink = new ZabbixProxyUplink(link.Exchange(followRedirects: false, allowLarge: true), Name,
            Options.ProtocolVersion, Time, Loggers.CreateLogger<ZabbixProxyUplink>(), session: null, _telemetry);
        _trapper = new ZabbixListener(Options.Trapper, (request, cancellation) => HandleAsync(stores, request, cancellation), _transport ?? ZabbixSocketTransport.Create(Loggers), Loggers, Time, _meters);
        ZabbixListener trapper = _trapper;
        // What the builder's AddProvider and OnTask gave, read with the rest of the options at start.
        IReadOnlyList<ZabbixProxyDataCallback> providers = [.. Options.Providers];
        ZabbixProxyTaskCallback? onTask = Options.TaskHandler;
        var sender = new ZabbixProxyDataSender(Name, stores, Options.PageSize, () => Config, () => providers, () => onTask, _telemetry, _logger, Time, Status);
        var running = new Running(stores, uplink, trapper, sender, providers);
        _running = running;
        await PullConfigAsync(uplink, cancellationToken).ConfigureAwait(false);            // the first configuration before the port opens
        await trapper.StartingAsync(cancellationToken).ConfigureAwait(false);
        return running;
    }

    private protected override async Task OnStartAsync(CancellationToken cancellationToken)
    {
        if (Interlocked.Exchange(ref _started, 1) != 0) return;
        Running running = await PrepareAsync(cancellationToken).ConfigureAwait(false);
        _pullingConfig = ZabbixProxyPaging.RunAsync("configuration", stopping => PullConfigLoopAsync(running.Uplink, stopping), _logger, Name, _stoppingConfig.Token);
        _uploading = ZabbixProxyPaging.RunAsync("upload", stopping => UploadLoopAsync(running, stopping), _logger, Name, _stoppingUpload.Token);
        await running.Trapper.StartAsync(cancellationToken).ConfigureAwait(false);
    }

    private protected override Task OnStartedAsync(CancellationToken cancellationToken) => _running?.Trapper.StartedAsync(cancellationToken) ?? Task.CompletedTask;

    private protected override async Task OnStoppingAsync(CancellationToken cancellationToken)
    {
        if (_running is { } running) await running.Trapper.StoppingAsync(cancellationToken).ConfigureAwait(false);   // no more values arrive
        await _stoppingConfig.CancelAsync().ConfigureAwait(false);
    }

    private protected override async Task OnStopAsync(CancellationToken cancellationToken)
    {
        if (_running is { } running) await running.Trapper.StopAsync(cancellationToken).ConfigureAwait(false);
        _stores?.Complete();
        try { await _uploading.WaitAsync(Options.DrainTimeout, Time, cancellationToken).ConfigureAwait(false); }
        catch (TimeoutException)
        {
            await _stoppingUpload.CancelAsync().ConfigureAwait(false);                    // the drain window passed
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            await _stoppingUpload.CancelAsync().ConfigureAwait(false);                    // the host gave up waiting
        }
        await _uploading.ConfigureAwait(false);
        await _pullingConfig.ConfigureAwait(false);
    }

    private protected override async Task OnStoppedAsync(CancellationToken cancellationToken)
    {
        if (_running is { } running) await running.Trapper.StoppedAsync(cancellationToken).ConfigureAwait(false);
        if (Interlocked.Exchange(ref _stores, null) is { } stores) await stores.DisposeAsync().ConfigureAwait(false);
    }

    /// <inheritdoc/>
#pragma warning disable IDE1006 // DisposeAsyncCore is the name the dispose pattern gives this method
    protected override async ValueTask DisposeAsyncCore()
#pragma warning restore IDE1006
    {
        if (IsKept) return;                                          // shared: its container releases it
        await base.DisposeAsyncCore().ConfigureAwait(false);          // stops a running component first, so it drains
        if (Interlocked.Exchange(ref _released, 1) != 0) return;
        await _stoppingConfig.CancelAsync().ConfigureAwait(false);
        await _stoppingUpload.CancelAsync().ConfigureAwait(false);
        if (_trapper is not null) await _trapper.DisposeAsync().ConfigureAwait(false);
        if (_stores is not null) await _stores.DisposeAsync().ConfigureAwait(false);
        Interlocked.Exchange(ref _link, null)?.Dispose();
        _telemetry.Dispose();
        _stoppingConfig.Dispose();
        _stoppingUpload.Dispose();
    }

    /// <inheritdoc/>
    protected override void Dispose(bool disposing)
    {
        if (IsKept) return;                                          // shared: its container releases it
        if (disposing && Interlocked.Exchange(ref _released, 1) == 0)
        {
            _stoppingConfig.Cancel();
            _stoppingUpload.Cancel();
            _trapper?.Dispose();
            _stores?.Dispose();
            Interlocked.Exchange(ref _link, null)?.Dispose();
            _telemetry.Dispose();
            _stoppingConfig.Dispose();
            _stoppingUpload.Dispose();
        }
        base.Dispose(disposing);
    }


    private ValueTask<ReadOnlyMemory<byte>> HandleAsync(ZabbixProxyStores stores, ZabbixRequest request, CancellationToken cancellationToken)
    {
        string kind = ZabbixProxyPaging.KindOf(request.Payload);
        return kind == "active checks"
            ? ValueTask.FromResult(ZabbixProxyPaging.ActiveChecks(request, Config))
            : ZabbixProxyPaging.TrapAsync(request, kind, Admission, _agentSessions, stores.History, Name, Time, Loggers, _meters, _logger, _telemetry, cancellationToken);
    }

    // ----- configuration --------------------------------------------------------------------------------------------

    private async Task PullConfigLoopAsync(ZabbixProxyUplink uplink, CancellationToken stopping)
    {
        using var timer = new PeriodicTimer(Options.ConfigInterval, Time);
        while (await timer.WaitForNextTickAsync(stopping).ConfigureAwait(false))
            await PullConfigAsync(uplink, stopping).ConfigureAwait(false);
    }

    private async Task PullConfigAsync(ZabbixProxyUplink uplink, CancellationToken cancellationToken)
    {
        ZabbixProxyAdmission held = Admission;
        try
        {
            ZabbixProxyConfig pulled = await uplink.GetConfigAsync(held.Config, cancellationToken).ConfigureAwait(false);
            if (ReferenceEquals(pulled, held.Config) && held.ProtocolVersion == uplink.ProtocolVersion) return;
            Volatile.Write(ref _admission, await ZabbixProxyAdmission.BuildAsync(pulled, uplink.ProtocolVersion, Options.Resolve, cancellationToken).ConfigureAwait(false));
        }
        catch (ZabbixProxyRefusedException)
        {
            // logged (7002) with the server's words by the uplink; the held configuration stays
        }
        catch (Exception failure) when (ZabbixProxyPaging.IsTransient(failure, cancellationToken))
        {
            ProxyLog.ProxyConfigPullFailed(_logger, Name, held.Config.ConfigRevision, failure);   // the held configuration stays
        }
    }

    // ----- history, discovery, auto-registration, interface availability, tasks -----------------------------------------

    private async Task UploadLoopAsync(Running running, CancellationToken stopping)
    {
        using var timer = new PeriodicTimer(Options.DataInterval, Time);
        ZabbixStore<ZabbixValue> store = running.Stores.History;
        ZabbixProxyDataSender sender = running.Sender;
        ZabbixProxyDataSender.Exchange upload = running.Uplink.SendAsync;
        // PeriodicTimer allows exactly one outstanding WaitForNextTickAsync at a time: a second call while one is still
        // pending throws. `tick` is the single call in flight, shared between the "nothing leased yet" wait below and the
        // "wait for the next turn" wait at the loop's end, and is never discarded uncompleted. `waiting` (the store's own
        // SingleReader signal) needs the same discipline: a `tick` that keeps winning (with a registered provider the
        // "just the cadence" `continue` below is never taken) would otherwise call `store.WaitToLeaseAsync` fresh every
        // iteration, leaving two outstanding reads on a SingleReader channel, which hangs inside the channel's own lock
        // rather than throwing. So `waiting` is cached like `tick` and only replaced once awaited to completion.
        Task<bool>? tick = null;
        Task<bool>? waiting = null;
        while (true)
        {
            if (!sender.AnyLeased && !AnyPending())
            {
                // The history store's own signal, raced against the data cadence: a proxy that only ever writes the
                // other kinds (never history) would otherwise wait on a channel that never signals.
                tick ??= timer.WaitForNextTickAsync(stopping).AsTask();
                waiting ??= store.WaitToLeaseAsync(stopping).AsTask();
                Task first = await Task.WhenAny(waiting, tick).ConfigureAwait(false);
                if (first == tick)
                {
                    if (!await tick.ConfigureAwait(false)) return;                          // the timer was disposed: stopping
                    tick = null;
                    if (!AnyPending() && running.Providers.Count == 0) continue;            // just the cadence; nothing new yet
                }
                else
                {
                    bool leaseAvailable = await waiting.ConfigureAwait(false);
                    waiting = null;
                    // History complete (the host is stopping), drained, nothing else waiting: a registered provider computes a
                    // page at send time and holds nothing, so it does not keep a stopping proxy's loop alive.
                    if (!leaseAvailable && !AnyPending()) return;
                }
            }
            bool sendAgainNow = false;
            try
            {
                sendAgainNow = await sender.TurnAsync(upload, stopping).ConfigureAwait(false);
            }
            catch (Exception failure) when (ZabbixProxyPaging.IsTransient(failure, stopping))
            {
                ProxyLog.ProxyUploadFailed(_logger, Name, sender.HistoryLeased, failure);   // the pages stay leased, with their ids
            }
            if (!sendAgainNow)
            {
                tick ??= timer.WaitForNextTickAsync(stopping).AsTask();
                if (!await tick.ConfigureAwait(false)) return;
                tick = null;
            }
        }

        bool AnyPending() => running.Stores.AnyPending();
    }

    // What StartingAsync builds, handed to the loops it starts: nothing after start reads a field that might not be set.
    private sealed record Running(ZabbixProxyStores Stores, ZabbixProxyUplink Uplink, ZabbixListener Trapper, ZabbixProxyDataSender Sender, IReadOnlyList<ZabbixProxyDataCallback> Providers);
}
