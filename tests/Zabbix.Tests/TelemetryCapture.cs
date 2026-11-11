// This Source Code Form is subject to the terms of the Mozilla Public
// License, v. 2.0. Copyright (c) 2026 schroeder247. SPDX-License-Identifier: MPL-2.0
using System.Collections.Concurrent;
using System.Diagnostics;
using System.Diagnostics.Metrics;
using Microsoft.Extensions.DependencyInjection;

namespace Community.Zabbix.Tests;

/// <summary>
/// What one test's operations traced and measured, isolated from every other test running in parallel: spans are the
/// ones under this capture's own root activity (the listener sees the shared static source) unless the test names another
/// filter (a watcher's ticks are roots of their own), and meters come from this capture's own <see cref="IMeterFactory"/>,
/// which is the scope a <c>MetricCollector</c> filters on.
/// </summary>
internal sealed class TelemetryCapture : IDisposable
{
    private readonly ServiceProvider _services = new ServiceCollection().AddMetrics().BuildServiceProvider();
    private readonly ActivityListener _listener;
    private readonly Activity _root;
    private readonly ConcurrentQueue<Activity> _spans = new();

    public TelemetryCapture(string sourceName, Func<Activity, bool>? accept = null)
    {
        accept ??= span => span.TraceId == _root!.TraceId && span != _root;
        _listener = new ActivityListener
        {
            ShouldListenTo = source => source.Name == sourceName || source.Name == nameof(TelemetryCapture),
            Sample = (ref ActivityCreationOptions<ActivityContext> _) => ActivitySamplingResult.AllDataAndRecorded,
            ActivityStopped = span => { if (span.Source.Name == sourceName && accept(span)) _spans.Enqueue(span); },
        };
        ActivitySource.AddActivityListener(_listener);
        _root = new Activity(nameof(TelemetryCapture));
        _root.Start();
    }

    /// <summary>The meter factory whose meters this capture's collectors see.</summary>
    public IMeterFactory Meters => _services.GetRequiredService<IMeterFactory>();

    /// <summary>The spans stopped so far under this capture's root.</summary>
    public IReadOnlyList<Activity> Spans => [.. _spans];

    public void Dispose()
    {
        _root.Dispose();                                                                    // stops it
        _listener.Dispose();
        _services.Dispose();
    }
}
