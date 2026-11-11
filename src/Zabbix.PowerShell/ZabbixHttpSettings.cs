// This Source Code Form is subject to the terms of the Mozilla Public
// License, v. 2.0. Copyright (c) 2026 schroeder247. SPDX-License-Identifier: MPL-2.0
using System.Net;
using Microsoft.Extensions.DependencyInjection;

namespace Community.Zabbix.PowerShell;

/// <summary>
/// The HTTP knobs <c>Connect-ZbxServer</c> takes as <c>Invoke-RestMethod</c> does (<c>-SkipCertificateCheck</c>,
/// <c>-TimeoutSec</c>, <c>-Proxy</c>, <c>-ProxyCredential</c>), applied through the server's builder as an application
/// applies its own (<c>ConfigureHttpClient</c>, <c>WithTimeout</c>): the SDK's handler is configured, never replaced, and the
/// deadline is the standard resilience handler's.
/// </summary>
/// <param name="SkipCertificateCheck">Accepts any server certificate (a self-signed frontend).</param>
/// <param name="Timeout">How long one request may take, retries included; the SDK's deadlines when <see langword="null"/>.</param>
/// <param name="Proxy">The HTTP proxy; the system's when <see langword="null"/>.</param>
/// <param name="ProxyCredential">The proxy's credential.</param>
internal sealed record ZabbixHttpSettings(bool SkipCertificateCheck, TimeSpan? Timeout, Uri? Proxy, NetworkCredential? ProxyCredential)
{
    /// <summary>Applies the knobs to the server <paramref name="server"/> registers.</summary>
    /// <param name="server">The server's builder, from <c>AddZabbix</c>.</param>
    [System.Diagnostics.CodeAnalysis.SuppressMessage("Security", "CA5359:Do not disable certificate validation",
        Justification = "Only with -SkipCertificateCheck, the user's explicit choice, as Invoke-RestMethod takes it; Connect-ZbxServer warns when it is given.")]
    public void ApplyTo(IZabbixBuilder server)
    {
        ArgumentNullException.ThrowIfNull(server);
        if (SkipCertificateCheck || Proxy is not null)
        {
            server.ConfigureHttpClient(client => client.ConfigurePrimaryHttpMessageHandler((handler, _) =>
            {
                var sockets = (SocketsHttpHandler)handler;
                if (SkipCertificateCheck) sockets.SslOptions.RemoteCertificateValidationCallback = (_, _, _, _) => true;
                if (Proxy is not null)
                {
                    sockets.Proxy = new WebProxy(Proxy) { Credentials = ProxyCredential };
                    sockets.UseProxy = true;
                }
            }));
        }
        // One request may take the whole deadline, retries included.
        if (Timeout is { } deadline) server.WithTimeout(deadline);
    }
}
