// This Source Code Form is subject to the terms of the Mozilla Public
// License, v. 2.0. Copyright (c) 2026 schroeder247. SPDX-License-Identifier: MPL-2.0
using System.ComponentModel;
using System.Diagnostics;
using System.Text.RegularExpressions;

namespace Community.Zabbix.Items;

/// <summary>
/// The processor time of the processes a query matches, by name, user and command line: what <c>proc.cpu.util</c>
/// reports (<c>common/procstat.c</c>). Each series adds up, sample by sample, what the processes present in both of two
/// samples spent between them, so one that starts or ends between samples does not jump the total. A query goes
/// unrequested after a day, and at most 1024 are followed, as in Zabbix's agent.
/// </summary>
internal sealed class ZabbixProcessSamples(ZabbixItemCollector collector)
    : ZabbixSamples<(string Name, string User, string CommandLine)>(collector, 2, TimeSpan.FromDays(1), 1024)
{
    protected override string FullReason => "The agent already follows as many process queries as it can.";

    protected override string FailedReason => "Cannot read cpu utilization data.";

    /// <summary>
    /// The percentage of one processor the query's processes used over the last <paramref name="seconds"/>: two
    /// processors' full use is 200. One decimal, as Zabbix's agent rounds it.
    /// </summary>
    public async ValueTask<double> UtilizationAsync(string name, string user, string commandLine, bool countUser, bool countSystem, int seconds, CancellationToken cancellationToken)
    {
        ZabbixSampleSeries series = await AskAsync((name, user, commandLine), cancellationToken).ConfigureAwait(false);
        if (series.Window(seconds) is not { } window) throw new ZabbixItemException(FailedReason);
        double elapsed = window.Elapsed.TotalSeconds;
        if (elapsed <= 0) return 0;
        ulong ticks = (countUser ? window.Latest[0] - window.Start[0] : 0) + (countSystem ? window.Latest[1] - window.Start[1] : 0);
        double perSecond = Collector.Files.IsLinux ? ZabbixLinuxProcess.TicksPerSecond : TimeSpan.TicksPerSecond;
        return Math.Round(ticks / perSecond / elapsed * 1000, MidpointRounding.AwayFromZero) / 10;
    }

    /// <inheritdoc/>
    protected override object? CreateState((string Name, string User, string CommandLine) key)
    {
        ZabbixHostFiles files = Collector.Files;
        return new Query(key.Name, key.User.Length > 0 && files.IsLinux ? ZabbixLinuxProcess.UserId(files, key.User) : null, key.User.Length > 0,
            key.CommandLine.Length > 0 ? ZabbixItemSet.Expression(key.CommandLine, 3) : null);
    }

    /// <inheritdoc/>
    protected override void Read(long now, IReadOnlyList<KeyValuePair<(string Name, string User, string CommandLine), ZabbixSampleSeries>> series)
    {
        // One look at the process list a round, however many queries read it (zbx_procstat_collect).
        List<ZabbixLinuxProcess>? processes = Collector.Files.IsLinux ? [.. ZabbixLinuxProcess.All(Collector.Files)] : null;
        foreach ((_, ZabbixSampleSeries one) in series)
        {
            if (one.State is not Query query) continue;
            lock (query)
            {
                var seen = new Dictionary<int, (ulong User, ulong System, ulong Start)>();
                if (processes is not null) ReadLinux(processes, query, seen);
                else ReadManaged(query, seen);
                foreach ((int pid, (ulong user, ulong system, ulong start)) in seen)
                {
                    if (!query.Last.TryGetValue(pid, out (ulong User, ulong System, ulong Start) last) || last.Start != start) continue;
                    if (user >= last.User) query.User += user - last.User;
                    if (system >= last.System) query.System += system - last.System;
                }
                query.Last = seen;
                one.Add(now, [query.User, query.System]);
            }
        }
    }

    private static void ReadLinux(List<ZabbixLinuxProcess> processes, Query query, Dictionary<int, (ulong User, ulong System, ulong Start)> seen)
    {
        if (query.WantsUser && query.UserId is null) return;                            // a user that does not exist owns none
        foreach (ZabbixLinuxProcess process in processes)
        {
            if (!process.Matches(query.Name, query.UserId, query.CommandLine)) continue;
            if (process.UserTicks is { } user && process.SystemTicks is { } system && process.StartTicks is { } start) seen[process.Pid] = (user, system, start);
        }
    }

    // The .NET process list: a process another account owns, or that has ended, has no times to read.
    private static void ReadManaged(Query query, Dictionary<int, (ulong User, ulong System, ulong Start)> seen)
    {
        foreach (Process process in ZabbixProcessItems.ManagedProcesses(query.Name))
        {
            using (process)
            {
                try
                {
                    seen[process.Id] = ((ulong)process.UserProcessorTime.Ticks, (ulong)process.PrivilegedProcessorTime.Ticks, (ulong)process.StartTime.Ticks);
                }
                catch (Exception refused) when (refused is Win32Exception or InvalidOperationException or NotSupportedException)
                {
                    // Left out of this sample; it counts again from the next one it can be read in.
                }
            }
        }
    }

    /// <summary>What a query keeps between samples: its matcher, its running totals, and the last sample's processes.</summary>
    private sealed class Query(string name, ulong? userId, bool wantsUser, Regex? commandLine)
    {
        public string Name { get; } = name;

        public ulong? UserId { get; } = userId;

        public bool WantsUser { get; } = wantsUser;

        public Regex? CommandLine { get; } = commandLine;

        public ulong User { get; set; }

        public ulong System { get; set; }

        public Dictionary<int, (ulong User, ulong System, ulong Start)> Last { get; set; } = [];
    }
}
