// This Source Code Form is subject to the terms of the Mozilla Public
// License, v. 2.0. Copyright (c) 2026 schroeder247. SPDX-License-Identifier: MPL-2.0
using System.Globalization;

namespace Community.Zabbix.Items;

/// <summary>
/// A block device's counters from <c>/proc/diskstats</c>, one series per device asked about: what <c>vfs.dev.read</c> and
/// <c>vfs.dev.write</c> report per second (<c>common/diskdevices.c</c>). A device goes unrequested after three hours, and
/// at most 1024 are followed, as in Zabbix's agent.
/// </summary>
internal sealed class ZabbixDeviceSamples(ZabbixItemCollector collector) : ZabbixSamples<string>(collector, 4, TimeSpan.FromHours(3), 1024)
{
    /// <summary>The series of every device together, the name <c>/proc/diskstats</c> has no line for.</summary>
    public const string All = "";

    protected override string FullReason => "The collector cannot follow that block device.";

    protected override string FailedReason => "The block device's counters cannot be read.";

    /// <summary>The sectors or operations <paramref name="device"/> read or wrote per second over the last <paramref name="seconds"/>.</summary>
    public async ValueTask<double> RateAsync(string device, bool read, bool sectors, int seconds, CancellationToken cancellationToken)
    {
        ZabbixSampleSeries series = await AskAsync(device, cancellationToken).ConfigureAwait(false);
        if (series.Window(seconds) is not { } window) throw new ZabbixItemException(FailedReason);
        int counter = (read ? 0 : 2) + (sectors ? 1 : 0);
        double elapsed = window.Elapsed.TotalSeconds;
        return elapsed <= 0 || window.Latest[counter] < window.Start[counter] ? 0 : (window.Latest[counter] - window.Start[counter]) / elapsed;
    }

    /// <inheritdoc/>
    protected override void Read(long now, IReadOnlyList<KeyValuePair<string, ZabbixSampleSeries>> series)
    {
        var rows = new Dictionary<string, ulong[]>(StringComparer.Ordinal);
        ulong[]? all = null;
        foreach (string line in Collector.Files.TryReadAllText("/proc/diskstats")?.Split('\n') ?? [])
        {
            string[] fields = line.Split(' ', StringSplitOptions.RemoveEmptyEntries);
            if (fields.Length < 10) continue;
            // reads, read sectors, writes, written sectors: fields 3, 5, 7 and 9.
            ulong[] counters = [Number(fields[3]), Number(fields[5]), Number(fields[7]), Number(fields[9])];
            rows[fields[2]] = counters;
            all ??= new ulong[4];
            for (int i = 0; i < 4; i++) all[i] += counters[i];
        }
        if (all is not null) rows[All] = all;
        foreach ((string device, ZabbixSampleSeries one) in series)
        {
            // A device that has gone keeps its last figures, as process_diskstat leaves them.
            if (rows.TryGetValue(device, out ulong[]? counters)) one.Add(now, counters);
            else if (one.LatestFailed) one.Add(now, []);
        }

        static ulong Number(string field) => ulong.TryParse(field, NumberStyles.None, CultureInfo.InvariantCulture, out ulong value) ? value : 0;
    }
}
