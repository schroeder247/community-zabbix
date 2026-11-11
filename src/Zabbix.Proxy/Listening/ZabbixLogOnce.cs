// This Source Code Form is subject to the terms of the Mozilla Public
// License, v. 2.0. Copyright (c) 2026 schroeder247. SPDX-License-Identifier: MPL-2.0
using System.Collections.Concurrent;
using Community.Zabbix.Diagnostics;

namespace Community.Zabbix.Listening;

/// <summary>
/// Decides whether an event a peer can repeat at will (a refused connection, a refused value) is logged: the first time
/// each key (a peer and a reason, a host and an item key) is seen within a window, so an operator still sees every
/// distinct problem while a flood of one is said once. Past <c>capacity</c> distinct keys in a window, the rest are
/// logged at the 1st, 10th, 100th, … one, as a store logs its drops. Every occurrence is still counted on its meter by
/// the caller; this only decides the log line.
/// </summary>
/// <typeparam name="TKey">What makes two occurrences the same.</typeparam>
internal sealed class ZabbixLogOnce<TKey>
    where TKey : notnull
{
    private readonly ConcurrentDictionary<TKey, byte> _seen = new();
    private readonly int _capacity;
    private readonly TimeSpan _window;
    private readonly TimeProvider _time;
    private long _windowStarted;
    private long _beyond;

    /// <summary>A gate holding at most <paramref name="capacity"/> keys, forgotten every <paramref name="window"/>.</summary>
    public ZabbixLogOnce(int capacity, TimeSpan window, TimeProvider time)
    {
        _capacity = capacity;
        _window = window;
        _time = time;
        _windowStarted = time.GetTimestamp();
    }

    /// <summary>Whether this occurrence of <paramref name="key"/> is logged.</summary>
    public bool ShouldLog(TKey key)
    {
        long started = Volatile.Read(ref _windowStarted);
        if (_time.GetElapsedTime(started) >= _window && Interlocked.CompareExchange(ref _windowStarted, _time.GetTimestamp(), started) == started)
        {
            _seen.Clear();                                                                 // a new window: a problem that came back is said again
            Interlocked.Exchange(ref _beyond, 0);
        }
        if (_seen.ContainsKey(key)) return false;
        if (_seen.Count < _capacity) return _seen.TryAdd(key, 0);
        return ZabbixLogSampling.IsPowerOfTen(Interlocked.Increment(ref _beyond));
    }
}
