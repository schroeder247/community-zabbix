// This Source Code Form is subject to the terms of the Mozilla Public
// License, v. 2.0. Copyright (c) 2026 schroeder247. SPDX-License-Identifier: MPL-2.0
using System.Collections.Immutable;

namespace Community.Zabbix.PowerShell;

/// <summary>
/// Holds the runspace's running sessions, by kind and name. Written under a lock (start and stop are rare), read
/// without one (a command sees one snapshot). Closing the context stops every session it holds.
/// </summary>
internal sealed class ZabbixSessions
{
    private readonly Lock _gate = new();
    private ImmutableDictionary<(Type Kind, string Name), IZabbixSession> _sessions =
        ImmutableDictionary.Create<(Type Kind, string Name), IZabbixSession>(KindAndName.Comparer);
    private bool _closed;

    internal ZabbixSessions()
    {
    }

    /// <summary>How many sessions are running.</summary>
    public int Count => Volatile.Read(ref _sessions).Count;

    /// <summary>Every running session.</summary>
    public IReadOnlyCollection<IZabbixSession> All => [.. Volatile.Read(ref _sessions).Values.OrderBy(session => session.Started)];

    /// <summary>The running sessions of kind <typeparamref name="T"/>, oldest first.</summary>
    /// <typeparam name="T">The kind.</typeparam>
    public IReadOnlyList<T> OfType<T>() where T : IZabbixSession
        => [.. Volatile.Read(ref _sessions).Where(pair => pair.Key.Kind == typeof(T)).Select(pair => (T)pair.Value).OrderBy(session => session.Started)];

    /// <summary>The session of kind <typeparamref name="T"/> named <paramref name="name"/>.</summary>
    /// <typeparam name="T">The kind.</typeparam>
    /// <param name="name">The name.</param>
    /// <param name="session">The session.</param>
    public bool TryGet<T>(string name, [System.Diagnostics.CodeAnalysis.NotNullWhen(true)] out T? session) where T : class, IZabbixSession
    {
        ArgumentNullException.ThrowIfNull(name);
        session = Volatile.Read(ref _sessions).TryGetValue((typeof(T), name), out IZabbixSession? found) ? (T)found : null;
        return session is not null;
    }

    /// <summary>Adds <paramref name="session"/>; <see langword="false"/> when one of its kind already has its name.</summary>
    /// <typeparam name="T">The kind it is found by.</typeparam>
    /// <param name="session">The session.</param>
    /// <exception cref="ObjectDisposedException">The context was closed.</exception>
    public bool TryAdd<T>(T session) where T : class, IZabbixSession
    {
        ArgumentNullException.ThrowIfNull(session);
        lock (_gate)
        {
            ObjectDisposedException.ThrowIf(_closed, this);
            if (_sessions.ContainsKey((typeof(T), session.Name))) return false;
            _sessions = _sessions.Add((typeof(T), session.Name), session);
            return true;
        }
    }

    /// <summary>Forgets <paramref name="session"/> (the caller stops it).</summary>
    /// <typeparam name="T">The kind it was added as.</typeparam>
    /// <param name="session">The session.</param>
    public bool Remove<T>(T session) where T : class, IZabbixSession
    {
        ArgumentNullException.ThrowIfNull(session);
        lock (_gate)
        {
            if (!_sessions.TryGetValue((typeof(T), session.Name), out IZabbixSession? held) || !ReferenceEquals(held, session)) return false;
            _sessions = _sessions.Remove((typeof(T), session.Name));
            return true;
        }
    }

    /// <summary>Forgets <paramref name="session"/> whatever kind it was added as (the caller stops it).</summary>
    /// <param name="session">The session.</param>
    internal bool Forget(IZabbixSession session)
    {
        ArgumentNullException.ThrowIfNull(session);
        lock (_gate)
        {
            (Type Kind, string Name)[] keys = [.. _sessions.Where(pair => ReferenceEquals(pair.Value, session)).Select(pair => pair.Key)];
            _sessions = _sessions.RemoveRange(keys);
            return keys.Length > 0;
        }
    }

    /// <summary>
    /// Stops every session: the runspace is closing or the module is being removed, and nobody is left to await them.
    /// Each stop is started here and runs to its end on its own (a watcher's poll or an agent's drain is cancelled, not
    /// abandoned). No command is left to report a failed stop to, so the failures are kept, not dropped: every stop's
    /// outcome is <see cref="Stopped"/>.
    /// </summary>
    internal void Close()
    {
        ImmutableDictionary<(Type Kind, string Name), IZabbixSession> closing;
        lock (_gate)
        {
            if (_closed) return;
            _closed = true;
            closing = _sessions;
            _sessions = _sessions.Clear();
        }
        Stopped = Task.WhenAll(closing.Values.Select(StopAsync));
    }

    /// <summary>
    /// Forgets <paramref name="session"/> and stops it, with no command to await the stop: a watch whose connection closed.
    /// The stop is kept in <see cref="Stopped"/>.
    /// </summary>
    /// <typeparam name="T">The kind it was added as.</typeparam>
    /// <param name="session">The session.</param>
    internal void Retire<T>(T session) where T : class, IZabbixSession
    {
        if (!Remove(session)) return;
        Task stopping = StopAsync(session);
        lock (_gate) Stopped = Task.WhenAll(Stopped, stopping);
    }

    /// <summary>
    /// Forgets and stops every session whose type <paramref name="module"/> declares: that sibling module (Community.Zabbix.Agent,
    /// Community.Zabbix.Proxy) is being removed while the context, and the sessions of the other modules, live on. The stops are
    /// kept in <see cref="Stopped"/>.
    /// </summary>
    /// <param name="module">The removed module's assembly.</param>
    internal void RetireFrom(System.Reflection.Assembly module)
    {
        ImmutableDictionary<(Type Kind, string Name), IZabbixSession> retiring;
        lock (_gate)
        {
            retiring = _sessions.Where(pair => pair.Value.GetType().Assembly == module).ToImmutableDictionary(KindAndName.Comparer);
            _sessions = _sessions.RemoveRange(retiring.Keys);
        }
        if (retiring.IsEmpty) return;
        Task stopping = Task.WhenAll(retiring.Values.Select(StopAsync));
        lock (_gate) Stopped = Task.WhenAll(Stopped, stopping);
    }

    // Starts the stop on the calling thread, as the caller's own await would; a failure, thrown or faulted, is the task's.
    private static async Task StopAsync(IZabbixSession session) => await session.DisposeAsync().ConfigureAwait(false);

    /// <summary>Every session's stop since <see cref="Close"/>, failed ones included (<see cref="Task.CompletedTask"/> before).</summary>
    internal Task Stopped { get; private set; } = Task.CompletedTask;

    private sealed class KindAndName : IEqualityComparer<(Type Kind, string Name)>
    {
        public static readonly KindAndName Comparer = new();

        public bool Equals((Type Kind, string Name) x, (Type Kind, string Name) y)
            => x.Kind == y.Kind && StringComparer.OrdinalIgnoreCase.Equals(x.Name, y.Name);

        public int GetHashCode((Type Kind, string Name) obj) => HashCode.Combine(obj.Kind, StringComparer.OrdinalIgnoreCase.GetHashCode(obj.Name));
    }
}
