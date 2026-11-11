// This Source Code Form is subject to the terms of the Mozilla Public
// License, v. 2.0. Copyright (c) 2026 schroeder247. SPDX-License-Identifier: MPL-2.0
using System.Diagnostics.Metrics;
using System.Globalization;
using System.Numerics;
using Microsoft.Extensions.Logging;

namespace Community.Zabbix.Hosting;

/// <summary>
/// The measurements of a <see cref="Meter"/> as values for a push agent: each instrument's name is the item key, and a
/// measurement's tags are the key's parameters, in the order of their names. A counter's measurements are increases and Zabbix
/// stores whole values, so a counter sends its running total; a gauge and an observable instrument send what they last said.
/// A histogram has no one value to send, so it's left out. The listener runs for as long as the source is read.
/// </summary>
internal sealed class ZabbixMeterSource(string meterName, TimeSpan interval, Func<string> host, TimeProvider time, ILogger logger) : IAsyncEnumerable<ZabbixValue>
{
    private readonly Lock _gate = new();
    private readonly Dictionary<(Instrument Instrument, string Tags), Series> _series = [];

    public async IAsyncEnumerator<ZabbixValue> GetAsyncEnumerator(CancellationToken cancellationToken = default)
    {
        using MeterListener listener = new();
        listener.InstrumentPublished = (instrument, published) =>
        {
            if (instrument.Meter.Name != meterName) return;
            if (IsHistogram(instrument)) AgentLog.MeterHistogramLeftOut(logger, instrument.Name, meterName);
            else published.EnableMeasurementEvents(instrument);
        };
        listener.SetMeasurementEventCallback<byte>(Record);
        listener.SetMeasurementEventCallback<short>(Record);
        listener.SetMeasurementEventCallback<int>(Record);
        listener.SetMeasurementEventCallback<long>(Record);
        listener.SetMeasurementEventCallback<float>(Record);
        listener.SetMeasurementEventCallback<double>(Record);
        listener.SetMeasurementEventCallback<decimal>(Record);
        listener.Start();

        IAsyncEnumerable<ZabbixValue> cycles = ZabbixPoll.Every(
            interval,
            _ =>
            {
                listener.RecordObservableInstruments();
                return ValueTask.FromResult<IEnumerable<ZabbixValue>>(Collect());
            },
            time,
            logger);
        await foreach (ZabbixValue value in cycles.WithCancellation(cancellationToken).ConfigureAwait(false)) yield return value;
    }

    private static bool IsHistogram(Instrument instrument) => instrument.GetType().Name.StartsWith("Histogram", StringComparison.Ordinal);

    private void Record<T>(Instrument instrument, T measurement, ReadOnlySpan<KeyValuePair<string, object?>> tags, object? state)
        where T : struct, INumber<T>
    {
        if (!T.IsFinite(measurement)) return;
        decimal value = decimal.CreateSaturating(measurement);
        string joined = Join(tags);
        lock (_gate)
        {
            if (!_series.TryGetValue((instrument, joined), out Series? series))
            {
                series = new Series(KeyOf(instrument, tags));
                _series.Add((instrument, joined), series);
                if (series.Key is null) AgentLog.MeterKeyInvalid(logger, instrument.Name, meterName);
            }

            series.Value = instrument is Counter<T> or UpDownCounter<T> ? series.Value + value : value;
            series.Fresh = true;
        }
    }

    private List<ZabbixValue> Collect()
    {
        DateTimeOffset now = time.GetUtcNow();
        string name = host();
        lock (_gate)
        {
            List<ZabbixValue> values = [];
            foreach (((Instrument instrument, _), Series series) in _series)
            {
                bool running = instrument.GetType().Name.StartsWith("Counter", StringComparison.Ordinal) || instrument.GetType().Name.StartsWith("UpDownCounter", StringComparison.Ordinal);
                if (series.Key is { } key && (series.Fresh || running)) values.Add(new ZabbixValue(name, key, series.Value.ToString(CultureInfo.InvariantCulture), now));
                series.Fresh = false;
            }

            return values;
        }
    }

    // What tells two series of one instrument apart: the tags' values, in the order of their names.
    private static string Join(ReadOnlySpan<KeyValuePair<string, object?>> tags)
    {
        if (tags.IsEmpty) return string.Empty;
        return string.Join('\0', Ordered(tags));
    }

    private static string[] Ordered(ReadOnlySpan<KeyValuePair<string, object?>> tags)
    {
        KeyValuePair<string, object?>[] sorted = tags.ToArray();
        Array.Sort(sorted, static (a, b) => string.CompareOrdinal(a.Key, b.Key));
        return [.. sorted.Select(static tag => Convert.ToString(tag.Value, CultureInfo.InvariantCulture) ?? string.Empty)];
    }

    private static ZabbixItemKey? KeyOf(Instrument instrument, ReadOnlySpan<KeyValuePair<string, object?>> tags)
        => ZabbixItemKey.TryCreate(instrument.Name, Ordered(tags), out ZabbixItemKey key) ? key : null;

    private sealed class Series(ZabbixItemKey? key)
    {
        public ZabbixItemKey? Key { get; } = key;

        public decimal Value { get; set; }

        public bool Fresh { get; set; }
    }
}
