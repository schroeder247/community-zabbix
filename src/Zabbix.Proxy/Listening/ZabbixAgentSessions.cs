// This Source Code Form is subject to the terms of the Mozilla Public
// License, v. 2.0. Copyright (c) 2026 schroeder247. SPDX-License-Identifier: MPL-2.0
using System.Collections.Concurrent;

namespace Community.Zabbix.Listening;

/// <summary>
/// Tracks the data sessions of the agents a receiver hears from, by host and session id, forgotten 25 hours after their last use
/// as upstream forgets them (<c>zbx_dc_cleanup_sessions</c>: <c>SEC_PER_DAY + SEC_PER_HOUR</c>, "more than
/// <c>MAX_ACTIVE_CHECKS_REFRESH_FREQUENCY</c>"). A restarted agent opens a new session, so the table holds one entry per
/// agent run of the last day: bounded by the agents, not by the values.
/// </summary>
/// <remarks>
/// Owned: <c>Microsoft.Extensions.Caching.Memory.MemoryCache</c> with a sliding expiration is the platform's expiring
/// map, but its clock is <c>ISystemClock</c>, not <see cref="TimeProvider"/>, so a test could not move it, and it is
/// one more disposable for a few dozen bytes per agent. The sweep runs
/// on use, at most once an hour.
/// </remarks>
internal sealed class ZabbixAgentSessions(TimeProvider time)
{
    /// <summary>How long an unused session is kept.</summary>
    public static readonly TimeSpan Expiry = TimeSpan.FromHours(25);

    private static readonly TimeSpan s_sweepEvery = TimeSpan.FromHours(1);

    private readonly ConcurrentDictionary<(ulong HostId, string Session), ZabbixAgentSession> _sessions = new();
    private long _swept = time.GetTimestamp();

    /// <summary>How many sessions are held.</summary>
    public int Count => _sessions.Count;

    /// <summary>
    /// The most sessions held. The key is the agent's own token, so an agent (or anything that speaks for one) that
    /// sends a new token per frame would otherwise grow the table until the sweep, an hour later. Upstream keeps one
    /// session per host and token too, in its configuration cache; past the cap a session is not kept and the frame's
    /// values are taken without de-duplication, as a 4.2 agent's are.
    /// </summary>
    public const int MaxSessions = 65_536;

    /// <summary>The session <paramref name="session"/> (an agent's 32-character session id) on <paramref name="hostId"/>, opened on first use; <see langword="null"/> when the table is full.</summary>
    public ZabbixAgentSession? Get(ulong hostId, string session)
    {
        long now = time.GetTimestamp();
        Sweep(now);
        if (!_sessions.TryGetValue((hostId, session), out ZabbixAgentSession? opened))
        {
            if (_sessions.Count >= MaxSessions) return null;
            opened = _sessions.GetOrAdd((hostId, session), static _ => new ZabbixAgentSession());
        }
        opened.Touch(now);
        return opened;
    }

    private void Sweep(long now)
    {
        long swept = Interlocked.Read(ref _swept);
        if (time.GetElapsedTime(swept, now) < s_sweepEvery || Interlocked.CompareExchange(ref _swept, now, swept) != swept) return;
        foreach (KeyValuePair<(ulong HostId, string Session), ZabbixAgentSession> entry in _sessions)
            if (time.GetElapsedTime(entry.Value.LastAccess, now) >= Expiry) _sessions.TryRemove(entry);
    }
}
