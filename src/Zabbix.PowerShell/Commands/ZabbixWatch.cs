// This Source Code Form is subject to the terms of the Mozilla Public
// License, v. 2.0. Copyright (c) 2026 schroeder247. SPDX-License-Identifier: MPL-2.0
using Community.Zabbix.Diagnostics;
using Community.Zabbix.PowerShell.Diagnostics;

namespace Community.Zabbix.PowerShell.Commands;

/// <summary>
/// Represents a running <see cref="IZabbixWatcher"/>, kept in the runspace's <see cref="ZabbixContext.Sessions"/> by name
/// (README, "anything that keeps running between commands"): started by <c>Watch-ZbxItem</c>, found by
/// <c>Get-ZbxWatch</c>, read by <c>Receive-ZbxWatch</c>, stopped by <c>Stop-ZbxWatch</c> — or when the runspace closes.
/// </summary>
public sealed class ZabbixWatch : IZabbixSession
{
    private readonly IZabbixWatcher _watcher;
    private readonly ZabbixPowerShellTelemetry _telemetry;

    internal ZabbixWatch(string name, IZabbixWatcher watcher, ZabbixConnection connection, TimeProvider time)
    {
        Name = name;
        _watcher = watcher;
        Connection = connection;
        Started = time.GetUtcNow();
        _telemetry = new ZabbixPowerShellTelemetry(meters: null, time);
        _telemetry.FinishSessionPhase(_telemetry.StartSessionPhase("Watch", name, "start"));
    }

    /// <inheritdoc/>
    public string Name { get; }

    /// <inheritdoc/>
    public DateTimeOffset Started { get; }

    /// <summary>The connection the watch polls through; it stops when that connection closes.</summary>
    internal ZabbixConnection Connection { get; }

    /// <summary>The plan being observed.</summary>
    public ZabbixWatchPlan Plan => _watcher.Plan;

    /// <summary>The latest value of every item seen so far, by host and key.</summary>
    public IReadOnlyDictionary<(string Host, ZabbixItemKey Key), ZabbixValue> Current => _watcher.Current;

    /// <summary>How many ticks read what they had to read.</summary>
    public long SucceededPolls => _watcher.SucceededPolls;

    /// <summary>How many ticks failed (each logged and skipped).</summary>
    public long FailedPolls => _watcher.FailedPolls;

    /// <summary>How many values readers have lost to their buffers' bounds, in total.</summary>
    public long Dropped => _watcher.DroppedCount;

    /// <summary>The watcher itself, for <c>Receive-ZbxWatch</c> to enumerate (each enumeration is its own reader).</summary>
    internal IZabbixWatcher Watcher => _watcher;

    /// <inheritdoc/>
    public async ValueTask DisposeAsync()
    {
        ZabbixOperation stopped = _telemetry.StartSessionPhase("Watch", Name, "stop");
        await _watcher.DisposeAsync().ConfigureAwait(false);
        _telemetry.FinishSessionPhase(stopped);
        _telemetry.Dispose();
    }
}
