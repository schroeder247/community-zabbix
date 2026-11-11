// This Source Code Form is subject to the terms of the Mozilla Public
// License, v. 2.0. Copyright (c) 2026 schroeder247. SPDX-License-Identifier: MPL-2.0
namespace Community.Zabbix.Listening;

/// <summary>
/// One agent's data session: the highest value id it has delivered (<c>zbx_session_t.last_id</c>), which a re-sent
/// upload is de-duplicated against.
/// </summary>
internal sealed class ZabbixAgentSession
{
    private readonly Lock _gate = new();
    private long _lastId;
    private long _lastAccess;

    /// <summary>The highest value id delivered; 0 before the first.</summary>
    public long LastId
    {
        get { lock (_gate) return _lastId; }
    }

    /// <summary>When the session was last used, as a <see cref="TimeProvider.GetTimestamp"/> reading.</summary>
    public long LastAccess => Interlocked.Read(ref _lastAccess);

    public void Touch(long now) => Interlocked.Exchange(ref _lastAccess, now);

    /// <summary>Moves <see cref="LastId"/> to <paramref name="id"/>, unless it is already higher (a network anomaly upstream logs and ignores).</summary>
    /// <param name="id">The last value id of a frame.</param>
    /// <param name="held">The id held when it was not moved.</param>
    /// <returns>Whether it moved.</returns>
    public bool TryAdvance(long id, out long held)
    {
        lock (_gate)
        {
            held = _lastId;
            if (_lastId > id) return false;
            _lastId = id;
            return true;
        }
    }
}
