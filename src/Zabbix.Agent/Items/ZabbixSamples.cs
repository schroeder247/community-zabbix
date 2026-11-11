// This Source Code Form is subject to the terms of the Mozilla Public
// License, v. 2.0. Copyright (c) 2026 schroeder247. SPDX-License-Identifier: MPL-2.0
namespace Community.Zabbix.Items;

/// <summary>
/// One family of sampled series, by what each one measures: a series starts when a key first asks for it, is read every
/// second by the <see cref="ZabbixItemCollector"/>, and is dropped when no key has asked for it for a while.
/// </summary>
/// <typeparam name="TKey">What tells the family's series apart.</typeparam>
internal abstract class ZabbixSamples<TKey>(ZabbixItemCollector collector, int width, TimeSpan idle, int limit) : IZabbixSamples
    where TKey : notnull
{
    private readonly Lock _gate = new();
    private readonly Dictionary<TKey, ZabbixSampleSeries> _series = [];

    /// <summary>What a key answers when the family holds as many series as it may.</summary>
    protected abstract string FullReason { get; }

    /// <summary>What a key answers when what it asks about cannot be sampled.</summary>
    protected abstract string FailedReason { get; }

    protected ZabbixItemCollector Collector => collector;

    /// <inheritdoc/>
    public bool IsEmpty
    {
        get
        {
            lock (_gate) return _series.Count == 0;
        }
    }

    /// <inheritdoc/>
    public void Sample(long now)
    {
        List<KeyValuePair<TKey, ZabbixSampleSeries>> live;
        lock (_gate)
        {
            foreach ((TKey key, ZabbixSampleSeries series) in _series)
            {
                if (collector.Time.GetElapsedTime(series.LastAsked, now) > idle) _series.Remove(key);
            }
            live = [.. _series];
        }
        if (live.Count > 0) Read(now, live);
    }

    /// <summary>
    /// The series for <paramref name="key"/>, once it holds two samples: a new series takes its first at once, and the
    /// wait for its second spends the check's deadline, as Zabbix's agent waits for its collector's data.
    /// </summary>
    protected async ValueTask<ZabbixSampleSeries> AskAsync(TKey key, CancellationToken cancellationToken)
    {
        ZabbixSampleSeries series = Series(key);
        collector.EnsureSampling();
        await series.TwoSamples.WaitAsync(cancellationToken).ConfigureAwait(false);
        return series;
    }

    /// <summary>What a family keeps beside a new series' samples.</summary>
    protected virtual object? CreateState(TKey key) => null;

    /// <summary>Takes one sample of each of <paramref name="series"/>; a failed one is <see cref="ZabbixSampleSeries.Add"/> with no counters.</summary>
    protected abstract void Read(long now, IReadOnlyList<KeyValuePair<TKey, ZabbixSampleSeries>> series);

    private ZabbixSampleSeries Series(TKey key)
    {
        long now = collector.Time.GetTimestamp();
        // The first sample is taken under the lock, so a tick that follows sees a series that has one.
        lock (_gate)
        {
            if (!_series.TryGetValue(key, out ZabbixSampleSeries? series))
            {
                if (_series.Count >= limit) throw new ZabbixItemException(FullReason);
                series = new ZabbixSampleSeries(width, collector.Time, CreateState(key));
                Read(now, [new(key, series)]);
                if (series.LatestFailed) throw new ZabbixItemException(FailedReason);       // never kept: a key cannot grow the table with names that do not exist
                _series.Add(key, series);
            }
            series.LastAsked = now;
            return !series.LatestFailed ? series : throw new ZabbixItemException(FailedReason);
        }
    }
}
