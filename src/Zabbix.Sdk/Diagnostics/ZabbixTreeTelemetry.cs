// This Source Code Form is subject to the terms of the Mozilla Public
// License, v. 2.0. Copyright (c) 2026 schroeder247. SPDX-License-Identifier: MPL-2.0
using System.Diagnostics;
using System.Diagnostics.Metrics;

namespace Community.Zabbix.Diagnostics;

/// <summary>
/// Provides the pull plane's traces and metrics, published as <see cref="ZabbixTelemetryNames.Tree"/>: a
/// span and a duration sample for every history page and every watcher tick, written through
/// one Start/Finish pair so the two cannot disagree; and counters for what a watcher did with its cadence (polls that
/// succeeded, polls that failed, values its readers' bounds shed).
/// </summary>
internal sealed class ZabbixTreeTelemetry : ZabbixLayerTelemetry
{
    /// <summary>The histogram of pull-plane operation durations, in seconds, tagged <see cref="OperationTag"/> and <c>error.type</c>.</summary>
    public const string OperationDuration = "zabbix.tree.operation.duration";

    /// <summary>The counter of watcher ticks that completed, tagged <see cref="ScopeTag"/>.</summary>
    public const string WatcherPolls = "zabbix.tree.watcher.polls";

    /// <summary>The counter of watcher ticks that failed (the tick is skipped, the cadence kept), tagged <see cref="ScopeTag"/> and <c>error.type</c>.</summary>
    public const string WatcherFailures = "zabbix.tree.watcher.failures";

    /// <summary>The counter of values a watcher's readers lost to their buffers' bounds, tagged <see cref="ScopeTag"/>.</summary>
    public const string WatcherDropped = "zabbix.tree.watcher.dropped";

    /// <summary>The span of one <c>history.get</c> page.</summary>
    public const string HistoryPageSpan = "zabbix.tree.history.page";

    /// <summary>The span of one watcher tick.</summary>
    public const string WatcherTickSpan = "zabbix.tree.watcher.tick";

    /// <summary>The operation tag: <c>history.page</c> or <c>watcher.tick</c>.</summary>
    public const string OperationTag = "zabbix.tree.operation";

    /// <summary>The scope tag: <c>server</c>, <c>group</c>, <c>host</c> or <c>item</c> (the depth, never a name: low cardinality).</summary>
    public const string ScopeTag = "zabbix.tree.scope";

    private static readonly ActivitySource s_activities = new(ZabbixTelemetryNames.Tree, ScopeVersion(typeof(ZabbixTreeTelemetry).Assembly));

    private readonly Counter<long> _polls;
    private readonly Counter<long> _failures;
    private readonly Counter<long> _dropped;

    /// <summary>The pull plane's telemetry.</summary>
    /// <param name="meters">The host's meter factory; <see langword="null"/> to own a meter (disposed with this instance).</param>
    /// <param name="time">The clock durations are measured on; the system clock when <see langword="null"/>.</param>
    public ZabbixTreeTelemetry(IMeterFactory? meters = null, TimeProvider? time = null)
        : base(s_activities, ZabbixTelemetryNames.Tree, OperationDuration, "The duration of a Zabbix pull-plane operation.", meters, time)
    {
        _polls = Meter.CreateCounter<long>(WatcherPolls, unit: "{poll}", description: "Watcher ticks that completed.");
        _failures = Meter.CreateCounter<long>(WatcherFailures, unit: "{poll}", description: "Watcher ticks that failed and were skipped.");
        _dropped = Meter.CreateCounter<long>(WatcherDropped, unit: "{value}", description: "Values a watcher's readers lost to their buffers' bounds.");
    }

    /// <summary>Opens a <c>history.get</c> page.</summary>
    /// <param name="valueType">The value type the page reads.</param>
    /// <param name="limit">The page's limit; <see langword="null"/> when the page reads one second whole.</param>
    /// <returns>The operation.</returns>
    internal ZabbixOperation StartHistoryPage(ZabbixItemValueType valueType, int? limit)
    {
        var tags = new TagList { { OperationTag, "history.page" }, { "rpc.method", "history.get" }, { "zabbix.tree.value_type", (int)valueType } };
        if (limit is { } n) tags.Add("zabbix.tree.history.limit", n);
        return Start(HistoryPageSpan, ActivityKind.Client, tags);
    }

    /// <summary>Opens a watcher tick.</summary>
    /// <param name="scope">The watched node's depth.</param>
    /// <returns>The operation.</returns>
    internal ZabbixOperation StartWatcherTick(string scope)
        => Start(WatcherTickSpan, ActivityKind.Internal, new TagList { { OperationTag, "watcher.tick" }, { ScopeTag, scope } });

    /// <summary>Counts a completed watcher tick.</summary>
    /// <param name="scope">The watched node's depth.</param>
    internal void Polled(string scope) => _polls.Add(1, new KeyValuePair<string, object?>(ScopeTag, scope));

    /// <summary>Counts a failed watcher tick.</summary>
    /// <param name="scope">The watched node's depth.</param>
    /// <param name="failure">What failed.</param>
    internal void PollFailed(string scope, Exception failure)
        => _failures.Add(1, new KeyValuePair<string, object?>(ScopeTag, scope), new KeyValuePair<string, object?>("error.type", ZabbixErrorType.Of(failure)));

    /// <summary>Counts a value a reader's bound shed.</summary>
    /// <param name="scope">The watched node's depth.</param>
    internal void Dropped(string scope) => _dropped.Add(1, new KeyValuePair<string, object?>(ScopeTag, scope));
}
