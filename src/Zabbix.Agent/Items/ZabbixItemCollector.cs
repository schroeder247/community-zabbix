// This Source Code Form is subject to the terms of the Mozilla Public
// License, v. 2.0. Copyright (c) 2026 schroeder247. SPDX-License-Identifier: MPL-2.0
using System.ComponentModel;
using System.Text.RegularExpressions;

namespace Community.Zabbix.Items;

/// <summary>
/// Samples what the bundled keys report over time, once a second as Zabbix's collector does: a processor's times
/// (<c>system.cpu.util</c>), a process query's (<c>proc.cpu.util</c>) and a block device's counters
/// (<c>vfs.dev.read</c>, <c>vfs.dev.write</c>). It runs only while a key is asking: the timer starts at the first
/// request and stops when every series has gone unrequested (docs/adr/0009-bundled-items.md §5).
/// </summary>
/// <remarks>
/// An agent's container owns one and disposes of it with the agent. A table built without a container
/// (<c>ZabbixItemDictionary.BuiltIn</c>) has no owner to dispose of it, so the timer holds the collector weakly: once
/// nothing holds the table the collector can be collected, and the next tick stops the timer.
/// </remarks>
internal sealed class ZabbixItemCollector : IDisposable
{
    private static readonly TimeSpan s_period = TimeSpan.FromSeconds(1);

    private readonly Lock _gate = new();
    private readonly IZabbixSamples[] _families;
    private ITimer? _timer;
    private int _sampling;
    private bool _disposed;

    public ZabbixItemCollector(ZabbixHostFiles files, TimeProvider? time = null)
    {
        Files = files;
        Time = time ?? TimeProvider.System;
        Processors = new ZabbixProcessorSamples(this);
        Processes = new ZabbixProcessSamples(this);
        Devices = new ZabbixDeviceSamples(this);
        _families = [Processors, Processes, Devices];
    }

    public ZabbixHostFiles Files { get; }

    public TimeProvider Time { get; }

    public ZabbixProcessorSamples Processors { get; }

    public ZabbixProcessSamples Processes { get; }

    public ZabbixDeviceSamples Devices { get; }

    /// <summary>Whether the timer is running.</summary>
    public bool IsSampling
    {
        get
        {
            lock (_gate) return _timer is not null;
        }
    }

    /// <summary>Starts the timer, unless it is running or the collector is disposed.</summary>
    public void EnsureSampling()
    {
        lock (_gate)
        {
            if (_timer is not null || _disposed) return;
            var beat = new Beat(new WeakReference<ZabbixItemCollector>(this));
            _timer = beat.Timer = Time.CreateTimer(static state => (state as Beat)?.Tick(), beat, s_period, s_period);
        }
    }

    /// <inheritdoc/>
    public void Dispose()
    {
        lock (_gate)
        {
            _disposed = true;
            _timer?.Dispose();
            _timer = null;
        }
    }

    private void Tick()
    {
        // A tick that takes longer than the period does not overlap the next, as Zabbix's one collector process cannot.
        if (Interlocked.Exchange(ref _sampling, 1) != 0) return;
        try
        {
            long now = Time.GetTimestamp();
            foreach (IZabbixSamples family in _families)
            {
                try
                {
                    family.Sample(now);
                }
                catch (Exception refused) when (refused is IOException or UnauthorizedAccessException or InvalidOperationException or Win32Exception or RegexMatchTimeoutException)
                {
                    // The operating system refused this round: the next tick asks again. A timer's callback has nobody to throw to.
                }
            }
            lock (_gate)
            {
                if (_timer is not null && Array.TrueForAll(_families, family => family.IsEmpty))
                {
                    _timer.Dispose();
                    _timer = null;
                }
            }
        }
        finally
        {
            Volatile.Write(ref _sampling, 0);
        }
    }

    // What the timer holds: the collector weakly, so a table nobody holds does not sample forever.
    private sealed class Beat(WeakReference<ZabbixItemCollector> collector)
    {
        public ITimer? Timer { get; set; }

        public void Tick()
        {
            if (collector.TryGetTarget(out ZabbixItemCollector? target)) target.Tick();
            else Timer?.Dispose();
        }
    }
}
