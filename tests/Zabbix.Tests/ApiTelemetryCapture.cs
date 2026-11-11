// This Source Code Form is subject to the terms of the Mozilla Public
// License, v. 2.0. Copyright (c) 2026 schroeder247. SPDX-License-Identifier: MPL-2.0
using System.Collections.Concurrent;
using System.Diagnostics;
using System.Diagnostics.Metrics;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Diagnostics.Metrics.Testing;

namespace Community.Zabbix.Tests;

/// <summary>
/// The API layer's spans and durations for one test. The activity source is static (the platform's pattern), so tests
/// running in parallel share it: this capture opens a root span of its own and keeps only the spans in that trace. The
/// duration comes from the test container's own meter factory, so it is isolated already.
/// </summary>
internal sealed class ApiTelemetryCapture : IDisposable
{
    private static readonly ActivitySource Tests = new("Community.Zabbix.Tests.Capture");
    private readonly ActivityListener _listener;
    private readonly Activity _root;
    private readonly ConcurrentQueue<Activity> _spans = new();

    public ApiTelemetryCapture(IServiceProvider provider)
    {
        _listener = new ActivityListener
        {
            ShouldListenTo = source => source.Name is ZabbixApiTelemetry.SourceName or "Community.Zabbix.Tests.Capture",
            Sample = (ref ActivityCreationOptions<ActivityContext> _) => ActivitySamplingResult.AllDataAndRecorded,
            ActivityStopped = activity => _spans.Enqueue(activity),
        };
        ActivitySource.AddActivityListener(_listener);
        _root = Tests.StartActivity("test")!;
        Durations = new MetricCollector<double>(provider.GetRequiredService<IMeterFactory>(), ZabbixApiTelemetry.MeterName, ZabbixApiTelemetry.CallDuration);
    }

    public MetricCollector<double> Durations { get; }

    /// <summary>The API spans that ended in this test's trace.</summary>
    public IReadOnlyList<Activity> Spans => _spans
        .Where(span => span.Source.Name == ZabbixApiTelemetry.SourceName && span.TraceId == _root.TraceId)
        .ToList();

    public void Dispose()
    {
        _root.Dispose();
        _listener.Dispose();
        Durations.Dispose();
    }
}
