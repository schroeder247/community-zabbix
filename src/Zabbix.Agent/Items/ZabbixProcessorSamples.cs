// This Source Code Form is subject to the terms of the Mozilla Public
// License, v. 2.0. Copyright (c) 2026 schroeder247. SPDX-License-Identifier: MPL-2.0
using System.Globalization;

namespace Community.Zabbix.Items;

/// <summary>
/// The processors' times, one series per processor asked about, and the total: what <c>system.cpu.util</c> reports
/// (<c>update_cpustats</c>, <c>get_cpustat</c>, <c>common/cpustat.c</c>). Linux's come from <c>/proc/stat</c>, a Mac's
/// from <c>host_processor_info</c>, and Windows' from <c>GetSystemTimes</c>, which has only the total.
/// </summary>
internal sealed class ZabbixProcessorSamples(ZabbixItemCollector collector) : ZabbixSamples<int>(collector, States, TimeSpan.FromDays(1), int.MaxValue)
{
    /// <summary>The series of every processor together.</summary>
    public const int All = -1;

    /// <summary>The states <c>/proc/stat</c> counts, in its order.</summary>
    public const int States = 10;

    private const int Guest = 8;
    private const int GuestNice = 9;

    protected override string FullReason => "The processors' figures cannot be read.";

    protected override string FailedReason => "The processors' figures cannot be read.";

    /// <summary>The percentage of its time <paramref name="processor"/> spent in <paramref name="state"/> over the last <paramref name="seconds"/>.</summary>
    public async ValueTask<double> UtilizationAsync(int processor, int state, int seconds, CancellationToken cancellationToken)
    {
        ZabbixSampleSeries series = await AskAsync(processor, cancellationToken).ConfigureAwait(false);
        if (series.Window(seconds) is not { } window) throw new ZabbixItemException(FailedReason);
        ulong total = 0;
        for (int i = 0; i < States; i++)
        {
            if (window.Latest[i] > window.Start[i]) total += window.Latest[i] - window.Start[i];
        }
        // A counter can fall: guest time is not always wholly inside user time in /proc/stat.
        ulong counter = window.Latest[state] > window.Start[state] ? window.Latest[state] - window.Start[state] : 0;
        return total == 0 ? 0 : 100.0 * counter / total;
    }

    /// <inheritdoc/>
    protected override void Read(long now, IReadOnlyList<KeyValuePair<int, ZabbixSampleSeries>> series)
    {
        Dictionary<int, ulong[]> rows = !Collector.Files.IsLinux && OperatingSystem.IsMacOS() ? MacRows()
            : !Collector.Files.IsLinux && OperatingSystem.IsWindows() ? WindowsRows()
            : LinuxRows();
        foreach ((int processor, ZabbixSampleSeries one) in series) one.Add(now, rows.TryGetValue(processor, out ulong[]? counters) ? counters : []);
    }

    [System.Runtime.Versioning.SupportedOSPlatform("macos")]
    private static Dictionary<int, ulong[]> MacRows()
    {
        ZabbixMacCalls.ProcessorTicks[] processors = ZabbixMacCalls.Processors();
        var rows = new Dictionary<int, ulong[]>();
        ulong[] total = new ulong[States];
        for (int processor = 0; processor < processors.Length; processor++)
        {
            ZabbixMacCalls.ProcessorTicks ticks = processors[processor];
            ulong[] row = new ulong[States];
            (row[0], row[1], row[2], row[3]) = (ticks.User, ticks.Nice, ticks.System, ticks.Idle);
            rows[processor] = row;
            for (int state = 0; state < States; state++) total[state] += row[state];
        }
        rows[All] = total;
        return rows;
    }

    // The kernel's ticks include the idle ones. Zabbix's one Windows type, system, is the busy time, kept in its place.
    [System.Runtime.Versioning.SupportedOSPlatform("windows")]
    private static Dictionary<int, ulong[]> WindowsRows()
    {
        (ulong idle, ulong kernel, ulong user) = ZabbixWindowsCalls.SystemTimes();
        ulong[] row = new ulong[States];
        row[2] = kernel + user - idle;
        row[3] = idle;
        return new Dictionary<int, ulong[]> { [All] = row };
    }

    private Dictionary<int, ulong[]> LinuxRows()
    {
        string? text = Collector.Files.TryReadAllText("/proc/stat");
        var rows = new Dictionary<int, ulong[]>();
        foreach (string line in text?.Split('\n') ?? [])
        {
            if (!line.StartsWith("cpu", StringComparison.Ordinal)) continue;
            string[] fields = line.Split(' ', StringSplitOptions.RemoveEmptyEntries);
            int row = All;
            if (fields[0].Length > 3 && !int.TryParse(fields[0].AsSpan(3), NumberStyles.None, CultureInfo.InvariantCulture, out row)) continue;
            ulong[] counters = new ulong[States];
            for (int i = 0; i < States && i + 1 < fields.Length; i++)
            {
                _ = ulong.TryParse(fields[i + 1], NumberStyles.None, CultureInfo.InvariantCulture, out counters[i]);
            }
            // Linux counts guest time in user and nice time too; Zabbix reports them apart.
            unchecked
            {
                counters[0] -= counters[Guest];
                counters[1] -= counters[GuestNice];
            }
            rows[row] = counters;
        }
        return rows;
    }
}
