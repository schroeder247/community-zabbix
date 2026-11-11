// This Source Code Form is subject to the terms of the Mozilla Public
// License, v. 2.0. Copyright (c) 2026 schroeder247. SPDX-License-Identifier: MPL-2.0
using System.Diagnostics;
using System.Diagnostics.Metrics;
using System.Net;
using Community.Zabbix.Diagnostics;

namespace Community.Zabbix.Listening;

/// <summary>
/// Publishes the listener layer's traces and metrics (<see cref="ZabbixTelemetryNames.Listener"/>). One
/// server span per accepted connection (one framed request each, so the connection's span is the request's), closed with
/// the connection-duration sample in the same call; the connections open now; the connections refused before they were
/// served, by reason. The names mirror what Kestrel's <c>ConnectionHandler</c> path would have published for free
/// (<c>kestrel.connection.duration</c>, <c>kestrel.active_connections</c>, <c>kestrel.rejected_connections</c>), under
/// the listener's own prefix because the listener owns its accept loop (see <see cref="ZabbixListener"/>).
/// </summary>
internal sealed class ZabbixListenerTelemetry : ZabbixLayerTelemetry
{
    /// <summary>The activity source and meter name.</summary>
    public const string Name = ZabbixTelemetryNames.Listener;

    /// <summary>The server span one connection opens.</summary>
    public const string RequestSpan = "zabbix.listener.request";

    /// <summary>How long a served connection lived, accept to close, in seconds; <c>error.type</c> on failure.</summary>
    public const string ConnectionDuration = "zabbix.listener.connection.duration";

    /// <summary>The connections being served now.</summary>
    public const string ActiveConnections = "zabbix.listener.active_connections";

    /// <summary>The connections refused before they were served, tagged <see cref="RejectionTag"/>.</summary>
    public const string RejectedConnections = "zabbix.listener.rejected_connections";

    /// <summary>Why a connection was refused: <c>peer_not_allowed</c>, <c>peer_limit</c>, <c>listener_full</c>, <c>tls_not_allowed</c>, <c>unencrypted_not_allowed</c>, <c>connection_refused</c>.</summary>
    public const string RejectionTag = "zabbix.listener.rejection";

    /// <summary>How the served connection was protected: <c>unencrypted</c>, <c>psk</c> or <c>cert</c>.</summary>
    public const string SecurityTag = "zabbix.tls.accept";

    private static readonly ActivitySource s_listenerSource = new(Name);

    private readonly UpDownCounter<long> _active;
    private readonly Counter<long> _rejected;

    /// <summary>The listener's telemetry on the host's meters.</summary>
    /// <param name="meters">The host's meter factory; an owned meter when <see langword="null"/>.</param>
    /// <param name="time">The clock durations are measured on.</param>
    public ZabbixListenerTelemetry(IMeterFactory? meters = null, TimeProvider? time = null)
        : base(s_listenerSource, Name, ConnectionDuration, "How long a connection to a Zabbix listener lived, accept to close.", meters, time)
    {
        _active = Meter.CreateUpDownCounter<long>(ActiveConnections, unit: "{connection}", description: "Connections a Zabbix listener is serving now.");
        _rejected = Meter.CreateCounter<long>(RejectedConnections, unit: "{connection}", description: "Connections a Zabbix listener refused before serving them.");
    }

    /// <summary>Opens a served connection: its server span, its start time, and one more active connection.</summary>
    /// <param name="local">Where the listener is bound.</param>
    /// <param name="peer">Who connected.</param>
    internal ZabbixOperation Accepted(EndPoint? local, EndPoint? peer)
    {
        var tags = new TagList { { "network.transport", "tcp" } };
        if (local is IPEndPoint server)
        {
            tags.Add("server.address", server.Address.ToString());
            tags.Add("server.port", server.Port);
        }
        _active.Add(1, tags);
        ZabbixOperation operation = Start(RequestSpan, ActivityKind.Server, tags);
        if (operation.Activity is { } activity && peer is IPEndPoint client)
        {
            activity.SetTag("client.address", client.Address.ToString());                        // span only: a metric tag per peer is unbounded
            activity.SetTag("client.port", client.Port);
        }
        return operation;
    }

    /// <summary>Closes a served connection: the span and the duration sample together, and one fewer active connection.</summary>
    /// <param name="operation">What <see cref="Accepted"/> returned.</param>
    /// <param name="security">How the connection was protected, once known.</param>
    /// <param name="failure">The failure, when it failed.</param>
    /// <param name="errorType">An explicit <c>error.type</c> (a timeout, a refusal inside the connection).</param>
    internal void Closed(in ZabbixOperation operation, ZabbixTlsAcceptModes? security, Exception? failure = null, string? errorType = null)
    {
        _active.Add(-1, operation.Tags);
        TagList extra = security is { } kind ? new TagList { { SecurityTag, Word(kind) } } : default;
        Finish(operation, failure, errorType, extra);
    }

    /// <summary>Counts a connection refused before it was served.</summary>
    /// <param name="reason">The <see cref="RejectionTag"/> value.</param>
    internal void Rejected(string reason) => _rejected.Add(1, new KeyValuePair<string, object?>(RejectionTag, reason));

    /// <summary>Zabbix's own word for a connection kind (<c>TLSAccept</c>'s vocabulary).</summary>
    /// <param name="kind">One kind.</param>
    internal static string Word(ZabbixTlsAcceptModes kind) => kind switch
    {
        ZabbixTlsAcceptModes.Psk => "psk",
        ZabbixTlsAcceptModes.Certificate => "cert",
        _ => "unencrypted",
    };
}
