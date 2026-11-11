// This Source Code Form is subject to the terms of the Mozilla Public
// License, v. 2.0. Copyright (c) 2026 schroeder247. SPDX-License-Identifier: MPL-2.0
using System.Diagnostics;
using System.Management.Automation;
using System.Net;
using Community.Zabbix.Diagnostics;
using Community.Zabbix.PowerShell.Diagnostics;
using Community.Zabbix.PowerShell.Sessions;
using Community.Zabbix.Protocol;
using Community.Zabbix.Proxy;
using Microsoft.Extensions.Logging;

namespace Community.Zabbix.PowerShell.Commands;

/// <summary>
/// Represents an active proxy's uplink kept running as a session: it re-pulls the configuration on
/// <c>-RefreshInterval</c> (Zabbix's own active-proxy cadence), over a <see cref="ZabbixProxyUplink"/> built with no DI
/// container — the SDK type composes over a plain <see cref="ZabbixExchangeCallback"/>. It uploads only what data providers
/// registered with <see cref="RegisterDataProvider"/> return, each run on the session's own runspace rather than the
/// loop's thread; <c>Send-ZbxProxyData</c> pushes explicitly, on its own connection.
/// </summary>
public sealed class ZabbixActiveProxySession : IZabbixSession, IZabbixLoggedSession
{
    private readonly ZabbixWireLink _link;
    private readonly ZabbixProxyUplink _uplink;
    private readonly ZabbixPowerShellLoggerProvider _logs;
    private readonly ILoggerFactory _loggers;
    private readonly ILogger _log;
    private readonly ZabbixPowerShellTelemetry _telemetry;
    private readonly ActivityContext? _sessionContext;
    private readonly CancellationTokenSource _stopping = new();
    private readonly Task _refreshing;
    private readonly Lock _providersGate = new();
    private readonly List<ZabbixProxyDataCallback> _providers = [];
    private readonly Lock _bridgeGate = new();
    private ZabbixScriptRunspace? _bridge;
    private ZabbixProxyTaskCallback? _taskHandler;
    private ZabbixProxyConfig _held;
    private Exception? _lastFault;
    private DateTimeOffset? _lastPulledAt;
    private int _stopped;
    // Disposed by RefreshAsync's own `using` (it owns construction); kept here too so Retune can reach the running
    // loop's Period without a second synchronization point. CA2213 does not see the `using` in the other method.
    [System.Diagnostics.CodeAnalysis.SuppressMessage("Usage", "CA2213:Disposable fields should be disposed", Justification = "Owned and disposed by RefreshAsync's `using var timer`; this field only aliases it for Retune.")]
    private PeriodicTimer? _timer;

    private ZabbixActiveProxySession(string name, DateTimeOffset started, ZabbixWireLink link, ZabbixProxyUplink uplink, ZabbixProxyConfig held, TimeProvider time, TimeSpan refreshInterval,
        ZabbixPowerShellLoggerProvider logs, ILoggerFactory loggers, ZabbixPowerShellTelemetry telemetry, ActivityContext? sessionContext)
    {
        Name = name;
        Started = started;
        _link = link;
        _uplink = uplink;
        _held = held;
        _logs = logs;
        _loggers = loggers;
        _log = loggers.CreateLogger("Community.Zabbix.PowerShell.Sessions.ActiveProxy");
        _telemetry = telemetry;
        _sessionContext = sessionContext;
        _refreshing = RefreshAsync(time, refreshInterval, _stopping.Token);
    }

    /// <inheritdoc/>
    public string Name { get; }

    /// <inheritdoc/>
    public DateTimeOffset Started { get; }

    /// <summary>The proxy's name as the server knows it.</summary>
    public string ProxyName => _uplink.ProxyName;

    /// <summary>The session the server de-duplicates history on.</summary>
    public string Session => _uplink.Session;

    /// <summary>The protocol version last announced (adopted from a server refusal, when one named itself).</summary>
    public ZabbixComponentVersion Version => _uplink.Version;

    /// <summary>The configuration revision last pulled.</summary>
    public long ConfigRevision => Volatile.Read(ref _held).ConfigRevision;

    /// <summary>When the configuration was last pulled; <see langword="null"/> before the first tick.</summary>
    public DateTimeOffset? LastPulledAt => _lastPulledAt;

    /// <summary>The refresh loop's latest fault, while it lasts (cleared by the next good pull).</summary>
    public Exception? Fault => _lastFault;

    internal ZabbixPowerShellLoggerProvider Logs => _logs;

    /// <inheritdoc/>
    ZabbixPowerShellLoggerProvider IZabbixLoggedSession.Logs => _logs;

    /// <summary>How often the session re-pulls the configuration, right now.</summary>
    public TimeSpan RefreshInterval => _timer?.Period ?? TimeSpan.Zero;

    /// <summary>
    /// Changes how often the running session re-pulls the configuration, without stopping and restarting it —
    /// <see cref="PeriodicTimer.Period"/> is itself mutable (.NET 8+): the loop's next wait picks up the new period,
    /// a pull already in flight is not interrupted.
    /// </summary>
    /// <param name="interval">The new refresh interval.</param>
    internal void Retune(TimeSpan interval)
    {
        ArgumentOutOfRangeException.ThrowIfLessThanOrEqual(interval, TimeSpan.Zero);
        ObjectDisposedException.ThrowIf(Volatile.Read(ref _stopped) != 0, this);
        _timer!.Period = interval;
    }

    private ZabbixScriptRunspace Bridge()
    {
        lock (_bridgeGate)
        {
            return _bridge ??= new ZabbixScriptRunspace(telemetry: _telemetry, handlerKind: "active-proxy", sessionContext: _sessionContext, module: ZabbixScriptRunspace.Manifest(typeof(ZabbixActiveProxySession), "Community.Zabbix.Proxy"), log: _log);
        }
    }

    /// <summary>
    /// Registers <paramref name="scriptBlock"/> as a data provider: asked, on this session's own dedicated runspace,
    /// for a page (its last output, when it is a <see cref="ZabbixProxyDataPage"/>) before every refresh tick's upload
    /// turn (<c>Register-ZbxActiveProxyDataProvider</c>); without a provider, the refresh loop pushes nothing.
    /// </summary>
    internal void RegisterDataProvider(ScriptBlock scriptBlock)
    {
        ArgumentNullException.ThrowIfNull(scriptBlock);
        ZabbixScriptRunspace bridge = Bridge();
        string scriptText = scriptBlock.ToString();
        lock (_providersGate)
        {
            _providers.Add(async (config, cancellationToken) =>
            {
                PSDataCollection<PSObject> output = await bridge.InvokeAsync(scriptText, [config], cancellationToken).ConfigureAwait(false);
                PSObject? last = output.Count > 0 ? output[^1] : null;
                return last?.BaseObject as ZabbixProxyDataPage;
            });
        }
    }

    /// <summary>
    /// Registers <paramref name="scriptBlock"/> as the handler for a task the server hands back in an upload's
    /// acknowledgement, on this session's own dedicated runspace: receives the task, its last output converted to the
    /// result's <c>info</c> text (success unless the scriptblock throws, in which case the failure's message is the
    /// result and its status is failure). Backs <c>Register-ZbxActiveProxyTaskHandler</c>.
    /// </summary>
    internal void RegisterTaskHandler(ScriptBlock scriptBlock)
    {
        ArgumentNullException.ThrowIfNull(scriptBlock);
        ZabbixScriptRunspace bridge = Bridge();
        string scriptText = scriptBlock.ToString();
        Volatile.Write(ref _taskHandler, async (task, cancellationToken) =>
        {
            try
            {
                PSDataCollection<PSObject> output = await bridge.InvokeAsync(scriptText, [task], cancellationToken).ConfigureAwait(false);
                PSObject? last = output.Count > 0 ? output[^1] : null;
                string? info = last is null ? null : LanguagePrimitives.ConvertTo<string>(last.BaseObject);
                return new ZabbixProxyTaskResult(ZabbixProxyTaskType.RemoteCommandResult, task.Clock, task.Ttl, Status: 0, info, task.ParentTaskId ?? 0UL);
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch (Exception failure) when (failure is not OutOfMemoryException)
            {
                return new ZabbixProxyTaskResult(ZabbixProxyTaskType.RemoteCommandResult, task.Clock, task.Ttl, Status: 1, failure.Message, task.ParentTaskId ?? 0UL);
            }
        });
    }

    /// <summary>
    /// One upload turn: asks every registered provider for a page, merges them (<see cref="ZabbixProxyDataProviders"/>,
    /// the same seam <see cref="ZabbixActiveProxy"/>'s upload loop uses), and — when the merged page is not empty —
    /// pushes it; a task the server hands back in the acknowledgement is run through the registered handler (if any)
    /// and its result pushed as a small follow-up page. No store backs this lean, uplink-only session, so a provider
    /// is asked fresh every tick and a push that fails is simply asked again next tick — there is nothing to hold.
    /// </summary>
    private async Task UploadTurnAsync(CancellationToken stopping)
    {
        IReadOnlyList<ZabbixProxyDataCallback> providers;
        lock (_providersGate) providers = [.. _providers];
        if (providers.Count == 0) return;
        IReadOnlyList<ZabbixProxyDataPage> pages = await ZabbixProxyDataProviders.AskAsync(providers, _held,
            failure => ZabbixPowerShellLog.SessionTickFailed(_log, "ActiveProxy", Name, failure), stopping).ConfigureAwait(false);
        ZabbixProxyDataPage merged = ZabbixProxyDataProviders.Merge(ZabbixProxyDataPage.Empty, pages);
        if (merged.IsEmpty) return;
        ZabbixProxyResponse response = await _uplink.UploadAsync(merged, more: false, proxyDelay: 0, stopping).ConfigureAwait(false);
        if (!response.IsSuccess || response.TaskCount == 0) return;
        ZabbixProxyTaskCallback? handler = Volatile.Read(ref _taskHandler);
        if (handler is null) return;
        var results = new List<ZabbixProxyTaskResult>();
        foreach (ZabbixProxyTask task in response.DeserializeTasks())
            results.Add(await handler(task, stopping).ConfigureAwait(false));
        if (results.Count > 0)
            await _uplink.UploadAsync(new ZabbixProxyDataPage(Tasks: results), more: false, proxyDelay: 0, stopping).ConfigureAwait(false);
    }

    internal static ZabbixActiveProxySession Start(string name, string proxyName, IReadOnlyList<DnsEndPoint> nodes, TimeProvider time, TimeSpan refreshInterval, ZabbixConnectionOptions? connection = null)
    {
        var logs = new ZabbixPowerShellLoggerProvider(time);
        ILoggerFactory loggers = ZabbixModuleTelemetry.CreateLoggers(logs);
        var telemetry = new ZabbixPowerShellTelemetry(meters: null, time);
        ZabbixOperation started = telemetry.StartSessionPhase("ActiveProxy", name, "start");
        ZabbixWireLink? link = null;
        try
        {
            ILogger<ZabbixProxyUplink> logger = loggers.CreateLogger<ZabbixProxyUplink>();
            // The session owns its connection and disposes it when it stops, which releases what certificate mode read.
            // Large replies: upstream's proxy receives its configuration as a large frame (zbx_tcp_recv_large), as the
            // proxy host's uplink does.
            link = ZabbixWireLinks.Open(nodes, connection ?? new ZabbixConnectionOptions(), time, loggers);
            var uplink = new ZabbixProxyUplink(link.AsExchange(followRedirects: false, acceptLargeReplies: true), proxyName, time: time, logger: logger);
            telemetry.FinishSessionPhase(started);
            return new ZabbixActiveProxySession(name, time.GetUtcNow(), link, uplink, ZabbixProxyConfig.Empty, time, refreshInterval, logs, loggers,
                telemetry, started.Activity?.Context);
        }
        catch (Exception failure)
        {
            link?.Dispose();
            telemetry.FinishSessionPhase(started, failure);
            ZabbixPowerShellLog.SessionStartFailed(loggers.CreateLogger("Community.Zabbix.PowerShell.Sessions.ActiveProxy"), "ActiveProxy", name, failure);
            telemetry.Dispose();
            loggers.Dispose();
            throw;
        }
    }

    private async Task RefreshAsync(TimeProvider time, TimeSpan interval, CancellationToken stopping)
    {
        using var timer = new PeriodicTimer(interval, time);
        _timer = timer;
        do
        {
            try
            {
                _held = await _uplink.GetConfigAsync(_held, stopping).ConfigureAwait(false);
                _lastPulledAt = time.GetUtcNow();
                _lastFault = null;
                await UploadTurnAsync(stopping).ConfigureAwait(false);
            }
            catch (Exception failure) when (failure is not OperationCanceledException)
            {
                _lastFault = failure;
                ZabbixPowerShellLog.SessionTickFailed(_log, "ActiveProxy", Name, failure);
            }
        } while (await timer.WaitForNextTickAsync(stopping).ConfigureAwait(false));
    }

    /// <inheritdoc/>
    public async ValueTask DisposeAsync()
    {
        if (Interlocked.Exchange(ref _stopped, 1) != 0) return;
        ZabbixOperation stopped = _telemetry.StartSessionPhase("ActiveProxy", Name, "stop");
        await _stopping.CancelAsync().ConfigureAwait(false);
        try
        {
            await _refreshing.ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            // Expected: the refresh loop's own cancellation.
        }
        if (_bridge is not null) await _bridge.DisposeAsync().ConfigureAwait(false);
        _link.Dispose();
        _telemetry.FinishSessionPhase(stopped);
        _telemetry.Dispose();
        _stopping.Dispose();
        _logs.Dispose();
        _loggers.Dispose();
    }
}
