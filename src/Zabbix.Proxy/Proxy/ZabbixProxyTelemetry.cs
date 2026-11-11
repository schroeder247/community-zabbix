// This Source Code Form is subject to the terms of the Mozilla Public
// License, v. 2.0. Copyright (c) 2026 schroeder247. SPDX-License-Identifier: MPL-2.0
using System.Diagnostics;
using System.Diagnostics.Metrics;
using System.Net;
using Community.Zabbix.Diagnostics;
using Community.Zabbix.Listening;

namespace Community.Zabbix.Proxy;

/// <summary>
/// Publishes the proxy layer's traces and metrics on <see cref="ZabbixTelemetryNames.Proxy"/>: a span and a
/// duration sample per configuration exchange (<see cref="ConfigSpan"/>: an active pull, a passive push) and per history
/// page (<see cref="DataSpan"/>: an active upload or probe, a passive pull), written together by
/// <see cref="ZabbixLayerTelemetry.Finish"/>; and <see cref="ValuesInstrument"/>, the records by outcome
/// (<see cref="Uploaded"/>, <see cref="Held"/>, <see cref="Refused"/>, <see cref="Unmapped"/>, <see cref="Invalid"/>) and by the <c>proxy data</c>
/// kind they travel as (<see cref="KindTag"/>: <see cref="History"/>, <see cref="Discovery"/>, <see cref="Autoregistration"/>,
/// <see cref="Availability"/>, <see cref="Tasks"/>) — one instrument with a low-cardinality tag, not one per kind, so a
/// dashboard sums or splits them as it likes. The store's own instruments (depth, acknowledgements, drops) stay on
/// <see cref="ZabbixTelemetryNames.Store"/>, one store per kind.
/// </summary>
internal sealed class ZabbixProxyTelemetry : ZabbixLayerTelemetry
{
    /// <summary>The activity source and the meter.</summary>
    public const string Name = ZabbixTelemetryNames.Proxy;

    /// <summary>The duration histogram, seconds, tagged <c>zabbix.proxy.operation</c> (<c>config</c>, <c>data</c>).</summary>
    public const string DurationInstrument = "zabbix.proxy.operation.duration";

    /// <summary>The records counter, tagged <c>zabbix.proxy.outcome</c> and <see cref="KindTag"/>.</summary>
    public const string ValuesInstrument = "zabbix.proxy.values";

    /// <summary>The span of one configuration exchange.</summary>
    public const string ConfigSpan = "zabbix.proxy.config";

    /// <summary>The span of one history page.</summary>
    public const string DataSpan = "zabbix.proxy.data";

    /// <summary>Values the server acknowledged.</summary>
    public const string Uploaded = "uploaded";

    /// <summary>Values in a page the server told the proxy to hold (uploads disabled); counted once, when held.</summary>
    public const string Held = "held";

    /// <summary>Values the trapper refused by Zabbix's rules (unknown host or key, item type, allowed hosts).</summary>
    public const string Refused = "refused";

    /// <summary>Stored values whose item left the configuration before they were sent.</summary>
    public const string Unmapped = "unmapped";

    /// <summary>
    /// Records in a page the server refused for its content while it took the proxy's other messages (a discovery row it
    /// cannot read): dropped, because the same page would be refused for ever.
    /// </summary>
    public const string Invalid = "invalid";

    /// <summary>The tag that names the <c>proxy data</c> kind a record travels as.</summary>
    public const string KindTag = "zabbix.proxy.kind";

    /// <summary>Item values: <c>history data</c>.</summary>
    public const string History = "history";

    /// <summary>Network discovery results: <c>discovery data</c>.</summary>
    public const string Discovery = "discovery";

    /// <summary>Active-agent autoregistrations: <c>auto registration</c>.</summary>
    public const string Autoregistration = "autoregistration";

    /// <summary>Interface availability changes: <c>interface availability</c>.</summary>
    public const string Availability = "interface_availability";

    /// <summary>Task results: <c>tasks</c>.</summary>
    public const string Tasks = "tasks";

    private static readonly ActivitySource s_proxySource = new(Name);

    private readonly Counter<long> _values;

    private static readonly Lazy<ZabbixProxyTelemetry> s_shared = new(() => new ZabbixProxyTelemetry(null, null));

    /// <summary>
    /// The instance an uplink built without a host reports to: one per process, owning one meter, because an uplink has no
    /// lifetime to dispose a meter of its own at (a meter per uplink would stay registered for the life of the process).
    /// </summary>
    internal static ZabbixProxyTelemetry Shared => s_shared.Value;

    internal ZabbixProxyTelemetry(IMeterFactory? meters, TimeProvider? time)
        : base(s_proxySource, Name, DurationInstrument, "How long one proxy exchange took: a configuration pull or push, a history page.", meters, time)
    {
        _values = Meter.CreateCounter<long>(ValuesInstrument, unit: "{record}", description: "Records through a Zabbix proxy, by outcome and proxy-data kind.");
        RefusedValues = new ZabbixLogOnce<(string Host, string Key, string Reason)>(4096, TimeSpan.FromHours(1), time ?? TimeProvider.System);
    }

    /// <summary>Which refused values are logged (7020): each host, key and reason once an hour; every one is counted (<see cref="Refused"/>).</summary>
    internal ZabbixLogOnce<(string Host, string Key, string Reason)> RefusedValues { get; }

    /// <summary>Opens a configuration exchange.</summary>
    /// <param name="proxy">The proxy's name.</param>
    /// <param name="passive">Whether the server came to the proxy.</param>
    /// <param name="peer">The server, when known.</param>
    /// <returns>The operation.</returns>
    internal ZabbixOperation StartConfig(string proxy, bool passive, EndPoint? peer = null)
        => Start(ConfigSpan, passive ? ActivityKind.Server : ActivityKind.Client, Tags(proxy, "config", passive, peer));

    /// <summary>Opens a history page.</summary>
    /// <param name="proxy">The proxy's name.</param>
    /// <param name="passive">Whether the server came to the proxy.</param>
    /// <param name="peer">The server, when known.</param>
    /// <returns>The operation.</returns>
    internal ZabbixOperation StartData(string proxy, bool passive, EndPoint? peer = null)
        => Start(DataSpan, passive ? ActivityKind.Server : ActivityKind.Client, Tags(proxy, "data", passive, peer));

    /// <summary>Counts records by outcome and kind.</summary>
    /// <param name="proxy">The proxy's name.</param>
    /// <param name="outcome">One of the outcome constants.</param>
    /// <param name="count">How many.</param>
    /// <param name="kind">One of the kind constants; <see cref="History"/> when not said.</param>
    internal void Values(string proxy, string outcome, long count, string kind = History)
    {
        if (count > 0) _values.Add(count, new TagList { { "zabbix.proxy.name", proxy }, { "zabbix.proxy.outcome", outcome }, { KindTag, kind } });
    }

    private static TagList Tags(string proxy, string operation, bool passive, EndPoint? peer)
    {
        var tags = new TagList { { "zabbix.proxy.name", proxy }, { "zabbix.proxy.operation", operation }, { "zabbix.proxy.mode", passive ? "passive" : "active" } };
        if (peer is IPEndPoint address)
        {
            // The Zabbix server is the TCP client of a passive proxy, so the peer is named by the socket (semconv
            // network.peer.*), not server.* (which on a server span names this side).
            tags.Add("network.peer.address", address.Address.ToString());
            tags.Add("network.peer.port", address.Port);
        }
        return tags;
    }
}
