// This Source Code Form is subject to the terms of the Mozilla Public
// License, v. 2.0. Copyright (c) 2026 schroeder247. SPDX-License-Identifier: MPL-2.0
using System.Collections.Immutable;
using System.Management.Automation.Runspaces;
using System.Runtime.CompilerServices;
using Community.Zabbix.PowerShell.Diagnostics;
using Microsoft.Extensions.Caching.Memory;

namespace Community.Zabbix.PowerShell;

/// <summary>
/// Holds the state of one runspace: the servers it is connected to, by name, and the current one. A cmdlet holds
/// nothing; it reads the context of the runspace it runs in. Each runspace — a console, a job, a
/// <c>ForEach-Object -Parallel</c> branch — has its own.
/// </summary>
/// <remarks>
/// Kept in a <see cref="ConditionalWeakTable{TKey, TValue}"/> keyed on the <see cref="Runspace"/>, not in the module's
/// <c>SessionState</c>: a module imported by its dll path has none, <c>PSCmdlet.SessionState</c> is the caller's scope,
/// and neither an <see cref="System.Management.Automation.IArgumentCompleter"/> nor an
/// <see cref="System.Management.Automation.ArgumentTransformationAttribute"/> can reach the module's state without a
/// command lookup per call. <see cref="Runspace.DefaultRunspace"/> is set on the pipeline thread for all of them, so
/// one key serves cmdlets, completers, transformations and module cleanup. The weak key lets a dropped runspace's
/// context be collected; a closing runspace (<see cref="Runspace.StateChanged"/>) and <c>Remove-Module</c>
/// (<see cref="ZabbixModule"/>) dispose it.
/// </remarks>
public sealed class ZabbixContext : IDisposable
{
    private static readonly ConditionalWeakTable<Runspace, ZabbixContext> s_contexts = [];

    private readonly Runspace _runspace;
    // Written under the gate (connect and disconnect are rare), read without it: a command sees one snapshot.
    private readonly Lock _gate = new();
    private ImmutableDictionary<string, ZabbixConnection> _servers = ImmutableDictionary.Create<string, ZabbixConnection>(StringComparer.OrdinalIgnoreCase);
    private ZabbixConnection? _current;
    private bool _disposed;

    private ZabbixContext(Runspace runspace)
    {
        _runspace = runspace;
        ExportsTelemetry = ZabbixModuleTelemetry.IsRequested();
        Completions = new MemoryCache(new MemoryCacheOptions { SizeLimit = 64 });
        Sessions = new ZabbixSessions();
        runspace.StateChanged += OnStateChanged;
    }

    /// <summary>The servers, by the name the session gave them (case-insensitive).</summary>
    public IReadOnlyDictionary<string, ZabbixConnection> Servers => Volatile.Read(ref _servers);

    /// <summary>The server a command talks to when it names none; <see langword="null"/> before the first connect.</summary>
    public ZabbixConnection? Current => Volatile.Read(ref _current);

    /// <summary>
    /// The number of sessions (watches, agents, proxies) running in this runspace; <c>Get-ZbxWatch</c>,
    /// <c>Get-ZbxActiveAgentStatus</c> and the other session commands list them.
    /// </summary>
    public int SessionCount => Sessions.Count;

    /// <summary>Whether connections export telemetry over OTLP: decided when the module was imported.</summary>
    public bool ExportsTelemetry { get; }

    /// <summary>
    /// The clock every connection and session of this runspace runs on: <see cref="TimeProvider.System"/>, or a test's
    /// fake set before it connects, so a test drives time instead of waiting for it.
    /// </summary>
    internal TimeProvider Time { get; set; } = TimeProvider.System;

    /// <summary>
    /// Opens a page in the user's browser (<c>Show-ZbxPage</c>): <see langword="null"/> when it was handed to one, else why
    /// not. <see cref="ZabbixBrowser.Open"/>, or a test's capture set before the command runs, so no test starts a browser.
    /// </summary>
    internal Func<Uri, string?> OpenPage { get; set; } = ZabbixBrowser.Open;

    private long _proxySequence;

    /// <summary>
    /// The next sequence id for a proxy history record this runspace builds (<c>New-ZbxHistoryValue</c>,
    /// <c>New-ZbxHistoryDataEntry</c>), in order: per context, never a static counter every runspace shares.
    /// </summary>
    internal long NextProxySequence() => Interlocked.Increment(ref _proxySequence);

    /// <summary>What this runspace keeps running between commands (watchers, agents, proxies); stopped when the context closes.</summary>
    internal ZabbixSessions Sessions { get; }

    /// <summary>The name completion cache: one entry per server and entity.</summary>
    internal IMemoryCache Completions { get; }

    /// <summary>The context of <paramref name="runspace"/>, created on first use.</summary>
    /// <param name="runspace">The runspace; <see cref="Runspace.DefaultRunspace"/> on the pipeline thread.</param>
    internal static ZabbixContext Of(Runspace runspace)
    {
        ArgumentNullException.ThrowIfNull(runspace);
        return s_contexts.GetValue(runspace, static runspace => new ZabbixContext(runspace));
    }

    /// <summary>The context of the runspace on the calling pipeline thread, if one was created.</summary>
    /// <param name="context">The context.</param>
    internal static bool TryGetCurrent([System.Diagnostics.CodeAnalysis.NotNullWhen(true)] out ZabbixContext? context)
    {
        context = null;
        return Runspace.DefaultRunspace is { } runspace && s_contexts.TryGetValue(runspace, out context);
    }

    /// <summary>The context of <paramref name="runspace"/>, if one was created (a sibling module's removal, which never creates one).</summary>
    /// <param name="runspace">The runspace.</param>
    /// <param name="context">The context.</param>
    internal static bool TryGet(Runspace runspace, [System.Diagnostics.CodeAnalysis.NotNullWhen(true)] out ZabbixContext? context)
        => s_contexts.TryGetValue(runspace, out context);

    /// <summary>Forgets the context of <paramref name="runspace"/> and closes its connections.</summary>
    /// <param name="runspace">The runspace.</param>
    internal static void Close(Runspace runspace)
    {
        if (s_contexts.TryGetValue(runspace, out ZabbixContext? context))
        {
            s_contexts.Remove(runspace);
            context.Dispose();
        }
    }

    /// <summary>
    /// Adds <paramref name="connection"/> under its name, closing a connection it replaces (safely: an in-flight call on
    /// the old one finishes first), and makes it current unless <paramref name="keepCurrent"/>.
    /// </summary>
    /// <param name="connection">The connection.</param>
    /// <param name="keepCurrent">Whether the current server stays as it was (a first connection always becomes current).</param>
    internal void Add(ZabbixConnection connection, bool keepCurrent = false)
    {
        ArgumentNullException.ThrowIfNull(connection);
        ZabbixConnection? replaced;
        lock (_gate)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            _servers.TryGetValue(connection.Name, out replaced);
            _servers = _servers.SetItem(connection.Name, connection);
            if (!keepCurrent || _current is null || ReferenceEquals(_current, replaced)) _current = connection;
        }
        if (!ReferenceEquals(replaced, connection) && replaced is not null) Close(replaced);
    }

    // A closed connection takes the watches that poll through it: they cannot poll a disposed container, and a watch
    // listed after its server is gone would say it is running when it is not.
    private void Close(ZabbixConnection connection)
    {
        connection.Dispose();
        foreach (Commands.ZabbixWatch watch in Sessions.OfType<Commands.ZabbixWatch>())
            if (ReferenceEquals(watch.Connection, connection)) Sessions.Retire(watch);
    }

    /// <summary>Removes and closes the server named <paramref name="name"/>; the current server becomes none if it was that one.</summary>
    /// <param name="name">The name.</param>
    /// <param name="connection">The connection removed.</param>
    internal bool TryRemove(string name, [System.Diagnostics.CodeAnalysis.NotNullWhen(true)] out ZabbixConnection? connection)
    {
        ArgumentNullException.ThrowIfNull(name);
        lock (_gate)
        {
            if (!_servers.TryGetValue(name, out connection)) return false;
            _servers = _servers.Remove(name);
            if (ReferenceEquals(_current, connection)) _current = null;
        }
        Close(connection);
        return true;
    }

    /// <summary>The server named <paramref name="name"/>, or the current one when <paramref name="name"/> is <see langword="null"/>.</summary>
    /// <param name="name">The name, or <see langword="null"/>.</param>
    /// <param name="connection">The connection.</param>
    internal bool TryGet(string? name, [System.Diagnostics.CodeAnalysis.NotNullWhen(true)] out ZabbixConnection? connection)
    {
        if (name is null)
        {
            connection = Current;
            return connection is not null;
        }
        return Servers.TryGetValue(name, out connection);
    }

    private void OnStateChanged(object? sender, RunspaceStateEventArgs e)
    {
        if (e.RunspaceStateInfo.State is RunspaceState.Closed or RunspaceState.Broken) Close(_runspace);
    }

    /// <summary>Stops every session and closes every connection.</summary>
    public void Dispose()
    {
        ImmutableDictionary<string, ZabbixConnection> servers;
        lock (_gate)
        {
            if (_disposed) return;
            _disposed = true;
            servers = _servers;
            _servers = _servers.Clear();
            _current = null;
        }
        _runspace.StateChanged -= OnStateChanged;
        Sessions.Close();
        foreach (ZabbixConnection connection in servers.Values) connection.Dispose();
        Completions.Dispose();
    }
}
