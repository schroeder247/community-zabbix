// This Source Code Form is subject to the terms of the Mozilla Public
// License, v. 2.0. Copyright (c) 2026 schroeder247. SPDX-License-Identifier: MPL-2.0
using System.Collections;
using System.Management.Automation;
using System.Text.Json;
using Community.Zabbix.Listening;
using Community.Zabbix.PowerShell.Diagnostics;
using Community.Zabbix.PowerShell.Sessions;
using Community.Zabbix.Proxy;
using Microsoft.Extensions.Logging;

namespace Community.Zabbix.PowerShell.Commands;

/// <summary>
/// Represents a passive proxy's listener kept running as a session: the server comes to it. Built as an application on the
/// SDK builds one, with <see cref="ZabbixPassiveProxy.CreateBuilder"/> on the session's logging and meters
/// (<see cref="ZabbixSessionHost"/>). The proxy's <c>OnConfig</c>, <c>OnStats</c> and <c>AddProvider</c> answer with what the
/// <c>Register-ZbxPassiveProxy*</c> commands register while the session runs.
/// </summary>
public sealed class ZabbixPassiveProxySession : IZabbixSession, IZabbixLoggedSession
{
    private readonly ZabbixSessionHost _session;
    private readonly ZabbixPassiveProxy _host;
    private readonly Registered _registered;
    private readonly Lock _bridgeGate = new();
    private ZabbixScriptRunspace? _bridge;
    private int _stopped;

    private ZabbixPassiveProxySession(string name, DateTimeOffset started, ZabbixSessionHost session, ZabbixPassiveProxy host, Registered registered)
    {
        Name = name;
        Started = started;
        _session = session;
        _host = host;
        _registered = registered;
    }

    private ZabbixScriptRunspace Bridge()
    {
        lock (_bridgeGate)
        {
            return _bridge ??= new ZabbixScriptRunspace(telemetry: _session.Telemetry, handlerKind: "passive-proxy", sessionContext: _session.SessionContext, module: ZabbixScriptRunspace.Manifest(typeof(ZabbixPassiveProxySession), "Community.Zabbix.Proxy"),
                log: _session.Loggers.CreateLogger("Community.Zabbix.PowerShell.Sessions.PassiveProxyHandler"));
        }
    }

    /// <summary>
    /// Registers <paramref name="scriptBlock"/> as what answers this session's proxy's <c>OnConfig</c>: invoked with the
    /// incoming and held <see cref="ZabbixProxyConfig"/>; a non-empty string result (or a thrown error) vetoes the
    /// configuration, anything else accepts it. A later registration replaces it.
    /// </summary>
    internal void RegisterConfigHandler(ScriptBlock scriptBlock)
    {
        ArgumentNullException.ThrowIfNull(scriptBlock);
        ZabbixScriptRunspace bridge = Bridge();
        string scriptText = scriptBlock.ToString();
        _registered.Config = async (incoming, held, cancellationToken) =>
        {
            try
            {
                PSDataCollection<PSObject> output = await bridge.InvokeAsync(scriptText, [incoming, held], cancellationToken).ConfigureAwait(false);
                PSObject? last = output.Count > 0 ? output[^1] : null;
                string? veto = last is null ? null : LanguagePrimitives.ConvertTo<string>(last.BaseObject);
                return string.IsNullOrEmpty(veto) ? null : veto;
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch (Exception failure) when (failure is not OutOfMemoryException)
            {
                return failure.Message;
            }
        };
    }

    /// <summary>
    /// Registers <paramref name="scriptBlock"/> as one more of the providers this session's proxy asks before each pull:
    /// invoked with the held <see cref="ZabbixProxyConfig"/>, its last output converted to a <see cref="ZabbixProxyDataPage"/>
    /// (<see langword="null"/> or no output contributes nothing this turn).
    /// </summary>
    internal void RegisterDataProvider(ScriptBlock scriptBlock)
    {
        ArgumentNullException.ThrowIfNull(scriptBlock);
        ZabbixScriptRunspace bridge = Bridge();
        string scriptText = scriptBlock.ToString();
        _registered.AddProvider(async (config, cancellationToken) =>
        {
            PSDataCollection<PSObject> output = await bridge.InvokeAsync(scriptText, [config], cancellationToken).ConfigureAwait(false);
            PSObject? last = output.Count > 0 ? output[^1] : null;
            return last?.BaseObject as ZabbixProxyDataPage;
        });
    }

    /// <summary>
    /// Registers <paramref name="scriptBlock"/> as what answers this session's proxy's <c>OnStats</c>: invoked with the
    /// <c>zabbix.stats</c> request's raw JSON text, its last output (an <see cref="IDictionary"/>, typically a hashtable)
    /// converted key by key into the reply's <c>data</c> object. A later registration replaces it.
    /// </summary>
    internal void RegisterStatsProvider(ScriptBlock scriptBlock)
    {
        ArgumentNullException.ThrowIfNull(scriptBlock);
        ZabbixScriptRunspace bridge = Bridge();
        string scriptText = scriptBlock.ToString();
        _registered.Stats = async (request, cancellationToken) =>
        {
            PSDataCollection<PSObject> output = await bridge.InvokeAsync(scriptText, [System.Text.Encoding.UTF8.GetString(request.Span)], cancellationToken).ConfigureAwait(false);
            PSObject? last = output.Count > 0 ? output[^1] : null;
            if (last?.BaseObject is not IDictionary map) return null;
            var data = new Dictionary<string, JsonElement>(StringComparer.Ordinal);
            foreach (DictionaryEntry entry in map)
                data[entry.Key.ToString() ?? string.Empty] = JsonSerializer.SerializeToElement(entry.Value);
            return data;
        };
    }

    /// <inheritdoc/>
    public string Name { get; }

    /// <inheritdoc/>
    public DateTimeOffset Started { get; }

    /// <summary>The listener (its local endpoint, connections in flight).</summary>
    public ZabbixListener? Listener => _host.Listener;

    /// <summary>The configuration held now (merged from the server's <c>proxy config</c> pushes).</summary>
    public ZabbixProxyConfig Config => _host.Config;

    /// <summary>The store the server's <c>proxy data</c> pulls page from; where <c>Send-ZbxPassiveProxyHistory</c> writes.</summary>
    internal Storage.ZabbixStore<ZabbixValue>? Store => _host.Store;

    internal ZabbixPowerShellLoggerProvider Logs => _session.Logs;

    /// <inheritdoc/>
    ZabbixPowerShellLoggerProvider IZabbixLoggedSession.Logs => _session.Logs;

    internal static async Task<ZabbixPassiveProxySession> StartAsync(string name, Action<ZabbixProxyOptions> configure, TimeProvider time, CancellationToken cancellationToken)
    {
        var registered = new Registered();
        ZabbixPassiveProxy? proxy = null;
        ZabbixSessionHost session = await ZabbixSessionHost.StartAsync("PassiveProxy", name, time, (loggers, meters) =>
        {
            ILogger log = loggers.CreateLogger("Community.Zabbix.PowerShell.Sessions.PassiveProxy");
            proxy = ZabbixPassiveProxy.CreateBuilder(name)
                .Configure(configure)
                .OnConfig(registered.ConfigAsync)
                .OnStats(registered.StatsAsync)
                .AddProvider((config, cancellation) => registered.ProvideAsync(config, failure => ZabbixPowerShellLog.SessionTickFailed(log, "PassiveProxy", name, failure), cancellation))
                .UseLogging(loggers)
                .UseTimeProvider(time)
                .UseMeterFactory(meters)
                .Build();
            return new ZabbixSessionComponent(proxy.StartAsync, proxy.StopAsync, proxy.DisposeAsync);
        }, cancellationToken).ConfigureAwait(false);
        return new ZabbixPassiveProxySession(name, time.GetUtcNow(), session, proxy ?? throw new System.Diagnostics.UnreachableException("The session builds its proxy before it returns."), registered);
    }

    /// <inheritdoc/>
    public async ValueTask DisposeAsync()
    {
        if (Interlocked.Exchange(ref _stopped, 1) != 0) return;
        try
        {
            await _session.DisposeAsync().ConfigureAwait(false);
        }
        finally
        {
            if (_bridge is not null) await _bridge.DisposeAsync().ConfigureAwait(false);
        }
    }

    // What the Register-ZbxPassiveProxy* commands register while the session runs. The proxy is built once, with handlers
    // that answer from here, so a scriptblock registered after the start answers the next request.
    private sealed class Registered
    {
        private readonly Lock _gate = new();
        private readonly List<ZabbixProxyDataCallback> _providers = [];
        private ZabbixPassiveProxyConfigCallback? _config;
        private ZabbixProxyStatsCallback? _stats;

        public ZabbixPassiveProxyConfigCallback? Config
        {
            get => Volatile.Read(ref _config);
            set => Volatile.Write(ref _config, value);
        }

        public ZabbixProxyStatsCallback? Stats
        {
            get => Volatile.Read(ref _stats);
            set => Volatile.Write(ref _stats, value);
        }

        public void AddProvider(ZabbixProxyDataCallback provider)
        {
            lock (_gate) _providers.Add(provider);
        }

        // Nothing registered accepts the configuration, as a proxy without OnConfig does.
        public ValueTask<string?> ConfigAsync(ZabbixProxyConfig incoming, ZabbixProxyConfig held, CancellationToken cancellationToken)
            => Config is { } config ? config(incoming, held, cancellationToken) : ValueTask.FromResult<string?>(null);

        // Nothing registered answers an empty data object, as a proxy without OnStats does.
        public ValueTask<IReadOnlyDictionary<string, JsonElement>?> StatsAsync(ReadOnlyMemory<byte> request, CancellationToken cancellationToken)
            => Stats is { } stats ? stats(request, cancellationToken) : ValueTask.FromResult<IReadOnlyDictionary<string, JsonElement>?>(null);

        // Every registered scriptblock's page, merged as the proxy merges its providers' pages; one that throws is logged and
        // skipped, so it doesn't cost the others theirs.
        public async ValueTask<ZabbixProxyDataPage?> ProvideAsync(ZabbixProxyConfig config, Action<Exception> onFailed, CancellationToken cancellationToken)
        {
            IReadOnlyList<ZabbixProxyDataCallback> providers;
            lock (_gate) providers = [.. _providers];
            if (providers.Count == 0) return null;
            IReadOnlyList<ZabbixProxyDataPage> pages = await ZabbixProxyDataProviders.AskAsync(providers, config, onFailed, cancellationToken).ConfigureAwait(false);
            return pages.Count == 0 ? null : ZabbixProxyDataProviders.Merge(ZabbixProxyDataPage.Empty, pages);
        }
    }
}
