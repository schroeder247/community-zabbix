// This Source Code Form is subject to the terms of the Mozilla Public
// License, v. 2.0. Copyright (c) 2026 schroeder247. SPDX-License-Identifier: MPL-2.0
namespace Community.Zabbix.Connector;

/// <summary>
/// Who may post to a connector endpoint. The endpoint fails closed, as every listener does: it is mapped only when a
/// <see cref="BearerToken"/>, an <see cref="AllowedPeers"/> list, or <see cref="AllowAnonymous"/> says who that is. With a
/// token and a peer list, a request needs both.
/// </summary>
public sealed class ZabbixConnectorOptions
{
    /// <summary>
    /// The token the connector object sends as <c>Authorization: Bearer &lt;token&gt;</c> (its <c>authtype</c> bearer); a
    /// request without it is answered 401, which the server does not retry.
    /// </summary>
    [ZabbixSecret]
    public string? BearerToken { get; set; }

    /// <summary>
    /// The addresses, CIDR blocks and DNS names that may post, as a listener's <c>AllowedPeers</c> takes them (see
    /// <see cref="Listening.ZabbixAllowedPeers"/>); a request from anywhere else is answered 403.
    /// </summary>
    public IList<string> AllowedPeers { get; } = [];

    /// <summary>Whether anyone who reaches the endpoint may post: the explicit opt-out of the token and the peer list, for an endpoint the application's own authorization (or the network) already guards.</summary>
    public bool AllowAnonymous { get; set; }
}
