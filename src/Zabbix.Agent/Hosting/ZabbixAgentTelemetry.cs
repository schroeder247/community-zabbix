// This Source Code Form is subject to the terms of the Mozilla Public
// License, v. 2.0. Copyright (c) 2026 schroeder247. SPDX-License-Identifier: MPL-2.0
using System.Diagnostics;
using System.Diagnostics.Metrics;
using Community.Zabbix.Diagnostics;

namespace Community.Zabbix.Hosting;

/// <summary>
/// Provides the agent layer's traces and metrics (activity source and meter <see cref="ZabbixTelemetryNames.Agent"/>).
/// A span per exchange with the server (<c>active checks</c>, <c>agent data</c>), its duration in
/// seconds on <see cref="ExchangeDurationInstrument"/> with <c>error.type</c> on failure; and
/// <see cref="ValuesInstrument"/>, the values a send loop took off its store counted by what became of them.
/// </summary>
internal sealed class ZabbixAgentTelemetry : ZabbixLayerTelemetry
{
    /// <summary>The activity source's name.</summary>
    public const string SourceName = ZabbixTelemetryNames.Agent;

    /// <summary>The meter's name.</summary>
    public const string MeterName = ZabbixTelemetryNames.Agent;

    /// <summary>The histogram of exchange durations with the server, in seconds.</summary>
    public const string ExchangeDurationInstrument = "zabbix.agent.exchange.duration";

    /// <summary>The counter of values by outcome (<see cref="OutcomeTag"/>).</summary>
    public const string ValuesInstrument = "zabbix.agent.values";

    /// <summary>The tag naming the agent.</summary>
    public const string AgentTag = "zabbix.agent.name";

    /// <summary>The tag naming the request (<c>active checks</c>, <c>agent data</c>).</summary>
    public const string RequestTag = "zabbix.agent.request";

    /// <summary>
    /// The tag on <see cref="ValuesInstrument"/>: <c>delivered</c>, <c>rejected</c>, <c>retried</c> (an attempt that
    /// failed; the loop keeps the batch), <c>held</c> (an attempt the destination paused), <c>returned</c> (handed back to
    /// the source at stop), <c>abandoned</c> (lost at stop).
    /// </summary>
    public const string OutcomeTag = "zabbix.agent.delivery.outcome";

    private static readonly ActivitySource s_activities = new(SourceName);

    private readonly Counter<long> _values;

    /// <summary>The layer's telemetry.</summary>
    /// <param name="meters">The host's meter factory; <see langword="null"/> to own the meter.</param>
    /// <param name="time">The clock durations are measured on.</param>
    public ZabbixAgentTelemetry(IMeterFactory? meters = null, TimeProvider? time = null)
        : base(s_activities, MeterName, ExchangeDurationInstrument, "The duration of an agent's exchange with its server.", meters, time)
        => _values = Meter.CreateCounter<long>(ValuesInstrument, unit: "{value}", description: "Values a send loop took off its store, by what became of them.");

    /// <summary>Opens the span of one exchange.</summary>
    /// <param name="agent">The agent's name.</param>
    /// <param name="request">The Zabbix request (<c>active checks</c>, <c>agent data</c>).</param>
    /// <returns>The operation, for <see cref="ZabbixLayerTelemetry.Finish"/>.</returns>
    public ZabbixOperation StartExchange(string agent, string request)
        => Start(request, ActivityKind.Client, new TagList { { AgentTag, agent }, { RequestTag, request } });

    /// <summary>Counts <paramref name="count"/> values of <paramref name="agent"/> under <paramref name="outcome"/>.</summary>
    /// <param name="agent">The agent's name.</param>
    /// <param name="outcome">What became of them.</param>
    /// <param name="count">How many.</param>
    public void Values(string agent, ZabbixSendOutcome outcome, int count)
        => _values.Add(count, new TagList { { AgentTag, agent }, { OutcomeTag, OutcomeName(outcome) } });

    private static string OutcomeName(ZabbixSendOutcome outcome) => outcome switch
    {
        ZabbixSendOutcome.Delivered => "delivered",
        ZabbixSendOutcome.Rejected => "rejected",
        ZabbixSendOutcome.Retried => "retried",
        ZabbixSendOutcome.Held => "held",
        ZabbixSendOutcome.Returned => "returned",
        ZabbixSendOutcome.Abandoned => "abandoned",
        _ => throw new ArgumentOutOfRangeException(nameof(outcome), outcome, "The delivery outcome is not one the agent knows."),
    };
}
