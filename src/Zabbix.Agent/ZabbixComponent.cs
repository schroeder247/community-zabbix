// This Source Code Form is subject to the terms of the Mozilla Public
// License, v. 2.0. Copyright (c) 2026 schroeder247. SPDX-License-Identifier: MPL-2.0
using System.Runtime.ExceptionServices;
using Community.Zabbix.Hosting;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace Community.Zabbix;

/// <summary>
/// The lifetime every agent and proxy has: start it, stop it, or run it until a token says to stop.
/// </summary>
/// <remarks>
/// <para>
/// Build an agent with its builder (<c>ZabbixActiveAgent.CreateBuilder</c>, …), or construct one and set its options, then
/// start it: a constructed component reads and checks nothing until <see cref="StartAsync"/>, which says what to set when
/// something is missing, and can then be called again. In an application with a host, register it
/// (<c>AddZabbixActiveAgent</c>, …) and the host starts and stops the same class.
/// </para>
/// <para>
/// A component starts once. A start that fails on the way stops what started and rethrows, and the component can then only
/// be disposed. Stopping delivers what the component still holds, within <see cref="StopTimeout"/>. Disposing a running
/// component stops it first, so <c>await using</c> drains it; a failure to stop is logged rather than thrown, so call
/// <see cref="StopAsync"/> first to see it. A component takes no process signals, so it embeds in a console app, a desktop
/// app, a test or a PowerShell session.
/// </para>
/// <para>
/// A component an application's container builds (<c>AddZabbixActiveAgent</c>, …) is the container's: whoever resolves it
/// shares it, a caller's <see cref="Dispose()"/> or <see cref="DisposeAsync"/> leaves it running, and the container disposes
/// it when it's disposed, as disposing an <c>HttpClient</c> from <c>IHttpClientFactory</c> leaves its handler. A component
/// you construct, or build with its builder, is yours to dispose.
/// </para>
/// </remarks>
/// <example>
/// <code language="csharp">
/// await using ZabbixActiveAgent agent = ZabbixActiveAgent.CreateBuilder("web-01")
///     .AddServer("zbx-server-01")
///     .AddItem("app.version", _ => "2.4.1")
///     .Build();
/// await agent.RunAsync(stoppingToken);
/// </code>
/// </example>
public abstract class ZabbixComponent : IAsyncDisposable, IDisposable
{
    private const int Created = 0;
    private const int Starting = 1;
    private const int Running = 2;
    private const int Stopping = 3;
    private const int Stopped = 4;

    private readonly string? _name;
    private readonly ILogger _logger;
    private readonly TaskCompletionSource _stopRequested = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private TimeSpan _stopTimeout = TimeSpan.FromSeconds(30);
    private IHostedService[] _companions = [];      // what a builder's container hosts beside the component: started after it, stopped before it
    private IAsyncDisposable? _container;           // the builder's container, which built the component; disposed after it
    private int _state;
    private int _disposed;
    private int _hosted;
    private int _shared;                            // the container's: a caller's dispose leaves it running
    private int _released;                          // the container let it go

    /// <summary>Initializes the lifetime. Only this SDK's agents and proxies derive from this class directly: its phases are internal.</summary>
    /// <param name="name">A registration's name; <see langword="null"/> when the component is constructed directly.</param>
    /// <param name="loggers">Where to log; nowhere when <see langword="null"/>.</param>
    /// <param name="time">The clock; the system's when <see langword="null"/>.</param>
    protected ZabbixComponent(string? name, ILoggerFactory? loggers, TimeProvider? time)
    {
        _name = name;
        Loggers = loggers ?? NullLoggerFactory.Instance;
        Time = time ?? TimeProvider.System;
        _logger = Loggers.CreateLogger<ZabbixComponent>();
    }

    /// <summary>The name its logs, metrics and status carry.</summary>
    public string Name => _name ?? DefaultName;

    /// <summary>
    /// How long <see cref="StopAsync"/> may take before the token its phases hold is cancelled: 30 seconds, the generic
    /// host's default (<c>HostOptions.ShutdownTimeout</c>). <see cref="Timeout.InfiniteTimeSpan"/> waits for as long as the
    /// stop takes. A host bounds a component it runs with its own timeout instead.
    /// </summary>
    /// <exception cref="ArgumentOutOfRangeException">The value is neither positive nor infinite.</exception>
    public TimeSpan StopTimeout
    {
        get => _stopTimeout;
        set
        {
            if (value <= TimeSpan.Zero && value != Timeout.InfiniteTimeSpan)
                throw new ArgumentOutOfRangeException(nameof(value), value, "StopTimeout must be positive, or Timeout.InfiniteTimeSpan.");
            _stopTimeout = value;
        }
    }

    /// <summary>The name when no registration gave one, read from what was set.</summary>
    private protected abstract string DefaultName { get; }

    /// <summary>Where the component logs.</summary>
    internal ILoggerFactory Loggers { get; }

    /// <summary>The clock the component runs on.</summary>
    internal TimeProvider Time { get; }

    /// <summary>Whether a start has begun; what a component is composed with is fixed from then on.</summary>
    private protected bool HasStarted => Volatile.Read(ref _state) != Created;

    /// <summary>
    /// Whether a dispose leaves the component running: it's shared, and its container hasn't released it. A derived class's
    /// <see cref="Dispose(bool)"/> and <see cref="DisposeAsyncCore"/> release nothing then.
    /// </summary>
    private protected bool IsKept => Volatile.Read(ref _shared) != 0 && Volatile.Read(ref _released) == 0;

    /// <summary>Starts the component.</summary>
    /// <param name="cancellationToken">Cancels the start.</param>
    /// <returns>A task that completes when the component has started.</returns>
    /// <exception cref="ObjectDisposedException">The component was disposed.</exception>
    /// <exception cref="InvalidOperationException">
    /// What was set does not validate, which the message says; or the component was already started, as a component starts once.
    /// </exception>
    public Task StartAsync(CancellationToken cancellationToken = default)
    {
        ObjectDisposedException.ThrowIf(Volatile.Read(ref _disposed) != 0, this);
        if (Volatile.Read(ref _state) == Created) CheckReady();
        return Interlocked.CompareExchange(ref _state, Starting, Created) == Created
            ? StartAllAsync(cancellationToken)
            : throw new InvalidOperationException($"'{Name}' was already started; a component starts once.");
    }

    /// <summary>
    /// Stops the component, delivering what it still holds within <see cref="StopTimeout"/>. Every phase runs even when
    /// another fails. A component that is not running is left as it is.
    /// </summary>
    /// <param name="cancellationToken">Ends the stop early: the token its phases hold is cancelled.</param>
    /// <returns>A task that completes when the component has stopped.</returns>
    /// <exception cref="AggregateException">More than one phase failed; a single failure is thrown as itself.</exception>
    public Task StopAsync(CancellationToken cancellationToken = default)
        => Interlocked.CompareExchange(ref _state, Stopped, Running) == Running ? StopAllAsync(cancellationToken) : Task.CompletedTask;

    /// <summary>Starts the component, waits until <paramref name="cancellationToken"/> is cancelled or it is stopped, and stops it.</summary>
    /// <param name="cancellationToken">Says when to stop.</param>
    /// <returns>A task that completes when the component has stopped.</returns>
    /// <exception cref="ObjectDisposedException">The component was disposed.</exception>
    /// <exception cref="InvalidOperationException">
    /// What was set does not validate, which the message says; or the component was already started, as a component starts once.
    /// </exception>
    public async Task RunAsync(CancellationToken cancellationToken = default)
    {
        await StartAsync(cancellationToken).ConfigureAwait(false);
        try { await _stopRequested.Task.WaitAsync(cancellationToken).ConfigureAwait(false); }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { /* the caller's word to stop */ }
        await StopAsync(CancellationToken.None).ConfigureAwait(false);
    }

    /// <summary>
    /// Stops a running component within <see cref="StopTimeout"/>, then releases what it holds, the container its builder
    /// built it in among it. A second call does nothing, and so does a call on a component its container shares: the
    /// container disposes it.
    /// </summary>
    /// <returns>A task that completes when the component is released.</returns>
    public async ValueTask DisposeAsync()
    {
        await DisposeAsyncCore().ConfigureAwait(false);
        Dispose(disposing: false);
        GC.SuppressFinalize(this);
        // The container disposes this component too, since it built it, and that call returns at once.
        if (Interlocked.Exchange(ref _container, null) is { } container) await container.DisposeAsync().ConfigureAwait(false);
    }

    /// <summary>
    /// Releases what the component holds at once, without delivering what it still holds. A second call does nothing, and so
    /// does a call on a component its container shares: the container disposes it.
    /// </summary>
    public void Dispose()
    {
        Dispose(disposing: true);
        GC.SuppressFinalize(this);
    }

    /// <summary>What the component's builder container hosts beside it, in the order it starts them.</summary>
    internal IReadOnlyList<IHostedService> Companions => _companions;

    /// <summary>
    /// Gives the component the container a builder built it in, to dispose after itself, and what that container hosts
    /// beside it (a trapper, a library's runner), which the component starts after itself and stops before itself, as a
    /// host would.
    /// </summary>
    /// <param name="container">The builder's container, which built this component.</param>
    /// <param name="companions">The services the container hosts, in their registration order.</param>
    internal void OwnContainer(IAsyncDisposable container, IEnumerable<IHostedService> companions)
    {
        _companions = [.. companions];
        _container = container;
        Volatile.Write(ref _shared, 0);             // the builder's caller owns it
    }

    /// <summary>Makes the component its container's, which <see cref="ZabbixComponentOwner{TComponent}"/> releases.</summary>
    internal void Share() => Volatile.Write(ref _shared, 1);

    /// <summary>Releases the component, shared or not: its container's disposal, through <see cref="ZabbixComponentOwner{TComponent}"/>.</summary>
    /// <returns>A task that completes when the component is released.</returns>
    internal ValueTask ReleaseAsync()
    {
        Volatile.Write(ref _released, 1);
        return DisposeAsync();
    }

    /// <summary>Releases the component at once, shared or not: its container's synchronous disposal.</summary>
    internal void Release()
    {
        Volatile.Write(ref _released, 1);
        Dispose();
    }

    /// <summary>Stops the component; a derived class then releases what it holds. See <see cref="DisposeAsync"/>.</summary>
    /// <returns>A task that completes when the component has stopped.</returns>
    protected virtual ValueTask DisposeAsyncCore() => IsKept ? ValueTask.CompletedTask : StopForDisposalAsync();

    /// <summary>Marks the component disposed; a derived class releases what it holds. See <see cref="Dispose()"/>.</summary>
    /// <param name="disposing">Whether this is <see cref="Dispose()"/>, rather than the end of <see cref="DisposeAsync"/>.</param>
    protected virtual void Dispose(bool disposing)
    {
        if (!disposing || IsKept) return;
        Volatile.Write(ref _disposed, 1);
        _stopRequested.TrySetResult();
        // A provider the container built may be disposable asynchronously only: the container's disposal is started, and a
        // part of it that doesn't finish at once finishes on its own, as a store's last commit does.
        if (Interlocked.Exchange(ref _container, null) is { } container)
        {
            ValueTask releasing = container.DisposeAsync();
            if (!releasing.IsCompletedSuccessfully) _ = releasing.AsTask();
        }
    }

    /// <summary>Throws <see cref="InvalidOperationException"/>, saying what to set, when the component cannot start as it is.</summary>
    private protected abstract void CheckReady();

    private protected abstract Task OnStartingAsync(CancellationToken cancellationToken);

    private protected abstract Task OnStartAsync(CancellationToken cancellationToken);

    private protected abstract Task OnStartedAsync(CancellationToken cancellationToken);

    private protected abstract Task OnStoppingAsync(CancellationToken cancellationToken);

    private protected abstract Task OnStopAsync(CancellationToken cancellationToken);

    private protected abstract Task OnStoppedAsync(CancellationToken cancellationToken);

    // A host's phases, through ZabbixHostedComponent: the host runs them in its order and bounds them with its own timeout,
    // and a registration's ValidateOnStart has already checked the options.
    internal Task HostStartingAsync(CancellationToken cancellationToken)
    {
        ObjectDisposedException.ThrowIf(Volatile.Read(ref _disposed) != 0, this);
        Volatile.Write(ref _hosted, 1);
        return Interlocked.CompareExchange(ref _state, Starting, Created) == Created
            ? OnStartingAsync(cancellationToken)
            : throw new InvalidOperationException($"'{Name}' was already started; a component starts once.");
    }

    internal async Task HostStartAsync(CancellationToken cancellationToken)
    {
        if (Volatile.Read(ref _state) == Created) await HostStartingAsync(cancellationToken).ConfigureAwait(false);   // a caller on the plain IHostedService shape
        await OnStartAsync(cancellationToken).ConfigureAwait(false);
        Volatile.Write(ref _state, Running);
    }

    internal Task HostStartedAsync(CancellationToken cancellationToken) => OnStartedAsync(cancellationToken);

    internal Task HostStoppingAsync(CancellationToken cancellationToken)
    {
        Volatile.Write(ref _state, Stopping);
        _stopRequested.TrySetResult();
        return OnStoppingAsync(cancellationToken);
    }

    internal async Task HostStopAsync(CancellationToken cancellationToken)
    {
        if (Volatile.Read(ref _state) != Stopping) await HostStoppingAsync(cancellationToken).ConfigureAwait(false);   // the plain shape again
        await OnStopAsync(cancellationToken).ConfigureAwait(false);
    }

    internal async Task HostStoppedAsync(CancellationToken cancellationToken)
    {
        await OnStoppedAsync(cancellationToken).ConfigureAwait(false);
        Volatile.Write(ref _state, Stopped);
    }

    private async ValueTask StopForDisposalAsync()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0) return;
        try
        {
            // A host stops what it started; a container disposed without that stop closes at once.
            if (Volatile.Read(ref _hosted) == 0) await StopAsync(CancellationToken.None).ConfigureAwait(false);
        }
        catch (Exception failure) when (failure is not OutOfMemoryException)
        {
            AgentLog.ComponentDisposeStopFailed(_logger, Name, failure);
        }
        finally
        {
            _stopRequested.TrySetResult();
        }
    }

    private async Task StartAllAsync(CancellationToken cancellationToken)
    {
        try
        {
            await OnStartingAsync(cancellationToken).ConfigureAwait(false);
            foreach (IHostedService companion in _companions)
                if (companion is IHostedLifecycleService lifecycle) await lifecycle.StartingAsync(cancellationToken).ConfigureAwait(false);
            await OnStartAsync(cancellationToken).ConfigureAwait(false);
            foreach (IHostedService companion in _companions) await companion.StartAsync(cancellationToken).ConfigureAwait(false);
            await OnStartedAsync(cancellationToken).ConfigureAwait(false);
            foreach (IHostedService companion in _companions)
                if (companion is IHostedLifecycleService lifecycle) await lifecycle.StartedAsync(cancellationToken).ConfigureAwait(false);
            Volatile.Write(ref _state, Running);
        }
        catch (Exception failure)
        {
            AgentLog.ComponentStartFailed(_logger, Name, failure);
            await RollBackAsync().ConfigureAwait(false);
            Volatile.Write(ref _state, Stopped);
            _stopRequested.TrySetResult();
            throw;
        }
        AgentLog.ComponentStarted(_logger, Name);
    }

    // The start's failure is the one the caller gets; a phase that also fails here is logged.
    private async Task RollBackAsync()
    {
        foreach (Func<CancellationToken, Task> phase in StopPhases())
        {
            try
            {
                await phase(CancellationToken.None).ConfigureAwait(false);
            }
            catch (Exception stopFailure) when (stopFailure is not OutOfMemoryException)
            {
                AgentLog.ComponentRollbackStopFailed(_logger, Name, stopFailure);
            }
        }
    }

    private async Task StopAllAsync(CancellationToken cancellationToken)
    {
        _stopRequested.TrySetResult();
        using var timeout = new CancellationTokenSource(_stopTimeout, Time);
        using var stop = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, timeout.Token);
        var failures = new List<Exception>();
        foreach (Func<CancellationToken, Task> phase in StopPhases())
        {
            try
            {
                await phase(stop.Token).ConfigureAwait(false);
            }
            catch (Exception failure) when (failure is not OutOfMemoryException)
            {
                AgentLog.ComponentStopFailed(_logger, Name, failure);
                failures.Add(failure);
            }
        }
        AgentLog.ComponentStopped(_logger, Name, failures.Count);
        if (failures.Count == 1) ExceptionDispatchInfo.Throw(failures[0]);
        if (failures.Count > 1) throw new AggregateException($"More than one phase of stopping '{Name}' failed.", failures);
    }

    // The stop's phases in order, each run whether another fails: in each, what the component's container hosts stops first,
    // in the reverse of its start, and then the component itself.
    private IEnumerable<Func<CancellationToken, Task>> StopPhases()
    {
        IHostedService[] companions = [.. Enumerable.Reverse(_companions)];
        foreach (IHostedService companion in companions)
            if (companion is IHostedLifecycleService lifecycle) yield return lifecycle.StoppingAsync;
        yield return OnStoppingAsync;
        foreach (IHostedService companion in companions) yield return companion.StopAsync;
        yield return OnStopAsync;
        foreach (IHostedService companion in companions)
            if (companion is IHostedLifecycleService lifecycle) yield return lifecycle.StoppedAsync;
        yield return OnStoppedAsync;
    }
}
