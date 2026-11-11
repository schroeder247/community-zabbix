// This Source Code Form is subject to the terms of the Mozilla Public
// License, v. 2.0. Copyright (c) 2026 schroeder247. SPDX-License-Identifier: MPL-2.0
using System.Diagnostics;
using System.Diagnostics.Metrics;
using Community.Zabbix.Diagnostics;
using Community.Zabbix.PowerShell.Diagnostics;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace Community.Zabbix.PowerShell.Sessions;

/// <summary>
/// What a long-running session runs on: the SDK's agent or proxy, constructed as an application constructs it, with the
/// module's logging (<see cref="ZabbixModuleTelemetry.AddSessionLogging"/>) and meters, and the session's start and stop
/// spans around it. The component takes no process signals, so the shell keeps its Ctrl+C. The agent and proxy modules pass
/// it in (<see cref="ZabbixSessionComponent"/>): this module does not carry the agent package.
/// </summary>
internal sealed class ZabbixSessionHost : IAsyncDisposable
{
    private readonly ServiceProvider _services;
    private readonly ZabbixSessionComponent _component;
    private readonly string _kind;
    private readonly string _name;
    private int _stopped;

    private ZabbixSessionHost(ServiceProvider services, ZabbixSessionComponent component, string kind, string name, ActivityContext? sessionContext)
    {
        _services = services;
        _component = component;
        _kind = kind;
        _name = name;
        SessionContext = sessionContext;
        Telemetry = services.GetRequiredService<ZabbixPowerShellTelemetry>();
        Loggers = services.GetRequiredService<ILoggerFactory>();
        Logs = services.GetRequiredService<ZabbixPowerShellLoggerProvider>();
    }

    /// <summary>The session's telemetry, from the module's meter factory.</summary>
    public ZabbixPowerShellTelemetry Telemetry { get; }

    /// <summary>The session's loggers, on the module's logging.</summary>
    public ILoggerFactory Loggers { get; }

    /// <summary>The bridge from the session's loggers to whichever command next flushes it.</summary>
    public ZabbixPowerShellLoggerProvider Logs { get; }

    /// <summary>The start phase's span, the parent of the session's handler spans.</summary>
    public ActivityContext? SessionContext { get; }

    /// <summary>
    /// Builds the session's logging and meters, has <paramref name="create"/> construct its component over them, and starts
    /// it; on a failure, logs it, disposes everything and throws.
    /// </summary>
    /// <param name="kind">The session's kind, for its telemetry and log (<c>ActiveAgent</c>, <c>PassiveProxy</c>, …).</param>
    /// <param name="name">The session's name.</param>
    /// <param name="time">The clock the component runs on.</param>
    /// <param name="create">Constructs the agent or proxy with the session's loggers and meters.</param>
    /// <param name="cancellationToken">Cancels the start.</param>
    [System.Diagnostics.CodeAnalysis.SuppressMessage("Reliability", "CA2000:Dispose objects before losing scope",
        Justification = "The logger provider is disposed here on every failure and owned by the returned session (disposed with it) on success.")]
    public static async Task<ZabbixSessionHost> StartAsync(string kind, string name, TimeProvider time, Func<ILoggerFactory, IMeterFactory, ZabbixSessionComponent> create, CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(kind);
        ArgumentException.ThrowIfNullOrWhiteSpace(name);
        ArgumentNullException.ThrowIfNull(time);
        ArgumentNullException.ThrowIfNull(create);
        var logs = new ZabbixPowerShellLoggerProvider(time);
        try
        {
            return await StartAsync(kind, name, time, create, logs, cancellationToken).ConfigureAwait(false);
        }
        catch
        {
            logs.Dispose();                                                              // the session owns it once it starts
            throw;
        }
    }

    [System.Diagnostics.CodeAnalysis.SuppressMessage("Reliability", "CA2000:Dispose objects before losing scope",
        Justification = "The container is disposed here on every failure and owned by the returned session (disposed with it) on success.")]
    private static async Task<ZabbixSessionHost> StartAsync(string kind, string name, TimeProvider time, Func<ILoggerFactory, IMeterFactory, ZabbixSessionComponent> create,
        ZabbixPowerShellLoggerProvider logs, CancellationToken cancellationToken)
    {
        var collection = new ServiceCollection();
        collection.AddSingleton(time);
        collection.AddMetrics();
        ZabbixModuleTelemetry.AddSessionLogging(collection, logs);
        collection.AddSingleton<ZabbixPowerShellTelemetry>();
        ServiceProvider services = collection.BuildServiceProvider(new ServiceProviderOptions { ValidateOnBuild = true, ValidateScopes = true });
        ZabbixSessionComponent? component = null;
        ZabbixPowerShellTelemetry telemetry = services.GetRequiredService<ZabbixPowerShellTelemetry>();
        ZabbixOperation started = telemetry.StartSessionPhase(kind, name, "start");
        try
        {
            // The starting command runs this through TryRun, so ZabbixPipeline.Current is its own pipeline for this
            // whole asynchronous flow, including the component's loops, which the start below spins up and which then run for
            // the session's whole life. Clearing it here gives them no pipeline to inherit, so the session's logger
            // provider buffers their lines for Get-Zbx<Session>Log and the next command, instead of posting them to a
            // command that has long returned. The reset is this flow's only (AsyncLocal), so the caller keeps its own.
            ZabbixPipeline.Reset();
            component = create(services.GetRequiredService<ILoggerFactory>(), services.GetRequiredService<IMeterFactory>());
            await component.StartAsync(cancellationToken).ConfigureAwait(false);
            telemetry.FinishSessionPhase(started);
            return new ZabbixSessionHost(services, component, kind, name, started.Activity?.Context);
        }
        catch (Exception failure)
        {
            telemetry.FinishSessionPhase(started, failure);
            ZabbixPowerShellLog.SessionStartFailed(services.GetRequiredService<ILoggerFactory>().CreateLogger("Community.Zabbix.PowerShell.Sessions." + kind), kind, name, failure);
            if (component is not null) await component.DisposeAsync().ConfigureAwait(false);
            await services.DisposeAsync().ConfigureAwait(false);
            throw;
        }
    }

    /// <summary>Stops the component, which delivers what it still holds, and disposes it and the session's logging and meters.</summary>
    public async ValueTask DisposeAsync()
    {
        if (Interlocked.Exchange(ref _stopped, 1) != 0) return;
        ZabbixOperation stopped = Telemetry.StartSessionPhase(_kind, _name, "stop");
        try
        {
            await _component.StopAsync(CancellationToken.None).ConfigureAwait(false);
            Telemetry.FinishSessionPhase(stopped);
        }
        catch (Exception stopFailure)
        {
            Telemetry.FinishSessionPhase(stopped, stopFailure);
            ZabbixPowerShellLog.SessionStopFailed(Loggers.CreateLogger("Community.Zabbix.PowerShell.Sessions." + _kind), _kind, _name, stopFailure);
            throw;
        }
        finally
        {
            await _component.DisposeAsync().ConfigureAwait(false);
            await _services.DisposeAsync().ConfigureAwait(false);
            Logs.Dispose();
        }
    }
}
