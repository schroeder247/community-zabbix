// This Source Code Form is subject to the terms of the Mozilla Public
// License, v. 2.0. Copyright (c) 2026 schroeder247. SPDX-License-Identifier: MPL-2.0
using System.Net;
using Microsoft.Extensions.Logging;

namespace Community.Zabbix.Protocol;

/// <summary>
/// Represents the framed-TCP connection to an ordered node list (a server's HA nodes, a proxy and its fallback): the real
/// transport beneath a <see cref="ZabbixWireClient"/>, the way <see cref="SocketsHttpHandler"/> is the real handler beneath an
/// <see cref="HttpClient"/>. Take its <see cref="ZabbixExchangeCallback"/> to decorate it (a log, a fault injector) or to speak a
/// request no client models, and hand it to a <see cref="ZabbixWireClient"/>.
/// </summary>
/// <remarks>
/// <para>Each exchange starts at the node that last answered; a node that cannot be connected to, or whose TLS handshake
/// fails, is logged and the next one is tried, as upstream's <c>zbx_connect_to_server</c> does. Once a request is written
/// nothing is sent again: the failure is the caller's, and the next exchange starts at the next node.</para>
/// <para>The link owns what certificate mode read from its files (the private key and the certificates), so dispose it
/// when done; an exchange taken from a disposed link throws <see cref="ObjectDisposedException"/>. A client handed one of
/// its exchanges owns nothing.</para>
/// </remarks>
public sealed class ZabbixWireLink : IDisposable
{
    private readonly ZabbixNodeLink _link;

    /// <summary>Initializes a link to <paramref name="nodes"/>.</summary>
    /// <param name="nodes">The nodes, in order of preference.</param>
    /// <exception cref="ArgumentException"><paramref name="nodes"/> is empty.</exception>
    public ZabbixWireLink(IEnumerable<DnsEndPoint> nodes)
        : this(nodes, connection: null)
    {
    }

    /// <summary>Initializes a link to <paramref name="nodes"/>.</summary>
    /// <param name="nodes">The nodes, in order of preference.</param>
    /// <param name="connection">The connection's words (deadline, TLS, compression) for every node; the defaults when <see langword="null"/>.</param>
    /// <exception cref="ArgumentException"><paramref name="nodes"/> is empty, or <paramref name="connection"/> does not validate.</exception>
    public ZabbixWireLink(IEnumerable<DnsEndPoint> nodes, ZabbixConnectionOptions? connection)
        : this(nodes, connection, time: null, logger: null)
    {
    }

    /// <summary>Initializes a link to <paramref name="nodes"/>.</summary>
    /// <param name="nodes">The nodes, in order of preference.</param>
    /// <param name="connection">The connection's words (deadline, TLS, compression) for every node; the defaults when <see langword="null"/>.</param>
    /// <param name="time">The clock the deadlines run on; the system clock when <see langword="null"/>.</param>
    /// <param name="logger">Where the link logs its failovers and redirects.</param>
    /// <exception cref="ArgumentException"><paramref name="nodes"/> is empty, or <paramref name="connection"/> does not validate.</exception>
    public ZabbixWireLink(IEnumerable<DnsEndPoint> nodes, ZabbixConnectionOptions? connection, TimeProvider? time, ILogger<ZabbixWireLink>? logger)
    {
        ArgumentNullException.ThrowIfNull(nodes);
        _link = new ZabbixNodeLink([.. nodes.Select(static node => (ZabbixServerNode)node)], connection, time, logger, telemetry: null);
    }

    /// <summary>Gets the node the next exchange starts at.</summary>
    public DnsEndPoint First => _link.First;

    /// <summary>
    /// Returns the link as a request/reply exchange: connect (failing over), send one frame, read one frame, close.
    /// </summary>
    /// <param name="followRedirects">
    /// Whether a proxy group's redirect reply is followed, as the agent and <c>zabbix_sender</c> follow it
    /// (<c>zbx_comms_exchange_with_redirect</c>): the request is sent to the redirect's target once. A server never sends
    /// one, so a request to a server does not need it.
    /// </param>
    /// <returns>The exchange, which uses this link for as long as the link lives.</returns>
    public ZabbixExchangeCallback AsExchange(bool followRedirects) => _link.Exchange(followRedirects);

    /// <summary>
    /// Returns the link as a request/reply exchange whose replies may be large frames: a proxy's configuration arrives
    /// as one, up to upstream's 16 GiB (<c>zbx_tcp_recv_large</c>), so a proxy uplink (<c>ZabbixProxyUplink</c>, in
    /// <c>Community.Zabbix.Proxy</c>) is built over this exchange, as the hosted proxy builds its own.
    /// </summary>
    /// <param name="followRedirects">Whether a proxy group's redirect reply is followed (<see cref="AsExchange(bool)"/>).</param>
    /// <param name="acceptLargeReplies">
    /// Whether a reply may be a large frame, capped at 16 GiB while <see cref="ZabbixConnectionOptions.MaxPayloadBytes"/>
    /// is unset; <see langword="false"/> keeps <see cref="ZabbixConnectionOptions.DefaultMaxPayloadBytes"/> (16 MiB), as
    /// <see cref="AsExchange(bool)"/> does. A set <see cref="ZabbixConnectionOptions.MaxPayloadBytes"/> caps either way.
    /// </param>
    /// <returns>The exchange, which uses this link for as long as the link lives.</returns>
    public ZabbixExchangeCallback AsExchange(bool followRedirects, bool acceptLargeReplies) => _link.Exchange(followRedirects, acceptLargeReplies);

    /// <summary>What certificate mode read, held until the link is disposed; <see langword="null"/> in the other modes.</summary>
    internal ZabbixCertificateTrust? Trust => _link.Trust;

    /// <summary>
    /// Returns the link as a notification, the active agent's heartbeat shape: connect (failing over), write one frame,
    /// then read until the peer closes, following a redirect if one comes back.
    /// </summary>
    /// <returns>The notification, which uses this link for as long as the link lives.</returns>
    public ZabbixNotificationCallback AsNotification() => _link.NotifyAsync;

    /// <summary>Releases what certificate mode read; an exchange already connected finishes on the stream it has.</summary>
    public void Dispose() => _link.Dispose();
}
