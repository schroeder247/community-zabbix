// This Source Code Form is subject to the terms of the Mozilla Public
// License, v. 2.0. Copyright (c) 2026 schroeder247. SPDX-License-Identifier: MPL-2.0
using System.Diagnostics;
using System.Diagnostics.Metrics;
using Community.Zabbix.Diagnostics;

namespace Community.Zabbix.PowerShell.Diagnostics;

/// <summary>
/// Records the PowerShell layer's traces and metrics: one span per command run through
/// <see cref="ZabbixCmdlet.TryRun(Func{CancellationToken, Task}, object?)"/>, named after the command, and its duration
/// in seconds with the same tags and <c>error.type</c> on failure. Published under <see cref="SourceName"/>, inside the
/// SDK's <c>Zabbix.*</c> subscription; one instance per connection, its meter from that connection's
/// <see cref="IMeterFactory"/>.
/// </summary>
internal sealed class ZabbixPowerShellTelemetry : ZabbixLayerTelemetry
{
    /// <summary>The activity source and meter name.</summary>
    public const string SourceName = ZabbixTelemetryNames.PowerShell;

    /// <summary>The command duration histogram, in seconds, tagged <c>zabbix.command</c> and <c>error.type</c>.</summary>
    public const string CommandDuration = "zabbix.powershell.command.duration";

    /// <summary>The tag naming the command.</summary>
    public const string CommandTag = "zabbix.command";

    /// <summary>
    /// The session-lifecycle span duration, in seconds, tagged <see cref="SessionKindTag"/>, <see cref="SessionNameTag"/>,
    /// <see cref="SessionPhaseTag"/> and <c>error.type</c>.
    /// </summary>
    public const string SessionDuration = "zabbix.powershell.session.duration";

    /// <summary>The tag naming the session's kind (<c>ActiveAgent</c>, <c>ActiveProxy</c>, <c>Watch</c>...).</summary>
    public const string SessionKindTag = "zabbix.session.kind";

    /// <summary>The tag naming the session.</summary>
    public const string SessionNameTag = "zabbix.session.name";

    /// <summary>The tag naming the lifecycle phase (<c>start</c> or <c>stop</c>).</summary>
    public const string SessionPhaseTag = "zabbix.session.phase";

    /// <summary>
    /// The scriptblock-handler invocation span duration, in seconds, tagged <see cref="HandlerKindTag"/>,
    /// <see cref="HandlerOutcomeTag"/> and <c>error.type</c>.
    /// </summary>
    public const string HandlerDuration = "zabbix.powershell.handler.duration";

    /// <summary>The tag naming the handler's kind (<c>agent-item</c>, <c>discovery-tick</c>...).</summary>
    public const string HandlerKindTag = "zabbix.handler.kind";

    /// <summary>The tag naming a handler invocation's outcome (<c>succeeded</c>, <c>failed</c>, <c>canceled</c>).</summary>
    public const string HandlerOutcomeTag = "zabbix.handler.outcome";

    /// <summary>Handler invocations, tagged <see cref="HandlerKindTag"/> and <see cref="HandlerOutcomeTag"/>.</summary>
    public const string HandlerInvocations = "zabbix.powershell.handler.invocations";

    private static readonly ActivitySource s_traces = new(SourceName);

    /// <summary>
    /// The bucket boundaries the module's own durations in seconds are recorded into: the OpenTelemetry semantic
    /// conventions' set for request durations, and 30 s and 60 s for a slow start or a long handler. Without them an
    /// OpenTelemetry SDK applies its default boundaries, which are made for milliseconds, and every sub-five-second
    /// duration falls into its first bucket.
    /// </summary>
    internal static InstrumentAdvice<double> SecondsAdvice { get; } = new()
    {
        HistogramBucketBoundaries = [0.005, 0.01, 0.025, 0.05, 0.075, 0.1, 0.25, 0.5, 0.75, 1, 2.5, 5, 7.5, 10, 30, 60],
    };

    private readonly Histogram<double> _sessionDuration;
    private readonly Histogram<double> _handlerDuration;
    private readonly Counter<long> _handlerInvocations;

    /// <summary>The layer, on the host's meter factory.</summary>
    /// <param name="meters">The connection's meter factory; <see langword="null"/> for a standalone session to own one.</param>
    /// <param name="time">The clock durations are measured on.</param>
    public ZabbixPowerShellTelemetry(IMeterFactory? meters, TimeProvider? time)
        : base(s_traces, SourceName, CommandDuration, "The duration of a Zabbix PowerShell command's call to the server.", meters, time)
    {
        _sessionDuration = Meter.CreateHistogram(SessionDuration, unit: "s", description: "The duration of one session lifecycle phase (start or stop).", tags: null, advice: SecondsAdvice);
        _handlerDuration = Meter.CreateHistogram(HandlerDuration, unit: "s", description: "The duration of one scriptblock handler invocation.", tags: null, advice: SecondsAdvice);
        _handlerInvocations = Meter.CreateCounter<long>(HandlerInvocations, unit: "{invocation}", description: "Scriptblock handler invocations, by kind and outcome.");
    }

    /// <summary>
    /// Opens a root span for a command that talks to no connected server (<c>Start-ZbxActiveProxy</c>,
    /// <c>Start-ZbxPassiveAgent</c>...), so its session's own lifecycle span nests under it as the "starting
    /// command's span" (<see cref="StartSessionPhase"/> parents to whatever <see cref="Activity.Current"/> is). No
    /// duration is recorded for it (there is no connected-server call to time): <see cref="StartCommand"/> already
    /// does that for the commands that have one.
    /// </summary>
    /// <param name="command">The command name.</param>
    public static Activity? StartCommandSpan(string command) => s_traces.StartActivity(command, ActivityKind.Internal);

    /// <summary>Opens the span of one command against <paramref name="endpoint"/>.</summary>
    /// <param name="command">The command name (<c>Get-ZbxHost</c>).</param>
    /// <param name="endpoint">The server's endpoint.</param>
    public ZabbixOperation StartCommand(string command, Uri endpoint)
    {
        ArgumentNullException.ThrowIfNull(endpoint);
        return Start(command, ActivityKind.Internal, new TagList
        {
            { CommandTag, command },
            { "server.address", endpoint.Host },
            { "server.port", endpoint.Port },
        });
    }

    /// <summary>
    /// Opens a session lifecycle phase's span (<c>{kind}.{phase}</c>), a child of whatever command span is ambient (the
    /// starting or stopping command's own).
    /// </summary>
    /// <param name="kind">The session's kind.</param>
    /// <param name="name">The session's name.</param>
    /// <param name="phase"><c>start</c> or <c>stop</c>.</param>
    public ZabbixOperation StartSessionPhase(string kind, string name, string phase)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(kind);
        ArgumentException.ThrowIfNullOrWhiteSpace(name);
        ArgumentException.ThrowIfNullOrWhiteSpace(phase);
        return Start($"{kind}.{phase}", ActivityKind.Internal, new TagList
        {
            { SessionKindTag, kind },
            { SessionNameTag, name },
            { SessionPhaseTag, phase },
        });
    }

    /// <summary>Closes a session lifecycle phase: records its duration and, on failure, <c>error.type</c>.</summary>
    /// <param name="operation">The operation <see cref="StartSessionPhase"/> returned.</param>
    /// <param name="failure">The exception, when the phase failed.</param>
    public void FinishSessionPhase(in ZabbixOperation operation, Exception? failure = null)
    {
        TagList tags = operation.Tags;
        AddErrorType(ref tags, failure, out bool cancelled, out string? type);
        _sessionDuration.Record(Time.GetElapsedTime(operation.StartTimestamp).TotalSeconds, tags);
        Close(operation.Activity, failure, cancelled, type);
    }

    /// <summary>
    /// Opens a scriptblock handler invocation's root span (never a child: a handler runs off the SDK's own dispatch, not
    /// any command's flow — even when a command span is ambient), linked to the owning session's own lifecycle span
    /// context when it has one.
    /// </summary>
    /// <param name="kind">The handler's kind (<c>agent-item</c>, <c>discovery-tick</c>...).</param>
    /// <param name="sessionContext">The owning session's span context, for the link; <see langword="null"/> when unsampled.</param>
    public ZabbixHandlerOperation StartHandler(string kind, ActivityContext? sessionContext)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(kind);
        ActivityLink[] links = sessionContext is { } context ? [new ActivityLink(context)] : [];
        var tags = new ActivityTagsCollection { { HandlerKindTag, kind } };
        // ActivitySource.StartActivity(name, kind, parentContext, ...) treats a default parentContext as "none given"
        // and falls back to Activity.Current when there is one — so an ambient command span would otherwise become
        // this span's parent. Clearing Activity.Current for the call is the documented way to force a true root.
        Activity? ambient = Activity.Current;
        Activity.Current = null;
        try
        {
            Activity? activity = Source.StartActivity($"{kind}.invoke", ActivityKind.Internal, default(ActivityContext), tags, links);
            return new ZabbixHandlerOperation(activity, Time.GetTimestamp(), kind);
        }
        finally
        {
            Activity.Current = ambient;
        }
    }

    /// <summary>Closes a handler invocation: records its duration, its outcome, an invocation count, and on failure <c>error.type</c>.</summary>
    /// <param name="operation">The operation <see cref="StartHandler"/> returned.</param>
    /// <param name="outcome"><c>succeeded</c>, <c>failed</c> or <c>canceled</c>.</param>
    /// <param name="failure">The exception, when the invocation failed.</param>
    public void FinishHandler(in ZabbixHandlerOperation operation, string outcome, Exception? failure = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(outcome);
        var tags = new TagList { { HandlerKindTag, operation.Kind }, { HandlerOutcomeTag, outcome } };
        AddErrorType(ref tags, failure, out bool cancelled, out string? type);
        _handlerInvocations.Add(1, tags);
        _handlerDuration.Record(Time.GetElapsedTime(operation.StartTimestamp).TotalSeconds, tags);
        operation.Activity?.SetTag(HandlerOutcomeTag, outcome);
        Close(operation.Activity, failure, cancelled, type);
    }

    private static void AddErrorType(ref TagList tags, Exception? failure, out bool cancelled, out string? type)
    {
        cancelled = failure is OperationCanceledException;
        type = cancelled ? null : failure is null ? null : ZabbixErrorType.Of(failure);
        if (type is not null) tags.Add("error.type", type);
    }

    private static void Close(Activity? activity, Exception? failure, bool cancelled, string? type)
    {
        if (activity is null) return;
        if (type is not null)
        {
            activity.SetTag("error.type", type);
            activity.SetStatus(ActivityStatusCode.Error, failure?.Message ?? type);
            if (failure is not null) activity.AddException(failure);
        }
        else if (!cancelled)
        {
            activity.SetStatus(ActivityStatusCode.Ok);
        }
        activity.Dispose();
    }
}
