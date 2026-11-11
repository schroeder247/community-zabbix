// This Source Code Form is subject to the terms of the Mozilla Public
// License, v. 2.0. Copyright (c) 2026 schroeder247. SPDX-License-Identifier: MPL-2.0
using System.Diagnostics;
using System.Diagnostics.Metrics;

namespace Community.Zabbix.Diagnostics;

/// <summary>
/// Writes a layer's traces and metrics through one Start/Finish pair. A subclass names its
/// activity source and its duration instrument; this base opens the span with the shared tags, and on finish records
/// the duration sample in seconds with the same tags, sets <c>error.type</c> on both, sets the span status, and attaches
/// the exception. The meter comes from the host's <see cref="IMeterFactory"/>, or is owned by this instance when there
/// is no container (then disposing the instance disposes it).
/// </summary>
internal abstract class ZabbixLayerTelemetry : IDisposable
{
    private readonly Meter? _ownedMeter;
    private readonly Histogram<double> _duration;

    /// <summary>Initialises the layer.</summary>
    /// <param name="source">The layer's activity source (static, since <see cref="ActivitySource"/> has no factory).</param>
    /// <param name="meterName">The layer's meter name, under <see cref="ZabbixTelemetryNames.Prefix"/>.</param>
    /// <param name="durationInstrument">The duration histogram's name.</param>
    /// <param name="durationDescription">What the duration measures.</param>
    /// <param name="meters">The host's meter factory; <see langword="null"/> to own a meter.</param>
    /// <param name="time">The clock durations are measured on; the system clock when <see langword="null"/>.</param>
    protected ZabbixLayerTelemetry(ActivitySource source, string meterName, string durationInstrument, string durationDescription, IMeterFactory? meters, TimeProvider? time)
    {
        ArgumentNullException.ThrowIfNull(source);
        ArgumentException.ThrowIfNullOrWhiteSpace(meterName);
        Source = source;
        Time = time ?? TimeProvider.System;
        var scope = new MeterOptions(meterName) { Version = ScopeVersion(GetType().Assembly) };
        Meter = meters?.Create(scope) ?? (_ownedMeter = new Meter(scope));
        _duration = Meter.CreateHistogram(durationInstrument, unit: "s", description: durationDescription, tags: null, advice: DurationAdvice);
    }

    /// <summary>
    /// The bucket boundaries every duration in seconds is recorded into: the OpenTelemetry semantic conventions' set for
    /// request durations, and 30 s and 60 s for the wire's deadlines and a long <c>configuration.import</c>. Without them an
    /// OpenTelemetry SDK applies its default boundaries, which are made for milliseconds, and every sub-five-second
    /// duration falls into its first bucket.
    /// </summary>
    internal static InstrumentAdvice<double> DurationAdvice { get; } = new()
    {
        HistogramBucketBoundaries = [0.005, 0.01, 0.025, 0.05, 0.075, 0.1, 0.25, 0.5, 0.75, 1, 2.5, 5, 7.5, 10, 30, 60],
    };

    /// <summary>
    /// The instrumentation scope's version an activity source and a meter carry: the library's informational version
    /// without its build metadata (the commit after <c>+</c>), so a backend tells which release emitted a span or a
    /// metric.
    /// </summary>
    /// <param name="library">The library that emits.</param>
    /// <returns>The version, or <see langword="null"/> when the assembly carries none.</returns>
    internal static string? ScopeVersion(System.Reflection.Assembly library)
    {
        ArgumentNullException.ThrowIfNull(library);
        string? version = System.Reflection.CustomAttributeExtensions.GetCustomAttribute<System.Reflection.AssemblyInformationalVersionAttribute>(library)?.InformationalVersion;
        return version?.Split('+', 2)[0];
    }

    /// <summary>The layer's activity source.</summary>
    protected ActivitySource Source { get; }

    /// <summary>The layer's meter, for its counters.</summary>
    protected Meter Meter { get; }

    /// <summary>The clock durations are measured on.</summary>
    protected TimeProvider Time { get; }

    /// <summary>Opens an operation: a span named <paramref name="name"/> carrying <paramref name="tags"/>, and a start time.</summary>
    /// <param name="name">The span name.</param>
    /// <param name="kind">The span kind.</param>
    /// <param name="tags">The tags shared by the span and the duration sample.</param>
    /// <returns>The operation, to pass to <see cref="Finish"/>.</returns>
    public ZabbixOperation Start(string name, ActivityKind kind, TagList tags)
    {
        // The tags go in with the start, not after it, so a sampler deciding on the span sees them (an unset parent
        // context is the ambient Activity.Current, as the two-argument overload uses).
        Activity? activity = Source.StartActivity(name, kind, parentContext: default, tags: tags);
        return new ZabbixOperation(activity, Time.GetTimestamp(), tags);
    }

    /// <summary>
    /// Closes an operation: records the duration sample (seconds) with the operation's tags plus <paramref name="extra"/>,
    /// and sets the same tags on the span. A <paramref name="failure"/> adds <c>error.type</c> (from
    /// <see cref="ZabbixErrorType.Of"/> unless <paramref name="errorType"/> is given) to both, sets the span status to
    /// Error with the error type as its description and records the exception, unless it is a peer's refusal, whose
    /// message carries the peer's words. Cancellation by the caller is not an error: the span ends Unset.
    /// </summary>
    /// <param name="operation">The operation <see cref="Start"/> returned.</param>
    /// <param name="failure">The exception, when the operation failed.</param>
    /// <param name="errorType">An explicit <c>error.type</c> for a failure that is not an exception (a refusal reply).</param>
    /// <param name="extra">Tags known only at the end (a result code, a byte count).</param>
    public void Finish(in ZabbixOperation operation, Exception? failure = null, string? errorType = null, TagList extra = default)
    {
        TagList tags = operation.Tags;
        foreach (KeyValuePair<string, object?> tag in extra) tags.Add(tag);
        bool cancelled = failure is OperationCanceledException && errorType is null;
        string? type = cancelled ? null : errorType ?? (failure is null ? null : ZabbixErrorType.Of(failure));
        if (type is not null) tags.Add("error.type", type);
        _duration.Record(Time.GetElapsedTime(operation.StartTimestamp).TotalSeconds, tags);

        Activity? activity = operation.Activity;
        if (activity is null) return;
        foreach (KeyValuePair<string, object?> tag in extra) activity.SetTag(tag.Key, tag.Value);
        if (type is not null)
        {
            activity.SetTag("error.type", type);
            // The status says what kind of failure it was, never the message: a message can carry a peer's words, which
            // echo the request's values (an item key, a macro), and a span is exported unredacted. A peer's refusal is
            // not recorded as an exception for the same reason; its code and kind are the error type.
            activity.SetStatus(ActivityStatusCode.Error, type);
            if (failure is not null and not ZabbixRefusedException) activity.AddException(failure);
        }
        else if (!cancelled)
        {
            activity.SetStatus(ActivityStatusCode.Ok);
        }
        activity.Dispose();
    }

    /// <summary>Adds an event to the operation's span (a failover, a retry, a redirect).</summary>
    /// <param name="operation">The operation.</param>
    /// <param name="name">The event name.</param>
    /// <param name="tags">The event's tags.</param>
    public static void Event(in ZabbixOperation operation, string name, TagList tags = default)
    {
        if (operation.Activity is null) return;
        ActivityTagsCollection collection = [];
        foreach (KeyValuePair<string, object?> tag in tags) collection.Add(tag.Key, tag.Value);
        operation.Activity.AddEvent(new ActivityEvent(name, tags: collection));
    }

    /// <inheritdoc/>
    public void Dispose()
    {
        Dispose(disposing: true);
        GC.SuppressFinalize(this);
    }

    /// <summary>Disposes the meter this instance owns (none when it came from the host's factory).</summary>
    /// <param name="disposing">Whether this is <see cref="Dispose()"/>.</param>
    protected virtual void Dispose(bool disposing)
    {
        if (disposing) _ownedMeter?.Dispose();
    }
}
