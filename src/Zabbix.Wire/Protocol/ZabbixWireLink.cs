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
        : this(nodes, connection, loggers: null, time: null)
    {
    }

    /// <summary>Initializes a link to <paramref name="nodes"/>, with the application's logging and clock.</summary>
    /// <param name="nodes">The nodes, in order of preference.</param>
    /// <param name="connection">The connection's words (deadline, TLS, compression) for every node; the defaults when <see langword="null"/>.</param>
    /// <param name="loggers">Where the link logs its failovers and redirects; nowhere when <see langword="null"/>. The caller keeps owning it.</param>
    /// <param name="time">The clock the deadlines run on; the system clock when <see langword="null"/>.</param>
    /// <exception cref="ArgumentException"><paramref name="nodes"/> is empty, or <paramref name="connection"/> does not validate.</exception>
    public ZabbixWireLink(IEnumerable<DnsEndPoint> nodes, ZabbixConnectionOptions? connection, ILoggerFactory? loggers, TimeProvider? time)
    {
        ArgumentNullException.ThrowIfNull(nodes);
        _link = new ZabbixNodeLink([.. nodes.Select(static node => (ZabbixServerNode)node)], connection, time, loggers?.CreateLogger<ZabbixWireLink>(), telemetry: null);
    }

    /// <summary>Gets the node the next exchange starts at.</summary>
    public DnsEndPoint First => _link.First;

    /// <summary>
    /// Returns the link as a request/reply exchange: connect (failing over), send one frame, read one frame, close. A redirect
    /// reply isn't followed, and a reply is at most <see cref="ZabbixConnectionOptions.DefaultMaxPayloadBytes"/> unless
    /// <see cref="ZabbixConnectionOptions.MaxPayloadBytes"/> says otherwise.
    /// </summary>
    /// <returns>The exchange, which uses this link for as long as the link lives.</returns>
    public ZabbixExchangeCallback AsExchange() => AsExchange(ZabbixExchangeOptions.None);

    /// <summary>
    /// Returns the link as a request/reply exchange: connect (failing over), send one frame, read one frame, close.
    /// <see cref="ZabbixExchangeOptions.FollowRedirects"/> follows a proxy group's redirect, and
    /// <see cref="ZabbixExchangeOptions.AcceptLargeReplies"/> takes a large reply, such as a proxy's configuration.
    /// </summary>
    /// <param name="options">How the exchange treats a reply.</param>
    /// <returns>The exchange, which uses this link for as long as the link lives.</returns>
    /// <example>
    /// <code language="csharp">
    /// ZabbixExchangeCallback sending = link.AsExchange(ZabbixExchangeOptions.FollowRedirects);
    /// </code>
    /// </example>
    public ZabbixExchangeCallback AsExchange(ZabbixExchangeOptions options)
        => _link.Exchange(options.HasFlag(ZabbixExchangeOptions.FollowRedirects), options.HasFlag(ZabbixExchangeOptions.AcceptLargeReplies));

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
