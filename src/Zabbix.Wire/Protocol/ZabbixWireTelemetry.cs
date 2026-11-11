// This Source Code Form is subject to the terms of the Mozilla Public
// License, v. 2.0. Copyright (c) 2026 schroeder247. SPDX-License-Identifier: MPL-2.0
using System.Diagnostics;
using System.Diagnostics.Metrics;
using Community.Zabbix.Diagnostics;

namespace Community.Zabbix.Protocol;

/// <summary>
/// Provides the framed-TCP wire's traces and metrics, under <see cref="ZabbixTelemetryNames.Wire"/>: a
/// client span per exchange or notification carrying <c>server.address</c>, <c>server.port</c>,
/// <c>network.transport</c> = <c>tcp</c>, <c>zabbix.tls.mode</c> and the request it carries (<c>rpc.method</c>); the duration histogram
/// <see cref="DurationInstrument"/> in seconds with the same tags and <c>error.type</c> on failure; a client span per
/// server-wire request named for the request (<c>rpc.method</c>), its duration on the same histogram; and counters for
/// the events a dashboard counts: failovers and redirects.
/// </summary>
internal sealed class ZabbixWireTelemetry : ZabbixLayerTelemetry
{
    /// <summary>The span of a request/reply exchange.</summary>
    public const string ExchangeSpan = "zabbix.wire.exchange";

    /// <summary>The span of a notification (written, never answered).</summary>
    public const string NotifySpan = "zabbix.wire.notify";

    /// <summary>The duration histogram, seconds, per exchange or notification.</summary>
    public const string DurationInstrument = "zabbix.wire.exchange.duration";

    /// <summary>The counter of connect-time failovers from one node to the next.</summary>
    public const string FailoverInstrument = "zabbix.wire.failovers";

    /// <summary>The counter of redirect replies (a proxy group moving a host).</summary>
    public const string RedirectInstrument = "zabbix.wire.redirects";

    /// <summary>The span event for a failover.</summary>
    public const string FailoverEvent = "zabbix.wire.failover";

    /// <summary>The span event for a refused frame.</summary>
    public const string FrameRefusedEvent = "zabbix.wire.frame_refused";

    /// <summary>The span event for a followed redirect (a new target, or a reset).</summary>
    public const string RedirectEvent = "zabbix.wire.redirect";

    /// <summary>The span event for a passed deadline.</summary>
    public const string DeadlineEvent = "zabbix.wire.deadline";

    private static readonly ActivitySource s_activities = new(ZabbixTelemetryNames.Wire, ScopeVersion(typeof(ZabbixWireTelemetry).Assembly));
    private static readonly Lazy<ZabbixWireTelemetry> s_fallback = new(() => new ZabbixWireTelemetry(null, null));
    private readonly Counter<long> _failovers;
    private readonly Counter<long> _redirects;

    /// <summary>The wire's telemetry on the host's meter factory.</summary>
    /// <param name="meters">The host's meter factory; <see langword="null"/> to own the meter.</param>
    /// <param name="time">The clock durations are measured on.</param>
    public ZabbixWireTelemetry(IMeterFactory? meters, TimeProvider? time = null)
        : base(s_activities, ZabbixTelemetryNames.Wire, DurationInstrument, "How long one framed exchange took, connect to reply.", meters, time)
    {
        _failovers = Meter.CreateCounter<long>(FailoverInstrument, unit: "{failover}", description: "Connect-time failovers from one node to the next.");
        _redirects = Meter.CreateCounter<long>(RedirectInstrument, unit: "{redirect}", description: "Replies that redirected the request to another node.");
    }

    /// <summary>
    /// The instance an exchange built without a container reports to: one per process, owning one
    /// <see cref="ZabbixTelemetryNames.Wire"/> meter, because an exchange is a delegate with no lifetime to dispose a meter
    /// of its own at (a meter per delegate would stay registered for the life of the process).
    /// </summary>
    internal static ZabbixWireTelemetry Shared => s_fallback.Value;

    internal ZabbixOperation StartExchange(bool notify, ZabbixTlsMode tls, ReadOnlySpan<byte> request)
        => Start(notify ? NotifySpan : ExchangeSpan, ActivityKind.Client, new TagList
        {
            { "network.transport", "tcp" },
            { "zabbix.tls.mode", TlsModeTag(tls) },
            { "rpc.system.name", "zabbix" },
            { "rpc.method", ZabbixRequestName.Of(request) },
        });

    /// <summary>Opens a server-wire request: the span is named for the request, as an RPC span is for its method.</summary>
    /// <param name="request">The request (<c>queue.get</c>, <c>status.get</c>, …).</param>
    /// <param name="node">The server, when the wire knows it.</param>
    /// <returns>The operation.</returns>
    internal ZabbixOperation StartServerWireRequest(string request, ZabbixServerNode? node)
    {
        var tags = new TagList { { "rpc.system.name", "zabbix" }, { "rpc.method", request } };
        if (node is { } server)
        {
            tags.Add("server.address", server.Host);
            tags.Add("server.port", server.Port);
        }
        return Start(request, ActivityKind.Client, tags);
    }

    internal static TagList Peer(ZabbixServerNode node) => new() { { "server.address", node.Host }, { "server.port", node.Port } };

    internal void Failover(in ZabbixOperation operation, ZabbixServerNode node, string errorType)
    {
        TagList tags = Peer(node);
        tags.Add("error.type", errorType);
        _failovers.Add(1, tags);
        Event(operation, FailoverEvent, tags);
    }

    internal void Redirected(in ZabbixOperation operation, ZabbixServerNode node, bool reset)
    {
        TagList tags = Peer(node);
        tags.Add("zabbix.wire.redirect.reset", reset);
        _redirects.Add(1, tags);
        Event(operation, RedirectEvent, tags);
    }

    private static string TlsModeTag(ZabbixTlsMode tls) => tls switch
    {
        ZabbixTlsMode.Certificate => "cert",
        ZabbixTlsMode.PreSharedKey => "psk",
        _ => "unencrypted",
    };
}
