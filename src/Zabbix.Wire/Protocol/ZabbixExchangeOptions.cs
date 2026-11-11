// This Source Code Form is subject to the terms of the Mozilla Public
// License, v. 2.0. Copyright (c) 2026 schroeder247. SPDX-License-Identifier: MPL-2.0
namespace Community.Zabbix.Protocol;

/// <summary>Specifies how an exchange that <see cref="ZabbixWireLink.AsExchange(ZabbixExchangeOptions)"/> returns treats a reply.</summary>
[Flags]
public enum ZabbixExchangeOptions
{
    /// <summary>A redirect reply isn't followed, and a reply is at most <see cref="ZabbixConnectionOptions.DefaultMaxPayloadBytes"/> (16 MiB).</summary>
    None = 0,

    /// <summary>
    /// A proxy group's redirect reply is followed, as the agent and <c>zabbix_sender</c> follow it
    /// (<c>zbx_comms_exchange_with_redirect</c>): the request is sent to the redirect's target once. A server never sends
    /// one, so a request to a server doesn't need it.
    /// </summary>
    FollowRedirects = 1,

    /// <summary>
    /// A reply may be a large frame, capped at 16 GiB while <see cref="ZabbixConnectionOptions.MaxPayloadBytes"/> is unset:
    /// a proxy's configuration arrives as one (<c>zbx_tcp_recv_large</c>), so a proxy uplink is built over such an exchange.
    /// A set <see cref="ZabbixConnectionOptions.MaxPayloadBytes"/> caps the reply either way.
    /// </summary>
    AcceptLargeReplies = 2,
}
