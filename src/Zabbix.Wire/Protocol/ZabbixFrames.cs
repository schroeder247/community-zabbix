// This Source Code Form is subject to the terms of the Mozilla Public
// License, v. 2.0. Copyright (c) 2026 schroeder247. SPDX-License-Identifier: MPL-2.0
using System.Net.Sockets;
using System.Security.Authentication;
using Community.Zabbix.Diagnostics;
using Microsoft.Extensions.Logging;

namespace Community.Zabbix.Protocol;

/// <summary>
/// Provides the framed-TCP exchanges and notifications over a node list. <see cref="Over"/> is the request/reply
/// exchange every Zabbix wire protocol is — one connection per request — and <see cref="Notify"/> the heartbeat's
/// write-then-listen. Both fail over as upstream does (<c>zbx_connect_to_server</c> and
/// <c>zbx_comms_exchange_with_redirect</c>, <c>src/libs/zbxcommshigh/commshigh.c</c>): the next node is tried only
/// when a node cannot be connected to or its TLS handshake fails; once the request is written nothing is re-sent, the
/// failure is the caller's, and the next call starts at the next node. The node that answered stays first (sticky), per
/// exchange instance. An exchange that follows redirects keeps the redirect it follows (a node and its revision) in front
/// of its list, as upstream's <c>addrs</c> vector does. Every protocol client is a façade over one of these.
/// </summary>
/// <remarks>
/// The delegates these return hold a <see cref="ZabbixNodeLink"/> nobody disposes, so in certificate mode the certificate
/// and key it read are released only when the delegate is collected. An owner that keeps an exchange for its lifetime
/// builds the <see cref="ZabbixNodeLink"/> itself and disposes it, as the sender, the getter, the server wire and the
/// hosts do.
/// </remarks>
internal static class ZabbixFrames
{
    /// <summary>An exchange with one node: connect, wrap, send one frame, read one frame, close.</summary>
    /// <param name="node">Where.</param>
    /// <param name="options">The connection's words; the defaults when <see langword="null"/>.</param>
    /// <param name="time">The clock the deadline runs on.</param>
    /// <param name="logger">Where to log.</param>
    /// <param name="telemetry">Where spans and durations go; the process-wide <see cref="ZabbixTelemetryNames.Wire"/> meter when <see langword="null"/>.</param>
    public static ZabbixExchangeCallback Connect(ZabbixServerNode node, ZabbixConnectionOptions? options = null, TimeProvider? time = null, ILogger? logger = null, ZabbixWireTelemetry? telemetry = null)
        => Over([node], options, time, logger, telemetry);

    /// <summary>
    /// An exchange over an ordered node list — a server's HA nodes, or a proxy and its fallback. Each call starts at the
    /// node that last answered; a node that cannot be connected to is logged (1102) and the next is tried; a failure after
    /// the request was written is thrown, never retried here, and moves the next call to the next node.
    /// </summary>
    /// <param name="nodes">The nodes, in order of preference.</param>
    /// <param name="options">The connection's words, for every node.</param>
    /// <param name="time">The clock the deadlines run on.</param>
    /// <param name="logger">Where to log.</param>
    /// <param name="telemetry">Where spans and durations go; the process-wide <see cref="ZabbixTelemetryNames.Wire"/> meter when <see langword="null"/>.</param>
    /// <param name="followRedirects">
    /// Whether a proxy group's redirect reply is followed, as <c>zbx_comms_exchange_with_redirect</c> does for the agent
    /// and <c>zabbix_sender</c>: a new target (its revision not older than the one followed) goes in front of the list and
    /// the request is sent there once, without failover; a <c>reset</c> moves the node that answered behind the others and
    /// the request is sent again with failover; a second redirect in one exchange, or an older revision, fails the exchange
    /// with an <see cref="IOException"/>. Upstream's proxy uplink does not follow redirects (a server never sends one).
    /// </param>
    /// <param name="allowLarge">Whether the reply may be a large frame under the large cap (<c>zbx_tcp_recv_large</c>):
    /// upstream receives only a proxy's configuration this way.</param>
    /// <exception cref="ArgumentException"><paramref name="nodes"/> is empty or holds a <see langword="default"/> node, or <paramref name="options"/> does not validate.</exception>
    [System.Diagnostics.CodeAnalysis.SuppressMessage("Reliability", "CA2000:Dispose objects before losing scope", Justification = "The delegate is the link's only owner: its certificate trust is released when the delegate is collected (the remarks above); an owner that must release it sooner builds the link.")]
    public static ZabbixExchangeCallback Over(IReadOnlyList<ZabbixServerNode> nodes, ZabbixConnectionOptions? options = null, TimeProvider? time = null, ILogger? logger = null, ZabbixWireTelemetry? telemetry = null,
        bool followRedirects = false, bool allowLarge = false)
        => new ZabbixNodeLink(nodes, options, time, logger, telemetry).Exchange(followRedirects, allowLarge);

    /// <summary>
    /// A notification over an ordered node list, the heartbeat's shape (<c>send_heartbeat_msg</c>): connect (failing over as
    /// <see cref="Over"/> does), write the frame, then read until the peer closes or the deadline passes, because upstream
    /// answers a heartbeat only to redirect it. A redirect is followed (the heartbeat is sent once more, as
    /// <see cref="Over"/> follows one); a closed connection, a passed deadline or any other reply is the notification's
    /// success, as upstream reads a failed receive after a notification as "the other side closed the connection as expected".
    /// </summary>
    /// <param name="nodes">The nodes, in order of preference.</param>
    /// <param name="options">The connection's words, for every node.</param>
    /// <param name="time">The clock the deadlines run on.</param>
    /// <param name="logger">Where to log.</param>
    /// <param name="telemetry">Where spans and durations go; the process-wide <see cref="ZabbixTelemetryNames.Wire"/> meter when <see langword="null"/>.</param>
    /// <exception cref="ArgumentException"><paramref name="nodes"/> is empty or holds a <see langword="default"/> node, or <paramref name="options"/> does not validate.</exception>
    [System.Diagnostics.CodeAnalysis.SuppressMessage("Reliability", "CA2000:Dispose objects before losing scope", Justification = "The delegate is the link's only owner: its certificate trust is released when the delegate is collected (the remarks above); an owner that must release it sooner builds the link.")]
    public static ZabbixNotificationCallback Notify(IReadOnlyList<ZabbixServerNode> nodes, ZabbixConnectionOptions? options = null, TimeProvider? time = null, ILogger? logger = null, ZabbixWireTelemetry? telemetry = null)
        => new ZabbixNodeLink(nodes, options, time, logger, telemetry).NotifyAsync;

    /// <summary>
    /// Whether <paramref name="failure"/> is how an exchange fails: no node reachable (<see cref="SocketException"/>), a
    /// connection lost or a redirect not followed (<see cref="IOException"/>), a refused handshake
    /// (<see cref="AuthenticationException"/>), a reply that is not a frame (<see cref="InvalidDataException"/>), or the
    /// deadline (<see cref="TimeoutException"/>). A caller's own cancellation is not one: it stays an
    /// <see cref="OperationCanceledException"/>.
    /// </summary>
    /// <param name="failure">The exception an exchange threw.</param>
    public static bool IsExchangeFailure(Exception failure)
        => failure is SocketException or IOException or AuthenticationException or InvalidDataException or TimeoutException;
}
