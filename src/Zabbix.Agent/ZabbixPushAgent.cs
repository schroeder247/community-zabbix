// This Source Code Form is subject to the terms of the Mozilla Public
// License, v. 2.0. Copyright (c) 2026 schroeder247. SPDX-License-Identifier: MPL-2.0
using System.Diagnostics.Metrics;
using System.Threading.Channels;
using Community.Zabbix.Hosting;
using Community.Zabbix.Protocol;
using Community.Zabbix.Storage;
using Microsoft.Extensions.Logging;

namespace Community.Zabbix;

/// <summary>
/// An agent that collects values from your own sources on a schedule, keeps each in a store until the server has it, and
/// sends them to trapper items, retrying through outages. It answers no item keys: for that, see
/// <see cref="ZabbixActiveAgent"/>.
/// </summary>
/// <remarks>
/// <para>
/// <see cref="CreateBuilder"/> builds one, and <c>AddZabbixPushAgent</c> registers one in an application's host, with the
/// same verbs (<see cref="ZabbixPushAgentBuilderExtensions"/>). An agent constructed directly takes its options and has no
/// sources: its options are read when it starts.
/// </para>
/// <para>
/// A stop stops the producing sources (a poll), lets the pump finish, within <see cref="ZabbixSendOptions.DrainTimeout"/>,
/// what it holds and what a source that ends by completion still has (a trapper's edge: every value in it was reported
/// processed to its sender, and the trapper, stopped first, completes it), which completes the store; it then stops the
/// drain, which delivers what the store still holds within a second <see cref="ZabbixSendOptions.DrainTimeout"/>, leasing
/// each batch and acknowledging it once delivered, so a durable store keeps a batch in flight. Only then are the store and
/// the sender disposed.
/// </para>
/// </remarks>
/// <example>
/// <code language="csharp">
/// ZabbixItemKey&lt;long&gt; memory = new("app.memory");
/// await using ZabbixPushAgent agent = ZabbixPushAgent.CreateBuilder("web-01")
///     .AddServer("zbx-server-01")
///     .AddPoll(memory, TimeSpan.FromSeconds(30), () =&gt; GC.GetTotalMemory(false))
///     .Build();
/// await agent.StartAsync();
/// </code>
/// </example>
public class ZabbixPushAgent : ZabbixComponent
{
    private readonly IServiceProvider _provider;
    private readonly ILogger _logger;
    private readonly IMeterFactory? _meters;
    private readonly List<Func<IServiceProvider, IAsyncEnumerable<ZabbixValue>>> _sources;
    private readonly Func<IServiceProvider, ZabbixStorePolicy<ZabbixValue>, ZabbixStoreServices, ZabbixStore<ZabbixValue>>? _storage;
    private readonly ZabbixAgentTelemetry _telemetry;
    private readonly ZabbixWireTelemetry? _wire;
    private readonly bool _ownsTelemetry;
    private readonly CancellationTokenSource _stoppingSources = new();
    private readonly CancellationTokenSource _stoppingDrain = new();
    private readonly CancellationTokenSource _abandoningDrain = new();     // closes the drain window at once: disposal, or a host that gave up
    private CancellationTokenSource? _abandoning;                       // ends the pump's last reads and writes: the drain window
    private Destination[] _destinations = [];
    private IReadOnlyList<ZabbixComponentStatus> _statuses;
    private int _released;
    private ZabbixSourcePump<ZabbixValue>? _pump;
    private Task _pumping = Task.CompletedTask;
    private Task _draining = Task.CompletedTask;
    private int _started;

    /// <summary>An agent to set up through <see cref="Options"/>.</summary>
    public ZabbixPushAgent()
        : this(new ZabbixPushAgentOptions())
    {
    }

    /// <summary>An agent as <paramref name="options"/> says.</summary>
    /// <param name="options">The agent's options, read when it starts.</param>
    /// <exception cref="ArgumentNullException"><paramref name="options"/> is <see langword="null"/>.</exception>
    public ZabbixPushAgent(ZabbixPushAgentOptions options)
        : this(options, null, null, null)
    {
    }

    /// <summary>An agent as <paramref name="options"/> says, with the application's logging, clock and meters.</summary>
    /// <param name="options">The agent's options, read when it starts.</param>
    /// <param name="loggers">Where to log; nowhere when <see langword="null"/>. The caller keeps owning it.</param>
    /// <param name="time">The clock; the system's when <see langword="null"/>.</param>
    /// <param name="meters">The meter factory to count with; the agent owns its meter when <see langword="null"/>.</param>
    /// <exception cref="ArgumentNullException"><paramref name="options"/> is <see langword="null"/>.</exception>
    public ZabbixPushAgent(ZabbixPushAgentOptions options, ILoggerFactory? loggers, TimeProvider? time, IMeterFactory? meters)
        : this(null, options, NoServices.Instance, loggers, time, meters)
    {
    }

    // A registration's: its name, the container its sources and store resolve from, and the container's telemetry.
    internal ZabbixPushAgent(string? name, ZabbixPushAgentOptions options, IServiceProvider provider, ILoggerFactory? loggers, TimeProvider? time,
        IMeterFactory? meters = null, ZabbixAgentTelemetry? telemetry = null, ZabbixWireTelemetry? wire = null)
        : base(name, loggers, time)
    {
        ArgumentNullException.ThrowIfNull(options);
        Options = options;
        _sources = [.. options.Sources];
        _storage = options.Storage;
        _provider = provider;
        _logger = Loggers.CreateLogger<ZabbixPushAgent>();
        _meters = meters;
        _ownsTelemetry = telemetry is null;
        _telemetry = telemetry ?? new ZabbixAgentTelemetry(meters, Time);
        _wire = wire;
        Status = new ZabbixComponentStatus(Name, _telemetry);
        _statuses = [Status];
    }

    /// <summary>
    /// Starts a builder for an agent that reports for <paramref name="hostName"/>: its sources, its servers, its store, and its
    /// logging.
    /// </summary>
    /// <param name="hostName">
    /// The agent's name, and the host its typed polls and meters report for unless <c>Configure</c> sets another.
    /// </param>
    /// <returns>A builder whose <see cref="ZabbixPushAgentBuilder.Build"/> returns the agent.</returns>
    /// <exception cref="ArgumentException"><paramref name="hostName"/> is empty.</exception>
    /// <exception cref="ArgumentNullException"><paramref name="hostName"/> is <see langword="null"/>.</exception>
    /// <example>
    /// <code language="csharp">
    /// await using ZabbixPushAgent agent = ZabbixPushAgent.CreateBuilder("web-01")
    ///     .AddServer("zbx-server-01")
    ///     .AddMeter("MyApp.Orders")
    ///     .Build();
    /// </code>
    /// </example>
    public static ZabbixPushAgentBuilder CreateBuilder(string hostName) => new(hostName);

    /// <summary>What the agent reads when it starts.</summary>
    public ZabbixPushAgentOptions Options { get; }

    /// <inheritdoc/>
    private protected override string DefaultName => Options.Name ?? Environment.MachineName;

    /// <inheritdoc/>
    private protected override void CheckReady()
        => ZabbixOptionsCheck.RequireValid(Name, Options, new ZabbixPushAgentOptionsValidator(), new ZabbixPushAgentServersValidator());

    /// <summary>What the agent is doing, for its first destination; before it starts, a status with nothing to report.</summary>
    public ZabbixComponentStatus Status { get; private set; }

    /// <summary>What the agent is doing for each destination, each with its own store and delivery accounting; before it starts, one status with nothing to report.</summary>
    public IReadOnlyList<ZabbixComponentStatus> Statuses => Volatile.Read(ref _statuses);

    /// <summary>Each destination's sender, which owns its connection; empty before start and after disposal.</summary>
    internal IReadOnlyList<ZabbixSender> Senders => [.. Volatile.Read(ref _destinations).Select(destination => destination.Sender)];

    /// <summary>One <c>ServerActive</c> destination: a store every value is written to, a sender over its HA nodes, and its accounting.</summary>
    private sealed record Destination(ZabbixStore<ZabbixValue> Store, ZabbixSender Sender, ZabbixComponentStatus Status);

    /// <summary>One destination's store: the one <c>UseStore</c> chose, else in memory; the agent's policy and the host's services either way.</summary>
    private ZabbixStore<ZabbixValue> StoreFor(ZabbixPushAgentOptions agent, string name)
    {
        var policy = new ZabbixStorePolicy<ZabbixValue> { MaxItems = agent.MaxItems ?? ZabbixStorePolicy<ZabbixValue>.DefaultMaxItems, Overflow = agent.Overflow };
        var services = new ZabbixStoreServices { Name = name, LoggerFactory = Loggers, Meters = _meters, Time = Time };
        if (_storage is { } storage) return storage(_provider, policy, services);
        return new ZabbixMemoryStore<ZabbixValue>(policy, services);
    }

    [System.Diagnostics.CodeAnalysis.SuppressMessage("Reliability", "CA2000:Dispose objects before losing scope", Justification = "Each sender is held by its Destination, whose disposal (OnStoppedAsync, OnDisposeAsync, OnDispose) disposes it; a failed start disposes those already built.")]
    private protected override Task OnStartingAsync(CancellationToken cancellationToken)
    {
        ZabbixServerActive serverActive = ZabbixServerActiveValidation.Parse(Options.ServerActive);
        var destinations = new Destination[serverActive.Count];
        for (int i = 0; i < destinations.Length; i++)
        {
            IReadOnlyList<ZabbixServerNode> nodes = serverActive[i];
            string name = destinations.Length == 1 ? Name : $"{Name}[{string.Join(';', nodes)}]";
            ZabbixStore<ZabbixValue> store = StoreFor(Options, name);
            if (destinations.Take(i).Any(earlier => ReferenceEquals(earlier.Store, store)))
                throw new InvalidOperationException($"Agent '{Name}': UseStore returned one store for two destinations; each destination needs its own store, or each would send only a share of the values.");
            // The sender builds the destination's connection over its HA nodes and owns it (a certificate's key among it):
            // disposing the destination disposes the sender. Certificate files that cannot be read fail the start here.
            ZabbixSender sender;
            try { sender = new ZabbixSender(nodes, Options.Connection, Time, Loggers.CreateLogger<ZabbixWireClient>(), _wire); }
            catch
            {
                foreach (Destination built in destinations.Take(i)) built.Sender.Dispose();
                throw;
            }
            var status = new ZabbixComponentStatus(name, _telemetry);
            status.Attach(store);
            destinations[i] = new Destination(store, sender, status);
        }
        Volatile.Write(ref _destinations, destinations);
        Status = destinations[0].Status;
        Volatile.Write(ref _statuses, [.. destinations.Select(destination => destination.Status)]);
        // Every value goes to every destination (upstream: each ServerActive destination has its own buffer).
        ChannelWriter<ZabbixValue> writer = destinations.Length == 1 ? destinations[0].Store : new ZabbixFanOutWriter<ZabbixValue>([.. destinations.Select(destination => destination.Store)]);
        _pump = new ZabbixSourcePump<ZabbixValue>(writer, _sources.Select(source => source(_provider)), Loggers.CreateLogger<ZabbixSourcePump<ZabbixValue>>());
        return Task.CompletedTask;
    }

    private protected override Task OnStartAsync(CancellationToken cancellationToken)
    {
        if (Interlocked.Exchange(ref _started, 1) != 0) return Task.CompletedTask;
        if (_pump is null) throw new System.Diagnostics.UnreachableException("The starting phase builds the pump.");
        ZabbixSendOptions send = Options.Send;
        _draining = Task.WhenAll(_destinations.Select(destination =>
        {
            destination.Status.Started(Time.GetUtcNow());
            return ZabbixAgentLoop.PassAsync(destination.Status.Name, "send", destination.Status, _logger,
                async stopping => await ZabbixSendLoop.RunAsync(destination.Store, destination.Sender.DeliverAsync, new ZabbixSendLoopOptions { Send = send, Time = Time, Logger = Loggers.CreateLogger<ZabbixWireClient>(), Progress = destination.Status }, stopping, _abandoningDrain.Token).ConfigureAwait(false),
                _stoppingDrain.Token);
        }));
        _abandoning = new CancellationTokenSource(Timeout.InfiniteTimeSpan, Time);
        _pumping = _pump.RunAsync(SourceFaulted, ValuesAbandoned, _stoppingSources.Token, _abandoning.Token);
        return Task.CompletedTask;
    }

    private protected override Task OnStartedAsync(CancellationToken cancellationToken) => Task.CompletedTask;

    // The producing sources (a poll) end; a source that ends by completion (a trapper's edge, whose values its senders were
    // told were processed) is read on until its trapper stops, which happens before this agent's stop phase, and the value
    // in hand is written.
    private protected override Task OnStoppingAsync(CancellationToken cancellationToken) => _stoppingSources.CancelAsync();

    private protected override async Task OnStopAsync(CancellationToken cancellationToken)
    {
        // The pump finishes what its sources hold within one drain window; the stores complete when it ends.
        _abandoning?.CancelAfter(Options.Send.DrainTimeout);
        try { await _pumping.WaitAsync(cancellationToken).ConfigureAwait(false); }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            if (_abandoning is not null) await _abandoning.CancelAsync().ConfigureAwait(false);   // the host gave up waiting: so does the pump
            // The abandoned pump ends at once; it completes the stores when it does, so the drain starts after it, and a
            // batch the pump held is counted abandoned there whatever the timing, never returned by a drain already gone.
            await _pumping.ConfigureAwait(ConfigureAwaitOptions.SuppressThrowing);
        }
        // The stores are complete: stopping the drains starts their window, in which each delivers what its store holds.
        await _stoppingDrain.CancelAsync().ConfigureAwait(false);
        try { await _draining.WaitAsync(cancellationToken).ConfigureAwait(false); }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            await _abandoningDrain.CancelAsync().ConfigureAwait(false);                  // the host gave up waiting: so does the drain
        }
    }

    // The drain ends before the store and the sender it uses are disposed; a drain still running is told to stop at once.
    private protected override async Task OnStoppedAsync(CancellationToken cancellationToken)
    {
        if (!_draining.IsCompleted)
        {
            await _stoppingDrain.CancelAsync().ConfigureAwait(false);
            await _abandoningDrain.CancelAsync().ConfigureAwait(false);
        }
        await _draining.ConfigureAwait(ConfigureAwaitOptions.SuppressThrowing);            // faults were logged where they happened
        if (!_pumping.IsCompleted && _abandoning is not null) await _abandoning.CancelAsync().ConfigureAwait(false);
        await _pumping.ConfigureAwait(ConfigureAwaitOptions.SuppressThrowing);            // a late pump never writes into a disposed store
        _pump?.Dispose();
        foreach (Destination destination in Interlocked.Exchange(ref _destinations, []))
        {
            await destination.Store.DisposeAsync().ConfigureAwait(false);
            destination.Sender.Dispose();
        }
    }

    // The synchronous form, for a container disposed synchronously: the loops are cancelled and the stores' disposal is
    // started, not awaited (a memory store's completes at once; a durable store's last commit finishes on its own loop).
    /// <inheritdoc/>
#pragma warning disable IDE1006 // DisposeAsyncCore is the name the dispose pattern gives this method
    protected override async ValueTask DisposeAsyncCore()
#pragma warning restore IDE1006
    {
        if (IsKept) return;                                          // shared: its container releases it
        await base.DisposeAsyncCore().ConfigureAwait(false);          // stops a running component first, so it drains
        if (Interlocked.Exchange(ref _released, 1) != 0) return;
        await _stoppingSources.CancelAsync().ConfigureAwait(false);
        if (_abandoning is not null) await _abandoning.CancelAsync().ConfigureAwait(false);
        await _stoppingDrain.CancelAsync().ConfigureAwait(false);
        await _abandoningDrain.CancelAsync().ConfigureAwait(false);
        // The pump and the drains end before the stores and senders they use are disposed (a write into a disposed store
        // would fault, event 3002; a delivery through a disposed sender would be reported a defect). Faults were logged where they happened.
        await _pumping.ConfigureAwait(ConfigureAwaitOptions.SuppressThrowing);
        await _draining.ConfigureAwait(ConfigureAwaitOptions.SuppressThrowing);
        _pump?.Dispose();
        foreach (Destination destination in Interlocked.Exchange(ref _destinations, []))
        {
            await destination.Store.DisposeAsync().ConfigureAwait(false);
            destination.Sender.Dispose();
        }
        _abandoning?.Dispose();
        _stoppingSources.Dispose();
        _stoppingDrain.Dispose();
        _abandoningDrain.Dispose();
        if (_ownsTelemetry) _telemetry.Dispose();
    }

    /// <inheritdoc/>
    protected override void Dispose(bool disposing)
    {
        if (IsKept) return;                                          // shared: its container releases it
        if (disposing && Interlocked.Exchange(ref _released, 1) == 0)
        {
            _stoppingSources.Cancel();
            _abandoning?.Cancel();
            _stoppingDrain.Cancel();
            _abandoningDrain.Cancel();
            foreach (Destination destination in Interlocked.Exchange(ref _destinations, []))
            {
                ValueTask closing = destination.Store.DisposeAsync();
                if (!closing.IsCompletedSuccessfully) _ = closing.AsTask();
                destination.Sender.Dispose();
            }
            _pump?.Dispose();
            _abandoning?.Dispose();
            _stoppingSources.Dispose();
            _stoppingDrain.Dispose();
            _abandoningDrain.Dispose();
            if (_ownsTelemetry) _telemetry.Dispose();
        }
        base.Dispose(disposing);
    }


    // A source that faults shows on every destination's status (each lost what it would have produced), so the health
    // check says so; it stays until the agent restarts, since the source does not come back.
    private void SourceFaulted(Exception failure)
    {
        foreach (Destination destination in Volatile.Read(ref _destinations)) destination.Status.Faulted("source", failure);
    }

    // Values the pump's drain window closed on never reached any destination's store: each counts them abandoned.
    private void ValuesAbandoned(int count)
    {
        DateTimeOffset now = Time.GetUtcNow();
        foreach (Destination destination in Volatile.Read(ref _destinations)) destination.Status.Report(new ZabbixSendProgress(ZabbixSendOutcome.Abandoned, count, now));
    }

    private sealed class NoServices : IServiceProvider
    {
        public static NoServices Instance { get; } = new();

        public object? GetService(Type serviceType) => null;
    }
}
