// This Source Code Form is subject to the terms of the Mozilla Public
// License, v. 2.0. Copyright (c) 2026 schroeder247. SPDX-License-Identifier: MPL-2.0
using System.Collections.Frozen;
using System.Management.Automation;
using Community.Zabbix.Items;
using Community.Zabbix.Listening;
using Community.Zabbix.PowerShell.Diagnostics;
using Community.Zabbix.PowerShell.Sessions;

namespace Community.Zabbix.PowerShell.Commands;

/// <summary>
/// Represents an active or passive agent kept running as a session in the runspace. It is
/// started by <c>Start-ZbxActiveAgent</c>/<c>Start-ZbxPassiveAgent</c>, found by <c>Get-ZbxActiveAgentStatus</c>/
/// <c>Get-ZbxPassiveAgentStatus</c>, stopped by <c>Stop-ZbxActiveAgent</c>/<c>Stop-ZbxPassiveAgent</c> — or when the
/// runspace closes. It builds the SDK's <see cref="ZabbixActiveAgent"/> or <see cref="ZabbixPassiveAgent"/> with its builder,
/// as an application does (<see cref="ZabbixSessionHost"/>), on the module's logging, bridged to this session's own
/// <see cref="ZabbixPowerShellLoggerProvider"/> (flushed by every command that touches the session, as
/// <see cref="ZabbixConnection"/> flushes its own). A scriptblock goes into the agent's <c>Items</c>, so one registered
/// after the start is answered from the next check on.
/// </summary>
public sealed class ZabbixAgentSession : IZabbixSession, IZabbixLoggedSession
{
    private readonly ZabbixSessionHost _host;
    private readonly ZabbixItemDictionary _handlers;
    private readonly Lock _bridgeGate = new();
    private ZabbixScriptRunspace? _bridge;
    private int _stopped;

    private ZabbixAgentSession(string name, string kind, string hostname, DateTimeOffset started, ZabbixSessionHost host, IReadOnlyList<ZabbixAgentStatus> statuses, ZabbixListener? listener,
        ZabbixItemDictionary handlers)
    {
        Name = name;
        Kind = kind;
        Hostname = hostname;
        Started = started;
        _host = host;
        AgentStatus = statuses.Count > 0 ? statuses[0] : null;
        DestinationStatus = statuses;
        Listener = listener;
        _handlers = handlers;
    }

    /// <summary>
    /// The keys this session answers: the built-in keys plus every scriptblock registered on it through
    /// <see cref="RegisterHandler(string, ScriptBlock)"/> (<c>Register-ZbxAgentHandler</c> and its kind-checked
    /// wrappers). The same instance the host reads live on every check, so a registration takes effect on the
    /// session's very next dispatch.
    /// </summary>
    internal ZabbixItemDictionary Handlers => _handlers;

    /// <summary>
    /// The keys the session's host gets items for (<c>Initialize-</c>, <c>Publish-</c>, <c>Get-ZbxAgentHost</c>): every
    /// key the session answers but the bundled items, Zabbix's own keys, which take parameters the session cannot
    /// guess and whose items Zabbix's templates bring.
    /// </summary>
    internal IReadOnlyList<string> ProvisionedKeys => [.. _handlers.Keys.Where(key => !BundledKeyNames.Contains(key))];

    // The bundled families' keys, read from their public providers as an application reads them.
    private static FrozenSet<string> BundledKeyNames { get; } =
        new ZabbixItemProvider[] { new ZabbixSystemItems(), new ZabbixProcessItems(), new ZabbixFileSystemItems(), new ZabbixFileItems(), new ZabbixNetworkItems(), new ZabbixWebItems() }
            .SelectMany(provider => provider.Items).Select(item => item.KeyName).Append("agent.variant").Append("agent.hostmetadata")
            .ToFrozenSet(StringComparer.Ordinal);

    /// <summary>What <c>agent.hostname</c> answers, and (for an active agent) the host the server assigns items to — the same value <c>Initialize-ZbxAgentHost</c> defaults <c>-HostName</c> to.</summary>
    internal string Hostname { get; }

    /// <summary>
    /// What <c>Initialize-</c>/<c>Publish-ZbxAgentHost</c> provisioned on the server for this session — the host,
    /// its interface and its items — or <see langword="null"/> before either has run. Kept here, per session, never
    /// static: <c>Get-</c>/<c>Clear-ZbxAgentHost</c> read it to know
    /// exactly what belongs to this session and nothing else. A plain mutable property, not a constructor
    /// dependency: the bookkeeping is optional and starts empty for a session nobody has published yet.
    /// </summary>
    internal ZabbixAgentHostRecord? HostRecord { get; set; }

    /// <summary>
    /// Registers <paramref name="scriptBlock"/> under <paramref name="keyName"/> on this session's own dedicated
    /// <see cref="ZabbixScriptRunspace"/> (built lazily, on the first registration) — never on the caller's pipeline
    /// thread and never on the session's own loop thread, either of which the scriptblock is unsafe to run on.
    /// </summary>
    internal void RegisterHandler(string keyName, ScriptBlock scriptBlock)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(keyName);
        ArgumentNullException.ThrowIfNull(scriptBlock);
        ZabbixScriptRunspace bridge = Bridge();
        string scriptText = scriptBlock.ToString();
        _handlers[keyName] = async (key, cancellationToken) =>
        {
            try
            {
                PSDataCollection<PSObject> output = await bridge.InvokeAsync(scriptText, [key], cancellationToken).ConfigureAwait(false);
                PSObject? last = output.Count > 0 ? output[^1] : null;
                string? value = last is null ? null : LanguagePrimitives.ConvertTo<string>(last.BaseObject);
                return new ZabbixAgentAnswer(key, value, null);
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch (Exception failure) when (failure is not OutOfMemoryException)
            {
                // ZabbixScriptRunspace.Invoke already recorded the handler span/duration/outcome and logged the
                // failure (Zabbix.PowerShell.Diagnostics.ZabbixPowerShellLog.HandlerInvocationFailed); this call site
                // only turns it into the answer the SDK's check dispatch expects.
                return new ZabbixAgentAnswer(key, null, failure.Message);
            }
        };
    }

    private ZabbixScriptRunspace Bridge()
    {
        lock (_bridgeGate)
        {
            return _bridge ??= new ZabbixScriptRunspace(telemetry: _host.Telemetry, handlerKind: "agent-item", sessionContext: _host.SessionContext, module: ZabbixScriptRunspace.Manifest(typeof(ZabbixAgentSession), "Community.Zabbix.Agent"),
                log: _host.Loggers.CreateLogger("Community.Zabbix.PowerShell.Sessions.AgentHandler"));
        }
    }

    /// <summary>The running agent session of <paramref name="kind"/> named <paramref name="name"/> in <paramref name="context"/>, or <see langword="null"/>.</summary>
    /// <param name="context">The runspace's context.</param>
    /// <param name="name">The name.</param>
    /// <param name="kind"><c>active</c> or <c>passive</c>.</param>
    internal static ZabbixAgentSession? Find(ZabbixContext context, string name, string kind)
        => context.Sessions.TryGet(name, out ZabbixAgentSession? session) && session.Kind == kind ? session : null;

    /// <inheritdoc/>
    public string Name { get; }

    /// <summary><c>active</c> or <c>passive</c>.</summary>
    public string Kind { get; }

    /// <inheritdoc/>
    public DateTimeOffset Started { get; }

    /// <summary>What an active agent's send loop is doing (deliveries, drops, faults) for its first destination; <see langword="null"/> for a passive agent.</summary>
    public ZabbixAgentStatus? AgentStatus { get; }

    /// <summary>
    /// What an active agent's send loop is doing for each of its destinations, in <c>-Server</c> order: one per
    /// <c>,</c>-separated destination, each with its own connection and delivery accounting; empty for a passive agent.
    /// </summary>
    public IReadOnlyList<ZabbixAgentStatus> DestinationStatus { get; }

    /// <summary>A passive agent's listener (its local endpoint, connections in flight); <see langword="null"/> for an active agent.</summary>
    public ZabbixListener? Listener { get; }

    /// <summary>The bridge from this session's own loggers (its background loops run off any command's pipeline) to whichever command next flushes it.</summary>
    internal ZabbixPowerShellLoggerProvider Logs => _host.Logs;

    /// <inheritdoc/>
    ZabbixPowerShellLoggerProvider IZabbixLoggedSession.Logs => Logs;

    /// <summary>An active agent: the SDK's class, built by its builder, one destination per <c>ServerActive</c> element over the session's one table.</summary>
    internal static async Task<ZabbixAgentSession> StartActiveAsync(string name, Action<ZabbixActiveAgentOptions> configure, ZabbixPluginOptions? plugins, TimeProvider time,
        CancellationToken cancellationToken)
    {
        ZabbixActiveAgent? agent = null;
        ZabbixSessionHost host = await ZabbixSessionHost.StartAsync("ActiveAgent", name, time, (loggers, meters) =>
        {
            ZabbixActiveAgentBuilder builder = ZabbixActiveAgent.CreateBuilder(name)
                .Configure(configure)
                .UseLogging(loggers)
                .UseTimeProvider(time)
                .UseMeterFactory(meters);
            if (plugins is not null) builder.AddPlugins(plugins);
            agent = builder.Build();
            return new ZabbixSessionComponent(agent.StartAsync, agent.StopAsync, agent.DisposeAsync);
        }, cancellationToken).ConfigureAwait(false);
        ZabbixActiveAgent started = agent ?? throw new System.Diagnostics.UnreachableException("The session builds its agent before it returns.");
        return new ZabbixAgentSession(name, "active", started.Options.Hostname, time.GetUtcNow(), host, started.Statuses, listener: null, started.Items);
    }

    /// <summary>A passive agent: the SDK's class, built by its builder, its listener on the SDK's socket transport.</summary>
    internal static async Task<ZabbixAgentSession> StartPassiveAsync(string name, Action<ZabbixPassiveAgentOptions> configure, ZabbixPluginOptions? plugins, TimeProvider time,
        CancellationToken cancellationToken)
    {
        ZabbixPassiveAgent? agent = null;
        ZabbixSessionHost host = await ZabbixSessionHost.StartAsync("PassiveAgent", name, time, (loggers, meters) =>
        {
            ZabbixPassiveAgentBuilder builder = ZabbixPassiveAgent.CreateBuilder(name)
                .Configure(configure)
                .UseLogging(loggers)
                .UseTimeProvider(time)
                .UseMeterFactory(meters);
            if (plugins is not null) builder.AddPlugins(plugins);
            agent = builder.Build();
            return new ZabbixSessionComponent(agent.StartAsync, agent.StopAsync, agent.DisposeAsync);
        }, cancellationToken).ConfigureAwait(false);
        ZabbixPassiveAgent started = agent ?? throw new System.Diagnostics.UnreachableException("The session builds its agent before it returns.");
        return new ZabbixAgentSession(name, "passive", started.Options.Hostname, time.GetUtcNow(), host, statuses: [], started.Listener, started.Items);
    }

    /// <inheritdoc/>
    public async ValueTask DisposeAsync()
    {
        if (Interlocked.Exchange(ref _stopped, 1) != 0) return;
        try
        {
            await _host.DisposeAsync().ConfigureAwait(false);
        }
        finally
        {
            if (_bridge is not null) await _bridge.DisposeAsync().ConfigureAwait(false);
        }
    }
}
