// This Source Code Form is subject to the terms of the Mozilla Public
// License, v. 2.0. Copyright (c) 2026 schroeder247. SPDX-License-Identifier: MPL-2.0
using System.Management.Automation;
using System.Net;
using Community.Zabbix.Protocol;
using Community.Zabbix.Proxy;

namespace Community.Zabbix.PowerShell.Commands;

/// <summary>
/// <para type="synopsis">Pulls an active proxy's configuration once, with no session.</para>
/// <para type="description">One <c>proxy config</c>
/// exchange with the server, as the proxy named <c>-ProxyName</c>; nothing keeps running (<c>Start-ZbxActiveProxy</c> is
/// the session that pulls on a cadence). Pipe the result back in as <c>-Held</c> for a delta pull (a revision of 0 asks
/// for everything). A server that requires TLS from the proxy is reached with <c>-PskIdentity</c> and <c>-Psk</c>, or
/// the certificate parameters.</para>
/// </summary>
[Cmdlet(VerbsCommunications.Receive, "ZbxProxyConfig")]
[OutputType(typeof(ZabbixProxyConfig))]
public sealed class ReceiveZbxProxyConfigCommand : ZabbixWireCmdlet
{
    /// <summary>The proxy's name as the server knows it.</summary>
    [Parameter(Mandatory = true, Position = 0)]
    [ValidateNotNullOrWhiteSpace]
    public string ProxyName { get; set; } = null!;

    /// <summary>
    /// The server: <c>host</c> or <c>host:port</c> (port 10051 when omitted; an IPv6 address with a port is bracketed,
    /// <c>[::1]:10051</c>). Several are the nodes of one HA cluster, tried in order.
    /// </summary>
    [Parameter(Mandatory = true, Position = 1)]
    [ValidateNotNullOrEmpty]
    [ZabbixEndPoint]
    public DnsEndPoint[] Server { get; set; } = null!;

    /// <summary>The configuration already held (a prior pull's result); everything is asked for when omitted.</summary>
    [Parameter(ValueFromPipeline = true)]
    public ZabbixProxyConfig? Held { get; set; }

    /// <inheritdoc/>
    protected override void ProcessRecord()
    {
        IReadOnlyList<DnsEndPoint> nodes = Server;
        TimeProvider time = ZabbixContext.Time;
        var options = new ZabbixConnectionOptions { Tls = TlsOptions };
        if (TryRun(async cancellationToken =>
            {
                // The connection lives for the pull: disposing it releases what certificate mode read. Large replies:
                // upstream's proxy receives its configuration as a large frame (zbx_tcp_recv_large).
                using ZabbixWireLink link = ZabbixWireLinks.Open(nodes, options, time, loggers: null);
                var uplink = new ZabbixProxyUplink(link.AsExchange(ZabbixExchangeOptions.AcceptLargeReplies), ProxyName, version: null, session: null, loggers: null, time);
                return await uplink.GetConfigAsync(Held ?? ZabbixProxyConfig.Empty, cancellationToken).ConfigureAwait(false);
            }, out ZabbixProxyConfig? config, ProxyName))
        {
            WriteObject(config);
        }
    }
}
