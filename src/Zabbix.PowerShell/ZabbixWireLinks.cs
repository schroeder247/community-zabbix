// This Source Code Form is subject to the terms of the Mozilla Public
// License, v. 2.0. Copyright (c) 2026 schroeder247. SPDX-License-Identifier: MPL-2.0
using System.Diagnostics;
using System.Net;
using Community.Zabbix.Protocol;
using Microsoft.Extensions.Logging;

namespace Community.Zabbix.PowerShell;

/// <summary>
/// Opens the connection a wire command or session exchanges over: the SDK's public <see cref="ZabbixWireLink"/> over the
/// nodes, as an application builds one to share a node list and its key files across clients. In certificate mode it
/// holds the private key and the certificates it read from the TLS files. The opener owns the link
/// and disposes it when it is done (a command when its call ends, a session when it stops), so the key is released then,
/// not when the collector reaches it. A wire client or proxy uplink is built over the link's exchange and owns nothing.
/// </summary>
/// <remarks>
/// Every link is announced on the <see cref="DiagnosticListener"/> named <see cref="DiagnosticName"/> as a
/// <see cref="LinkOpened"/> event whose payload is the link itself: the in-process observation
/// <see cref="DiagnosticSource"/> is for, by which a test holds each opener to disposing what it opened.
/// </remarks>
internal static class ZabbixWireLinks
{
    /// <summary>The name of the diagnostic listener the links are announced on.</summary>
    internal const string DiagnosticName = "Community.Zabbix.PowerShell.Wire";

    /// <summary>The event a link is announced with; its payload is the <see cref="ZabbixWireLink"/>.</summary>
    internal const string LinkOpened = "LinkOpened";

    private static readonly DiagnosticListener s_diagnostics = new(DiagnosticName);

    /// <summary>Opens a link over <paramref name="nodes"/>, tried in order; the caller disposes it.</summary>
    /// <param name="nodes">The nodes, in order of preference.</param>
    /// <param name="options">The connection's words, TLS included.</param>
    /// <param name="time">The clock the deadlines run on.</param>
    /// <param name="loggers">Where the link logs its failovers and redirects; nowhere when <see langword="null"/>.</param>
    /// <returns>The link, owned by the caller.</returns>
    /// <exception cref="ArgumentException"><paramref name="nodes"/> is empty, or <paramref name="options"/> does not validate.</exception>
    /// <exception cref="IOException">A certificate mode file cannot be read.</exception>
    /// <exception cref="System.Security.Cryptography.CryptographicException">A certificate mode file is not PEM, or the key does not match the certificate.</exception>
    public static ZabbixWireLink Open(IReadOnlyList<DnsEndPoint> nodes, ZabbixConnectionOptions options, TimeProvider time, ILoggerFactory? loggers)
    {
        var link = new ZabbixWireLink(nodes, options, loggers, time);
        if (s_diagnostics.IsEnabled(LinkOpened)) s_diagnostics.Write(LinkOpened, link);
        return link;
    }
}
