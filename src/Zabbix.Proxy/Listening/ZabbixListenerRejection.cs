// This Source Code Form is subject to the terms of the Mozilla Public
// License, v. 2.0. Copyright (c) 2026 schroeder247. SPDX-License-Identifier: MPL-2.0
namespace Community.Zabbix.Listening;

/// <summary>
/// The codes a listener refuses a connection with: the <see cref="ZabbixListenerTelemetry.RejectionTag"/> value and the
/// <c>Rejection</c> of event 6003. A code, never prose: a log query or an alert matches it, and it carries nothing of the
/// listener's configuration.
/// </summary>
internal static class ZabbixListenerRejection
{
    /// <summary>The peer has as many connections open as <c>MaxConnectionsPerPeer</c> allows.</summary>
    public const string PeerLimit = "peer_limit";

    /// <summary>The peer is not in <c>AllowedPeers</c>.</summary>
    public const string PeerNotAllowed = "peer_not_allowed";

    /// <summary>The listener has <c>MaxConcurrentConnections</c> open and its queue is full.</summary>
    public const string ListenerFull = "listener_full";

    /// <summary>The connection's deadline passed before it was answered.</summary>
    public const string Timeout = "timeout";

    /// <summary>The peer connected unencrypted and <c>TlsAccept</c> does not include unencrypted.</summary>
    public const string UnencryptedNotAllowed = "unencrypted_not_allowed";

    /// <summary>The peer began a TLS handshake and <c>TlsAccept</c> includes neither psk nor cert.</summary>
    public const string TlsNotAllowed = "tls_not_allowed";

    /// <summary>The accept refused the connection for another reason.</summary>
    public const string ConnectionRefused = "connection_refused";
}
