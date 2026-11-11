// This Source Code Form is subject to the terms of the Mozilla Public
// License, v. 2.0. Copyright (c) 2026 schroeder247. SPDX-License-Identifier: MPL-2.0
using System.Collections.Concurrent;
using System.Diagnostics;
using System.Diagnostics.Metrics;
using Community.Zabbix.Diagnostics;
using Community.Zabbix.PowerShell.Diagnostics;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Diagnostics.Metrics.Testing;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Testing;
using Microsoft.Extensions.Time.Testing;

namespace Community.Zabbix.PowerShell.Tests;

/// <summary>
/// One span/duration per session lifecycle phase (start/stop) and per scriptblock handler invocation,
/// the module's own 14xxx log events, and the unified session-logging provider (one idiom behind a session's ring buffer
/// and a connection's unrouted queue).
/// </summary>
public sealed class SessionTelemetryTests
{
    private static IMeterFactory Meters() => new ServiceCollection().AddMetrics().BuildServiceProvider().GetRequiredService<IMeterFactory>();

    [Fact]
    public void Every_duration_histogram_advises_second_scale_buckets()
    {
        // An OpenTelemetry SDK's default boundaries are made for milliseconds: a duration in seconds needs its own.
        var advised = new ConcurrentDictionary<string, IReadOnlyList<double>?>();
        using var listener = new MeterListener
        {
            InstrumentPublished = (instrument, _) =>
            {
                if (instrument.Meter.Name == ZabbixPowerShellTelemetry.SourceName && instrument is Histogram<double> histogram)
                    advised[instrument.Name] = histogram.Advice?.HistogramBucketBoundaries;
            },
        };
        listener.Start();
        using var telemetry = new ZabbixPowerShellTelemetry(Meters(), new FakeTimeProvider());

        foreach (string name in new[] { ZabbixPowerShellTelemetry.SessionDuration, ZabbixPowerShellTelemetry.HandlerDuration })
            Assert.Contains(1.0, Assert.IsAssignableFrom<IReadOnlyList<double>>(advised[name]));
    }

    [Fact]
    public void StartSessionPhase_opens_a_span_named_after_the_kind_and_phase_tagged_with_the_session()
    {
        // A ConcurrentQueue, not a List<Activity> under a lock: ActivitySource.AddActivityListener is process-global,
        // so under xunit's parallel test execution another test's ActivityStopped callback can enqueue concurrently
        // with THIS test's own Assert.Single enumerating the collection (a List flakily throws "Collection was modified"),
        // the same reason Zabbix.Tests's ApiTelemetryCapture uses a queue rather than a locked list. Each test still
        // isolates its own spans by a unique session/tag name in the Assert.Single predicate below.
        var spans = new ConcurrentQueue<Activity>();
        using var listener = new ActivityListener
        {
            ShouldListenTo = source => source.Name == ZabbixPowerShellTelemetry.SourceName,
            Sample = (ref ActivityCreationOptions<ActivityContext> _) => ActivitySamplingResult.AllDataAndRecorded,
            ActivityStopped = span => spans.Enqueue(span),
        };
        ActivitySource.AddActivityListener(listener);
        using var telemetry = new ZabbixPowerShellTelemetry(Meters(), new FakeTimeProvider());

        ZabbixOperation started = telemetry.StartSessionPhase("ActiveProxy", "probe", "start");
        telemetry.FinishSessionPhase(started);

        Activity span = Assert.Single(spans, s => s.OperationName == "ActiveProxy.start");
        Assert.Equal("probe", span.GetTagItem(ZabbixPowerShellTelemetry.SessionNameTag));
        Assert.Equal("ActiveProxy", span.GetTagItem(ZabbixPowerShellTelemetry.SessionKindTag));
        Assert.Equal("start", span.GetTagItem(ZabbixPowerShellTelemetry.SessionPhaseTag));
        Assert.Equal(ActivityStatusCode.Ok, span.Status);
    }

    [Fact]
    public void StartSessionPhase_nests_under_whatever_command_span_is_ambient()
    {
        using var listener = new ActivityListener
        {
            ShouldListenTo = source => source.Name == ZabbixPowerShellTelemetry.SourceName,
            Sample = (ref ActivityCreationOptions<ActivityContext> _) => ActivitySamplingResult.AllDataAndRecorded,
        };
        ActivitySource.AddActivityListener(listener);
        using var telemetry = new ZabbixPowerShellTelemetry(Meters(), new FakeTimeProvider());

        using Activity? command = ZabbixPowerShellTelemetry.StartCommandSpan("Start-ZbxActiveProxy");
        ZabbixOperation started = telemetry.StartSessionPhase("ActiveProxy", "probe", "start");

        Assert.NotNull(started.Activity);
        Assert.Equal(command!.SpanId, started.Activity!.ParentSpanId);
        telemetry.FinishSessionPhase(started);
    }

    [Fact]
    public void FinishSessionPhase_of_a_failed_phase_sets_error_type_and_the_span_status()
    {
        // A ConcurrentQueue, not a List<Activity> under a lock: ActivitySource.AddActivityListener is process-global,
        // so under xunit's parallel test execution another test's ActivityStopped callback can enqueue concurrently
        // with THIS test's own Assert.Single enumerating the collection (a List flakily throws "Collection was modified"),
        // the same reason Zabbix.Tests's ApiTelemetryCapture uses a queue rather than a locked list. Each test still
        // isolates its own spans by a unique session/tag name in the Assert.Single predicate below.
        var spans = new ConcurrentQueue<Activity>();
        using var listener = new ActivityListener
        {
            ShouldListenTo = source => source.Name == ZabbixPowerShellTelemetry.SourceName,
            Sample = (ref ActivityCreationOptions<ActivityContext> _) => ActivitySamplingResult.AllDataAndRecorded,
            ActivityStopped = span => spans.Enqueue(span),
        };
        ActivitySource.AddActivityListener(listener);
        using var telemetry = new ZabbixPowerShellTelemetry(Meters(), new FakeTimeProvider());

        ZabbixOperation started = telemetry.StartSessionPhase("ActiveAgent", "probe-fail", "start");
        telemetry.FinishSessionPhase(started, new InvalidOperationException("boom"));

        Activity span = Assert.Single(spans, s => s.GetTagItem(ZabbixPowerShellTelemetry.SessionNameTag) as string == "probe-fail");
        Assert.Equal(ActivityStatusCode.Error, span.Status);
        Assert.NotNull(span.GetTagItem("error.type"));
    }

    [Fact]
    public void SessionDuration_records_a_sample_tagged_with_the_session_and_error_type_on_failure()
    {
        IMeterFactory meters = Meters();
        using var telemetry = new ZabbixPowerShellTelemetry(meters, new FakeTimeProvider());
        using var durations = new MetricCollector<double>(meters, ZabbixPowerShellTelemetry.SourceName, ZabbixPowerShellTelemetry.SessionDuration);

        telemetry.FinishSessionPhase(telemetry.StartSessionPhase("Watch", "w1", "start"));
        telemetry.FinishSessionPhase(telemetry.StartSessionPhase("Watch", "w1", "stop"), new TimeoutException());

        IReadOnlyList<CollectedMeasurement<double>> samples = durations.GetMeasurementSnapshot();
        Assert.Equal(2, samples.Count);
        Assert.False(samples[0].Tags.ContainsKey("error.type"));
        Assert.Equal("timeout", samples[1].Tags["error.type"]);
    }

    [Fact]
    public void StartHandler_opens_a_root_span_even_under_an_ambient_activity_linked_to_the_session()
    {
        // A ConcurrentQueue, not a List<Activity> under a lock: ActivitySource.AddActivityListener is process-global,
        // so under xunit's parallel test execution another test's ActivityStopped callback can enqueue concurrently
        // with THIS test's own Assert.Single enumerating the collection (a List flakily throws "Collection was modified"),
        // the same reason Zabbix.Tests's ApiTelemetryCapture uses a queue rather than a locked list. Each test still
        // isolates its own spans by a unique session/tag name in the Assert.Single predicate below.
        var spans = new ConcurrentQueue<Activity>();
        using var listener = new ActivityListener
        {
            ShouldListenTo = source => source.Name == ZabbixPowerShellTelemetry.SourceName,
            Sample = (ref ActivityCreationOptions<ActivityContext> _) => ActivitySamplingResult.AllDataAndRecorded,
            ActivityStopped = span => spans.Enqueue(span),
        };
        ActivitySource.AddActivityListener(listener);
        using var telemetry = new ZabbixPowerShellTelemetry(Meters(), new FakeTimeProvider());
        ZabbixOperation session = telemetry.StartSessionPhase("ActiveAgent", "probe-handler", "start");
        ActivityContext? sessionContext = session.Activity?.Context;
        telemetry.FinishSessionPhase(session);

        using Activity? ambient = ZabbixPowerShellTelemetry.StartCommandSpan("Test-ZbxAgentItem");
        ZabbixHandlerOperation handler = telemetry.StartHandler("agent-item", sessionContext);
        telemetry.FinishHandler(handler, "succeeded");

        Activity span = Assert.Single(spans, s => s.OperationName == "agent-item.invoke");
        Assert.Null(span.ParentId);                                    // a root, not a child of the ambient command
        Assert.Single(span.Links, link => link.Context.SpanId == sessionContext!.Value.SpanId);
        Assert.Equal("succeeded", span.GetTagItem(ZabbixPowerShellTelemetry.HandlerOutcomeTag));
    }

    [Fact]
    public void HandlerDuration_and_invocation_count_are_recorded_by_kind_and_outcome()
    {
        IMeterFactory meters = Meters();
        using var telemetry = new ZabbixPowerShellTelemetry(meters, new FakeTimeProvider());
        using var durations = new MetricCollector<double>(meters, ZabbixPowerShellTelemetry.SourceName, ZabbixPowerShellTelemetry.HandlerDuration);
        using var invocations = new MetricCollector<long>(meters, ZabbixPowerShellTelemetry.SourceName, ZabbixPowerShellTelemetry.HandlerInvocations);

        telemetry.FinishHandler(telemetry.StartHandler("discovery-tick", null), "succeeded");
        telemetry.FinishHandler(telemetry.StartHandler("discovery-tick", null), "failed", new InvalidOperationException("boom"));

        Assert.Equal(2, durations.GetMeasurementSnapshot().Count);
        IReadOnlyList<CollectedMeasurement<long>> counted = invocations.GetMeasurementSnapshot();
        Assert.Equal(2, counted.Count);
        Assert.Contains(counted, m => Equals(m.Tags[ZabbixPowerShellTelemetry.HandlerOutcomeTag], "succeeded"));
        Assert.Contains(counted, m => Equals(m.Tags[ZabbixPowerShellTelemetry.HandlerOutcomeTag], "failed") && m.Tags.ContainsKey("error.type"));
        Assert.Equal("{invocation}", invocations.Instrument?.Unit);   // the semantic conventions' annotation for a count
    }

    [Fact]
    public void SessionTickFailed_is_logged_at_Warning_with_its_own_event_id()
    {
        var logs = new FakeLogCollector();
        ILogger logger = new FakeLogger(logs);

        ZabbixPowerShellLog.SessionTickFailed(logger, "ActiveProxy", "probe", new InvalidOperationException("pull failed"));

        FakeLogRecord record = Assert.Single(logs.GetSnapshot());
        Assert.Equal(14200, record.Id.Id);
        Assert.Equal(LogLevel.Warning, record.Level);
    }

    [Fact]
    public void SessionStartFailed_is_logged_at_Error_with_its_own_event_id()
    {
        var logs = new FakeLogCollector();
        ILogger logger = new FakeLogger(logs);

        ZabbixPowerShellLog.SessionStartFailed(logger, "PassiveAgent", "probe", new InvalidOperationException("listen failed"));

        FakeLogRecord record = Assert.Single(logs.GetSnapshot());
        Assert.Equal(14201, record.Id.Id);
        Assert.Equal(LogLevel.Error, record.Level);
    }

    [Fact]
    public void SessionStopFailed_is_logged_at_Warning_with_its_own_event_id()
    {
        var logs = new FakeLogCollector();
        ILogger logger = new FakeLogger(logs);

        ZabbixPowerShellLog.SessionStopFailed(logger, "Watch", "probe", new InvalidOperationException("stop failed"));

        FakeLogRecord record = Assert.Single(logs.GetSnapshot());
        Assert.Equal(14202, record.Id.Id);
        Assert.Equal(LogLevel.Warning, record.Level);
    }

    [Fact]
    public void HandlerInvocationFailed_is_logged_at_Warning_with_its_own_event_id()
    {
        var logs = new FakeLogCollector();
        ILogger logger = new FakeLogger(logs);

        ZabbixPowerShellLog.HandlerInvocationFailed(logger, "agent-item", "boom", new InvalidOperationException("boom"));

        FakeLogRecord record = Assert.Single(logs.GetSnapshot());
        Assert.Equal(14300, record.Id.Id);
        Assert.Equal(LogLevel.Warning, record.Level);
        Assert.EndsWith(": boom", record.Message, StringComparison.Ordinal);   // the reason is in the line
    }
}
