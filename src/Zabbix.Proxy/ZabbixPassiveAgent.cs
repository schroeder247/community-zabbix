// This Source Code Form is subject to the terms of the Mozilla Public
// License, v. 2.0. Copyright (c) 2026 schroeder247. SPDX-License-Identifier: MPL-2.0
using System.Diagnostics.Metrics;
using Community.Zabbix.Hosting;
using Community.Zabbix.Items;
using Community.Zabbix.Listening;
using Microsoft.AspNetCore.Connections;
using Microsoft.Extensions.Logging;

namespace Community.Zabbix;

/// <summary>
/// A Zabbix passive agent: it listens on the agent port (10050 by default) and answers the item keys a server, a proxy or
/// a <see cref="ZabbixWireClient"/> asks it for, from <see cref="Items"/>.
/// </summary>
/// <remarks>
/// <see cref="CreateBuilder"/> builds one, and <c>AddZabbixPassiveAgent</c> registers one in an application's host, with the
/// same verbs (<see cref="ZabbixPassiveAgentBuilderExtensions"/> and the item verbs). An agent constructed directly reads its
/// <see cref="Options"/> when it starts. <see cref="Items"/> is live: a key added while the agent runs is answered from the
/// next request on.
/// </remarks>
/// <example>
/// <code language="csharp">
/// await using ZabbixPassiveAgent agent = ZabbixPassiveAgent.CreateBuilder("web-01")
///     .AllowPeer(IPNetwork.Parse("10.0.0.0/24"))
///     .AddItem("app.version", _ =&gt; "2.4.1")
///     .Build();
/// await agent.StartAsync();
/// </code>
/// </example>
public class ZabbixPassiveAgent : ZabbixComponent
{
    private readonly IConnectionListenerFactory? _transport;
    private readonly IMeterFactory? _meters;
    private readonly ZabbixItemCollector? _containerCollector;
    private readonly bool _itemsAreComplete;
    private ZabbixItemCollector? _collector;    // this agent's own, when no container gave one
    private ZabbixListener? _listener;
    private int _released;

    /// <summary>A passive agent to set up through <see cref="Options"/> and <see cref="Items"/>.</summary>
    public ZabbixPassiveAgent()
        : this(new ZabbixPassiveAgentOptions())
    {
    }

    /// <summary>A passive agent as <paramref name="options"/> says.</summary>
    /// <param name="options">The agent's options, read when it starts.</param>
    /// <exception cref="ArgumentNullException"><paramref name="options"/> is <see langword="null"/>.</exception>
    public ZabbixPassiveAgent(ZabbixPassiveAgentOptions options)
        : this(options, null, null, null)
    {
    }

    /// <summary>A passive agent as <paramref name="options"/> says, with the application's logging, clock and meters.</summary>
    /// <param name="options">The agent's options, read when it starts.</param>
    /// <param name="loggers">Where to log; nowhere when <see langword="null"/>. The caller keeps owning it.</param>
    /// <param name="time">The clock; the system's when <see langword="null"/>.</param>
    /// <param name="meters">The meter factory to count with; the agent owns its meter when <see langword="null"/>.</param>
    /// <exception cref="ArgumentNullException"><paramref name="options"/> is <see langword="null"/>.</exception>
    public ZabbixPassiveAgent(ZabbixPassiveAgentOptions options, ILoggerFactory? loggers, TimeProvider? time, IMeterFactory? meters)
        : this(null, options, null, loggers, time, meters, null)
    {
    }

    // A registration's: its name, and the container's transport and collector.
    internal ZabbixPassiveAgent(string? name, ZabbixPassiveAgentOptions options, IConnectionListenerFactory? transport, ILoggerFactory? loggers, TimeProvider? time, IMeterFactory? meters, ZabbixItemCollector? collector)
        : base(name, loggers, time)
    {
        ArgumentNullException.ThrowIfNull(options);
        Options = options;
        _transport = transport;
        _meters = meters;
        _containerCollector = collector;
        Items = new ZabbixItemDictionary { Owner = this };
    }

    // Over a table a test composed, which is answered as it stands.
    internal ZabbixPassiveAgent(ZabbixPassiveAgentOptions options, ZabbixItemDictionary items, IConnectionListenerFactory transport, TimeProvider? time = null, ILoggerFactory? loggers = null, IMeterFactory? meters = null)
        : base(null, loggers, time)
    {
        ArgumentNullException.ThrowIfNull(options);
        Options = options;
        _transport = transport;
        _meters = meters;
        _itemsAreComplete = true;
        Items = items;
    }

    /// <summary>
    /// Starts a builder for the agent of <paramref name="hostname"/>: who may poll it, its items, and its logging.
    /// </summary>
    /// <param name="hostname">The agent's name, and what <c>agent.hostname</c> answers unless <c>Configure</c> sets another.</param>
    /// <returns>A builder whose <see cref="ZabbixPassiveAgentBuilder.Build"/> returns the agent.</returns>
    /// <exception cref="ArgumentException"><paramref name="hostname"/> is empty.</exception>
    /// <exception cref="ArgumentNullException"><paramref name="hostname"/> is <see langword="null"/>.</exception>
    /// <example>
    /// <code language="csharp">
    /// await using ZabbixPassiveAgent agent = ZabbixPassiveAgent.CreateBuilder("web-01")
    ///     .AllowPeer(IPAddress.Parse("10.0.0.5"))
    ///     .AddItem(pending, () =&gt; queue.Count)
    ///     .Build();
    /// </code>
    /// </example>
    public static ZabbixPassiveAgentBuilder CreateBuilder(string hostname) => new(hostname);

    /// <summary>What the agent reads when it starts.</summary>
    public ZabbixPassiveAgentOptions Options { get; }

    /// <summary>
    /// The keys this agent answers: what you add, and from the start the keys every agent answers and the bundled items
    /// (<see cref="ZabbixPassiveAgentOptions.BundledItems"/>) under every name you left free.
    /// </summary>
    public ZabbixItemDictionary Items { get; }

    /// <summary>The listener, once the agent has started: its local endpoint and the connections in flight.</summary>
    public ZabbixListener? Listener => Volatile.Read(ref _listener);

    /// <inheritdoc/>
    private protected override string DefaultName => Options.Name ?? Options.Hostname;

    /// <inheritdoc/>
#pragma warning disable IDE1006 // DisposeAsyncCore is the name the dispose pattern gives this method
    protected override async ValueTask DisposeAsyncCore()
#pragma warning restore IDE1006
    {
        await base.DisposeAsyncCore().ConfigureAwait(false);          // stops a running component first, so it drains
        if (Interlocked.Exchange(ref _released, 1) != 0) return;
        if (_listener is not null) await _listener.DisposeAsync().ConfigureAwait(false);
        await Items.ReleaseAsync().ConfigureAwait(false);
        _collector?.Dispose();
    }

    /// <inheritdoc/>
    protected override void Dispose(bool disposing)
    {
        if (disposing && Interlocked.Exchange(ref _released, 1) == 0)
        {
            _listener?.Dispose();
            Items.ReleaseAll();
            _collector?.Dispose();
        }
        base.Dispose(disposing);
    }

    /// <inheritdoc/>
    private protected override void CheckReady() => ZabbixOptionsCheck.RequireValid(Name, Options, new ZabbixPassiveAgentOptionsValidator());

    /// <inheritdoc/>
    [System.Diagnostics.CodeAnalysis.SuppressMessage("Reliability", "CA2000:Dispose objects before losing scope", Justification = "The listener is held in a field and disposed with the agent.")]
    private protected override Task OnStartingAsync(CancellationToken cancellationToken)
    {
        if (!_itemsAreComplete)
        {
            ZabbixItemCollector? collector = _containerCollector;
            if (collector is null && Options.BundledItems) collector = _collector = new ZabbixItemCollector(ZabbixHostFiles.Host, Time);
            Items.KeyRules = ZabbixKeyRules.Parse(Options.KeyRules);
            Items.AddDefaults(Options.Hostname, null, Options.BundledItems, collector);
        }
        var responder = new ZabbixPassiveCheckResponder(Items, Options.CheckTimeout, Time, Loggers.CreateLogger<ZabbixPassiveCheckResponder>());
        var listener = new ZabbixListener(Options, responder.Handler, _transport ?? ZabbixSocketTransport.Create(Loggers), Time, Loggers.CreateLogger<ZabbixListener>(), _meters);
        Volatile.Write(ref _listener, listener);
        return listener.StartingAsync(cancellationToken);
    }

    /// <inheritdoc/>
    private protected override Task OnStartAsync(CancellationToken cancellationToken) => Started.StartAsync(cancellationToken);

    /// <inheritdoc/>
    private protected override Task OnStartedAsync(CancellationToken cancellationToken) => Started.StartedAsync(cancellationToken);

    /// <inheritdoc/>
    private protected override Task OnStoppingAsync(CancellationToken cancellationToken) => _listener?.StoppingAsync(cancellationToken) ?? Task.CompletedTask;

    /// <inheritdoc/>
    private protected override Task OnStopAsync(CancellationToken cancellationToken) => _listener?.StopAsync(cancellationToken) ?? Task.CompletedTask;

    /// <inheritdoc/>
    private protected override Task OnStoppedAsync(CancellationToken cancellationToken) => _listener?.StoppedAsync(cancellationToken) ?? Task.CompletedTask;

    private ZabbixListener Started => _listener ?? throw new System.Diagnostics.UnreachableException("The starting phase builds the listener.");
}
