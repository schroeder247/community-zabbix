// This Source Code Form is subject to the terms of the Mozilla Public
// License, v. 2.0. Copyright (c) 2026 schroeder247. SPDX-License-Identifier: MPL-2.0
using Community.Zabbix.Diagnostics;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using OpenTelemetry;
using OpenTelemetry.Logs;
using OpenTelemetry.Metrics;
using OpenTelemetry.Resources;
using OpenTelemetry.Trace;

namespace Community.Zabbix.PowerShell.Diagnostics;

/// <summary>
/// Composes the module's logging and configures OTLP export of the SDK's and the module's logs, traces and metrics
/// (the <c>Zabbix.*</c> sources) when <c>OTEL_EXPORTER_OTLP_ENDPOINT</c> is set; when it is unset, nothing is loaded or
/// exported.
/// </summary>
/// <remarks>
/// Inside <c>pwsh</c> the module is the application, so it makes the telemetry decision at import. The exporters read
/// every other <c>OTEL_*</c> variable themselves (protocol, headers, service name).
/// One set of exporters serves the process: a tracer and a meter provider subscribe process-wide (an
/// <see cref="System.Diagnostics.ActivityListener"/>, a <see cref="System.Diagnostics.Metrics.MeterListener"/>), so one per
/// connection would export every span and measurement once per open connection. They are created on first use — the first
/// connection that exports — and shared by every connection of every runspace through a lease; the last lease out
/// flushes and disposes them (the last connection closed: <c>Disconnect-ZbxServer</c>, a closing runspace,
/// <c>Remove-Module</c>), and the next connection creates them again. Logs reach them through each connection's own
/// logging pipeline (after its redaction), forwarded to the shared logger factory.
/// <para>
/// Every logger factory of the module is composed one way (<see cref="AddModuleLogging"/>): a connection's, a background
/// session's and a connection-less command's (<c>Send-ZbxData -Server</c>, <c>Get-ZbxAgentItem</c>). Each takes the
/// SDK's redaction policy, writes every level to the host's streams (the command's preference variables decide what
/// shows), and, when export is asked for, holds a lease on the exporters for its own life, so an agent session or a
/// sender with no connection exports its logs, spans and metrics too. The exported logs start at <c>Information</c>,
/// configured as any .NET application's are: <c>Logging__LogLevel__Default</c> and
/// <c>Logging__LogLevel__&lt;category&gt;</c> in the environment.
/// </para>
/// </remarks>
internal static class ZabbixModuleTelemetry
{
    /// <summary>The variable whose presence turns export on.</summary>
    public const string EndpointVariable = "OTEL_EXPORTER_OTLP_ENDPOINT";

    /// <summary>The service name exported when <c>OTEL_SERVICE_NAME</c> does not name one.</summary>
    public const string ServiceName = "Community.Zabbix.PowerShell";

    // The one process-wide state of the module, and the reason it is process-wide: the tracer and meter providers listen
    // process-wide whoever creates them, so a static owns them; no per-runspace state lives here. Leased by
    // connections, never read by a runspace for anything else.
    private static readonly Leases s_shared = new();

    /// <summary>Whether the environment asks for export.</summary>
    public static bool IsRequested() => !string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable(EndpointVariable));

    /// <summary>
    /// The module's one logging composition: no other provider, every level (the host's streams filter by preference),
    /// redaction on, with the SDK's redactors (<see cref="ZabbixRedactionBuilderExtensions.SetZabbixRedactors"/>) and the
    /// default fallback, which erases any other class, and the container's <see cref="ZabbixPowerShellLoggerProvider"/>,
    /// the bridge to the running command's streams. The module is the application here, so the policy is its own.
    /// </summary>
    /// <param name="logging">The factory's builder.</param>
    public static ILoggingBuilder AddModuleLogging(ILoggingBuilder logging)
    {
        ArgumentNullException.ThrowIfNull(logging);
        logging.ClearProviders().SetMinimumLevel(LogLevel.Trace).EnableRedaction();
        logging.Services.AddRedaction(redaction => redaction.SetZabbixRedactors());
        logging.Services.AddSingleton<ILoggerProvider>(provider => provider.GetRequiredService<ZabbixPowerShellLoggerProvider>());
        return logging;
    }

    /// <summary>
    /// The loggers of a background session or a connection-less command: <see cref="AddModuleLogging"/>, and the export
    /// of its logs when <see cref="IsRequested"/>, holding a lease on the exporters until the factory is disposed.
    /// </summary>
    /// <param name="host">The bridge to the host's streams; owned by the caller.</param>
    public static ILoggerFactory CreateLoggers(ZabbixPowerShellLoggerProvider host)
    {
        ArgumentNullException.ThrowIfNull(host);
        bool export = IsRequested();
        return LoggerFactory.Create(logging =>
        {
            logging.Services.AddSingleton(host);
            AddModuleLogging(logging);
            if (export) AddLeasedExport(logging.Services);
        });
    }

    /// <summary>
    /// The logging of a session's container (<see cref="Sessions.ZabbixSessionHost"/>): <see cref="AddModuleLogging"/>
    /// bridged to <paramref name="host"/>, and the export of its logs when <see cref="IsRequested"/>, holding a lease on
    /// the exporters until the container is disposed — what <see cref="CreateLoggers"/> composes, in the container.
    /// </summary>
    /// <param name="services">The session's container.</param>
    /// <param name="host">The bridge to the host's streams; owned by the caller.</param>
    public static void AddSessionLogging(IServiceCollection services, ZabbixPowerShellLoggerProvider host)
    {
        ArgumentNullException.ThrowIfNull(services);
        ArgumentNullException.ThrowIfNull(host);
        services.AddSingleton(host);
        services.AddLogging(logging => AddModuleLogging(logging));
        if (IsRequested()) AddLeasedExport(services);
    }

    // Kept apart from CreateLoggers: the OpenTelemetry SDK's assemblies load only when this method is compiled.
    private static void AddLeasedExport(IServiceCollection services)
        => services.AddSingleton<ILoggerProvider>(_ => new LeasedForwarding(s_shared));

    /// <summary>
    /// Takes a lease on the process's exporters, creating them on first use, and adds the export of the connection's
    /// logs to its container. The connection returns the lease with <see cref="Release"/> when its container goes.
    /// </summary>
    /// <param name="services">The connection's container.</param>
    public static void AddOtlpExport(IServiceCollection services)
    {
        ArgumentNullException.ThrowIfNull(services);
        Exporters exporters = s_shared.Take();
        services.AddSingleton<ILoggerProvider>(_ => new Forwarding(exporters.Loggers));
    }

    /// <summary>
    /// Returns a connection's lease: its pending telemetry is exported now (a process that exits right after
    /// <c>Disconnect-ZbxServer</c> still exports what the connection recorded), and the last lease out disposes the
    /// exporters.
    /// </summary>
    public static void Release() => s_shared.Return();

    /// <summary>The process's exporters and the connections' leases on them.</summary>
    private sealed class Leases
    {
        private readonly Lock _gate = new();
        private Exporters? _exporters;
        private int _count;

        public Exporters Take()
        {
            lock (_gate)
            {
                _exporters ??= Exporters.Create();
                _count++;
                return _exporters;
            }
        }

        public void Return()
        {
            Exporters? exporters;
            bool last;
            lock (_gate)
            {
                if (_count == 0 || _exporters is null) return;
                exporters = _exporters;
                last = --_count == 0;
                if (last) _exporters = null;
            }
            if (last) exporters.Dispose();
            else exporters.Flush();
        }
    }

    /// <summary>
    /// How long a flush of the three providers may take together: the OTLP exporter's own default request timeout
    /// (<c>OTEL_EXPORTER_OTLP_TIMEOUT</c>, 10 s), so a flush that a live collector answers is never cut short.
    /// </summary>
    internal static readonly TimeSpan s_flushDeadline = TimeSpan.FromSeconds(10);

    /// <summary>
    /// The process's exporters: the OpenTelemetry SDK composed in a container of its own (the tracer, meter and logger
    /// providers over <c>Zabbix.*</c>, OTLP), started when built — no host starts them, and resolving is starting.
    /// </summary>
    internal sealed class Exporters : IDisposable
    {
        private readonly ServiceProvider _services;
        private readonly TimeSpan _flushDeadline;
        private readonly TimeProvider _time;

        /// <summary>Starts the exporters <paramref name="services"/> composes.</summary>
        /// <param name="services">A container with <c>AddOpenTelemetry</c> in it.</param>
        /// <param name="flushDeadline">How long one <see cref="Flush"/> may take; <see cref="s_flushDeadline"/> when omitted.</param>
        /// <param name="time">The clock the deadline runs on; the system's when omitted (the exporters serve every runspace).</param>
        public Exporters(ServiceProvider services, TimeSpan? flushDeadline = null, TimeProvider? time = null)
        {
            _services = services;
            _flushDeadline = flushDeadline ?? s_flushDeadline;
            _time = time ?? TimeProvider.System;
            _ = services.GetService<TracerProvider>();
            _ = services.GetService<MeterProvider>();
            _ = services.GetService<LoggerProvider>();
            Loggers = services.GetRequiredService<ILoggerFactory>();
        }

        /// <summary>The factory whose provider exports: a connection's logs are forwarded to it.</summary>
        public ILoggerFactory Loggers { get; }

        public static Exporters Create()
        {
            // The exported level is configuration, not code: Information unless Logging__LogLevel__* says otherwise.
            IConfiguration configuration = new ConfigurationBuilder()
                .AddEnvironmentVariables()
                .Build();
            var services = new ServiceCollection();
            services.AddLogging(logging => logging
                .SetMinimumLevel(LogLevel.Information)
                .AddConfiguration(configuration.GetSection("Logging")));
            services.AddOpenTelemetry()
                .ConfigureResource(resource =>
                {
                    if (string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable("OTEL_SERVICE_NAME"))) resource.AddService(ServiceName);
                })
                .WithTracing(tracing => tracing.AddSource(ZabbixTelemetryNames.Subscription))
                .WithMetrics(metrics => metrics.AddMeter(ZabbixTelemetryNames.Subscription))
                .WithLogging()
                .UseOtlpExporter();
            return new Exporters(services.BuildServiceProvider());
        }

        /// <summary>
        /// Exports what is pending. Disposing the container alone exports the pending traces and metrics but not the
        /// pending log batch (OpenTelemetry 1.18.0); flushing the three providers first exports all three
        /// (ProcessTests.Otlp_export_follows_the_endpoint_set_at_import). The three flushes share one deadline
        /// (<see cref="s_flushDeadline"/> by default), each given what the ones before it left: <c>ForceFlush()</c>'s default is
        /// <see cref="Timeout.Infinite"/>, and this runs on the thread that drops a connection's lease — the pipeline
        /// thread of <c>Disconnect-ZbxServer</c> or of a closing runspace — so a collector that never answers holds the
        /// prompt for one deadline, not one per provider.
        /// </summary>
        public void Flush()
        {
            long started = _time.GetTimestamp();
            int Remaining() => (int)Math.Max(0, (_flushDeadline - _time.GetElapsedTime(started)).TotalMilliseconds);
            _services.GetService<LoggerProvider>()?.ForceFlush(Remaining());
            _services.GetService<TracerProvider>()?.ForceFlush(Remaining());
            _services.GetService<MeterProvider>()?.ForceFlush(Remaining());
        }

        public void Dispose()
        {
            Flush();
            _services.Dispose();
        }
    }

    // The logs of a session or a connection-less command, forwarded to the shared factory under a lease of its own:
    // created with the factory that holds it, returned (and the exporters flushed, or disposed by the last lease out)
    // when that factory is disposed.
    private sealed class LeasedForwarding : ILoggerProvider
    {
        private readonly Leases _leases;
        private readonly ILoggerFactory _shared;
        private int _returned;

        public LeasedForwarding(Leases leases)
        {
            _leases = leases;
            _shared = leases.Take().Loggers;
        }

        public ILogger CreateLogger(string categoryName) => _shared.CreateLogger(categoryName);

        public void Dispose()
        {
            if (Interlocked.Exchange(ref _returned, 1) == 0) _leases.Return();
        }
    }

    // A connection's logs, forwarded to the shared factory; disposing the connection's container leaves it running.
    private sealed class Forwarding(ILoggerFactory shared) : ILoggerProvider
    {
        public ILogger CreateLogger(string categoryName) => shared.CreateLogger(categoryName);

        public void Dispose()
        {
            // The shared factory is the exporters', disposed with them.
        }
    }
}
