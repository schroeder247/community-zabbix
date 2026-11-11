// This Source Code Form is subject to the terms of the Mozilla Public
// License, v. 2.0. Copyright (c) 2026 schroeder247. SPDX-License-Identifier: MPL-2.0
using System.Diagnostics.Metrics;
using Community.Zabbix.Hosting;
using Community.Zabbix.Items;
using Community.Zabbix.Protocol;
using Microsoft.Extensions.Logging;

namespace Community.Zabbix;

/// <summary>
/// A Zabbix active agent: it asks its server which item keys to collect, answers each on its own schedule from
/// <see cref="Items"/>, and sends the values. Each destination of <see cref="ZabbixActiveAgentOptions.ServerActive"/> has
/// its own item list, session, buffer and heartbeat.
/// </summary>
/// <remarks>
/// <see cref="CreateBuilder"/> builds one, and <c>AddZabbixActiveAgent</c> registers one in an application's host, with the
/// same verbs (<see cref="ZabbixActiveAgentBuilderExtensions"/> and the item verbs). An agent constructed directly reads its
/// <see cref="Options"/> when it starts. <see cref="Items"/> is live: a key added while the agent runs is answered from the
/// next check on.
/// </remarks>
/// <example>
/// <code language="csharp">
/// await using ZabbixActiveAgent agent = ZabbixActiveAgent.CreateBuilder("web-01")
///     .AddServer("zbx-server-01")
///     .AddItem("app.version", _ =&gt; "2.4.1")
///     .Build();
/// await agent.StartAsync();
/// </code>
/// </example>
public class ZabbixActiveAgent : ZabbixComponent
{
    private readonly IMeterFactory? _meters;
    private readonly ZabbixAgentTelemetry? _containerTelemetry;
    private readonly ZabbixWireTelemetry? _wire;
    private readonly ZabbixItemCollector? _containerCollector;
    private readonly ZabbixComponentStatus _notStarted;
    private ZabbixActiveAgentDestination[] _destinations = [];
    private IReadOnlyList<ZabbixComponentStatus> _statuses;
    private ZabbixItemCollector? _collector;    // this agent's own, when no container gave one
    private ZabbixAgentTelemetry? _telemetry;   // this agent's own, when no container gave one
    private int _released;

    /// <summary>An active agent to set up through <see cref="Options"/> and <see cref="Items"/>.</summary>
    public ZabbixActiveAgent()
        : this(new ZabbixActiveAgentOptions())
    {
    }

    /// <summary>An active agent as <paramref name="options"/> says.</summary>
    /// <param name="options">The agent's options, read when it starts.</param>
    /// <exception cref="ArgumentNullException"><paramref name="options"/> is <see langword="null"/>.</exception>
    public ZabbixActiveAgent(ZabbixActiveAgentOptions options)
        : this(options, null, null, null)
    {
    }

    /// <summary>An active agent as <paramref name="options"/> says, with the application's logging, clock and meters.</summary>
    /// <param name="options">The agent's options, read when it starts.</param>
    /// <param name="loggers">Where to log; nowhere when <see langword="null"/>. The caller keeps owning it.</param>
    /// <param name="time">The clock, and through its <see cref="TimeProvider.LocalTimeZone"/> the zone delays are read in; the system's when <see langword="null"/>.</param>
    /// <param name="meters">The meter factory to count with; the agent owns its meter when <see langword="null"/>.</param>
    /// <exception cref="ArgumentNullException"><paramref name="options"/> is <see langword="null"/>.</exception>
    public ZabbixActiveAgent(ZabbixActiveAgentOptions options, ILoggerFactory? loggers, TimeProvider? time, IMeterFactory? meters)
        : this(null, options, loggers, time, meters, null, null, null)
    {
    }

    // A registration's: its name, and the container's telemetry and collector.
    internal ZabbixActiveAgent(string? name, ZabbixActiveAgentOptions options, ILoggerFactory? loggers, TimeProvider? time, IMeterFactory? meters, ZabbixAgentTelemetry? telemetry, ZabbixWireTelemetry? wire,
        ZabbixItemCollector? collector)
        : base(name, loggers, time)
    {
        ArgumentNullException.ThrowIfNull(options);
        Options = options;
        _meters = meters;
        _containerTelemetry = telemetry;
        _wire = wire;
        _containerCollector = collector;
        Items = new ZabbixItemDictionary { Owner = this };
        _notStarted = new ZabbixComponentStatus(Name);
        _statuses = [_notStarted];
    }

    /// <summary>
    /// Starts a builder for the agent of <paramref name="hostName"/>: its servers, its items, and its logging.
    /// </summary>
    /// <param name="hostName">The agent's name, and the host as the server knows it unless <c>Configure</c> sets another.</param>
    /// <returns>A builder whose <see cref="ZabbixActiveAgentBuilder.Build"/> returns the agent.</returns>
    /// <exception cref="ArgumentException"><paramref name="hostName"/> is empty.</exception>
    /// <exception cref="ArgumentNullException"><paramref name="hostName"/> is <see langword="null"/>.</exception>
    /// <example>
    /// <code language="csharp">
    /// await using ZabbixActiveAgent agent = ZabbixActiveAgent.CreateBuilder("web-01")
    ///     .AddServer("zbx-server-01")
    ///     .AddItem(pending, () =&gt; queue.Count)
    ///     .Build();
    /// </code>
    /// </example>
    public static ZabbixActiveAgentBuilder CreateBuilder(string hostName) => new(hostName);

    /// <summary>What the agent reads when it starts.</summary>
    public ZabbixActiveAgentOptions Options { get; }

    /// <summary>
    /// The keys this agent answers, every destination alike: what you add, and from the start the keys every agent answers
    /// and the bundled items (<see cref="ZabbixActiveAgentOptions.BundledItems"/>) under every name you left free.
    /// </summary>
    public ZabbixItemDictionary Items { get; }

    /// <summary>What the agent is doing, for its first destination.</summary>
    public ZabbixComponentStatus Status => _statuses[0];

    /// <summary>What the agent is doing for each destination, in <see cref="ZabbixActiveAgentOptions.ServerActive"/> order; before it starts, one status with nothing to report.</summary>
    public IReadOnlyList<ZabbixComponentStatus> Statuses => Volatile.Read(ref _statuses);

    /// <inheritdoc/>
    private protected override string DefaultName => Options.Name ?? Options.HostName;

    /// <summary>Each destination's own loop, for what a test reads of it.</summary>
    internal IReadOnlyList<ZabbixActiveAgentDestination> Sessions => _destinations;

    /// <inheritdoc/>
#pragma warning disable IDE1006 // DisposeAsyncCore is the name the dispose pattern gives this method
    protected override async ValueTask DisposeAsyncCore()
#pragma warning restore IDE1006
    {
        if (IsKept) return;                                          // shared: its container releases it
        await base.DisposeAsyncCore().ConfigureAwait(false);          // stops a running component first, so it drains
        if (Interlocked.Exchange(ref _released, 1) != 0) return;
        foreach (ZabbixActiveAgentDestination destination in _destinations) await destination.DisposeAsync().ConfigureAwait(false);
        await Items.ReleaseAsync().ConfigureAwait(false);
        _collector?.Dispose();
        _telemetry?.Dispose();
    }

    /// <inheritdoc/>
    protected override void Dispose(bool disposing)
    {
        if (IsKept) return;                                          // shared: its container releases it
        if (disposing && Interlocked.Exchange(ref _released, 1) == 0)
        {
            foreach (ZabbixActiveAgentDestination destination in _destinations) destination.Dispose();
            Items.ReleaseAll();
            _collector?.Dispose();
            _telemetry?.Dispose();
        }
        base.Dispose(disposing);
    }

    /// <inheritdoc/>
    private protected override void CheckReady() => ZabbixOptionsCheck.RequireValid(Name, Options, new ZabbixActiveAgentOptionsValidator(), new ZabbixActiveAgentServersValidator());

    /// <inheritdoc/>
    private protected override Task OnStartingAsync(CancellationToken cancellationToken)
    {
        ZabbixItemCollector? collector = _containerCollector;
        if (collector is null && Options.BundledItems) collector = _collector = new ZabbixItemCollector(ZabbixHostFiles.Host, Time);
        Items.KeyRules = ZabbixKeyRules.Parse(Options.KeyRules);
        Items.AddDefaults(Options.HostName, Options.HostMetadata, Options.BundledItems, collector);
        ZabbixAgentTelemetry telemetry = _containerTelemetry ?? (_telemetry = new ZabbixAgentTelemetry(_meters, Time));
        int count = Math.Max(ZabbixServerActiveValidation.Parse(Options.ServerActive).Count, 1);
        string name = Name;
        _destinations = [.. Enumerable.Range(0, count).Select(i => new ZabbixActiveAgentDestination(name, Options, Items, Loggers, Time, session: null, _meters, i, telemetry, _wire))];
        Volatile.Write(ref _statuses, [.. _destinations.Select(destination => destination.Status)]);
        return Task.WhenAll(_destinations.Select(destination => destination.StartingAsync(cancellationToken)));
    }

    /// <inheritdoc/>
    private protected override Task OnStartAsync(CancellationToken cancellationToken) => Task.WhenAll(_destinations.Select(destination => destination.StartAsync(cancellationToken)));

    /// <inheritdoc/>
    private protected override Task OnStartedAsync(CancellationToken cancellationToken) => Task.WhenAll(_destinations.Select(destination => destination.StartedAsync(cancellationToken)));

    /// <inheritdoc/>
    private protected override Task OnStoppingAsync(CancellationToken cancellationToken) => Task.WhenAll(_destinations.Select(destination => destination.StoppingAsync(cancellationToken)));

    /// <inheritdoc/>
    private protected override Task OnStopAsync(CancellationToken cancellationToken) => Task.WhenAll(_destinations.Select(destination => destination.StopAsync(cancellationToken)));

    /// <inheritdoc/>
    private protected override Task OnStoppedAsync(CancellationToken cancellationToken) => Task.WhenAll(_destinations.Select(destination => destination.StoppedAsync(cancellationToken)));
}
