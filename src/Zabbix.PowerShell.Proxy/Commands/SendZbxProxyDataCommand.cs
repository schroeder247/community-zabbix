// This Source Code Form is subject to the terms of the Mozilla Public
// License, v. 2.0. Copyright (c) 2026 schroeder247. SPDX-License-Identifier: MPL-2.0
using System.Management.Automation;
using System.Net;
using Community.Zabbix.Protocol;
using Community.Zabbix.Proxy;

namespace Community.Zabbix.PowerShell.Commands;

/// <summary>
/// <para type="synopsis">Pushes one page of history as an active proxy, with no session.</para>
/// <para type="description">One <c>proxy data</c>
/// exchange with the server, as the proxy named <c>-ProxyName</c>. A server that requires TLS from the proxy (its
/// <c>tls_accept</c>) is reached with <c>-PskIdentity</c> and <c>-Psk</c>, or the certificate parameters, as the proxy's
/// own <c>TLSConnect</c> would.</para>
/// </summary>
[Cmdlet(VerbsCommunications.Send, "ZbxProxyData", SupportsShouldProcess = true, ConfirmImpact = ConfirmImpact.Low)]
[OutputType(typeof(ZabbixProxyResponse))]
public sealed class SendZbxProxyDataCommand : ZabbixWireCmdlet
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

    /// <summary>The values to push.</summary>
    [Parameter(Mandatory = true, Position = 2, ValueFromPipeline = true)]
    public ZabbixProxyHistoryItem[] History { get; set; } = null!;

    /// <summary>Whether more is held behind this page.</summary>
    [Parameter]
    public SwitchParameter More { get; set; }

    /// <summary>How old, in seconds, the oldest value left behind is; 0 when nothing is left.</summary>
    [Parameter]
    [ValidateRange(0, long.MaxValue)]
    public long ProxyDelay { get; set; }

    /// <summary>
    /// The proxy version to announce (<c>7.0.0</c>); 7.4 when omitted. A server refuses a proxy newer than itself ("current
    /// proxy version is not supported by server"), as a 7.0 server does with the 7.4 default.
    /// </summary>
    [Parameter]
    public Version? Version { get; set; }

    private readonly List<ZabbixProxyHistoryItem> _history = [];

    /// <inheritdoc/>
    /// <remarks>Gathers: piped values are one page, sent at the end, not one upload and connection per record.</remarks>
    protected override void ProcessRecord() => _history.AddRange(History);

    /// <inheritdoc/>
    protected override void EndProcessing()
    {
        if (_history.Count == 0) return;
        IReadOnlyList<DnsEndPoint> nodes = Server;
        TimeProvider time = ZabbixContext.Time;
        var options = new ZabbixConnectionOptions { Tls = TlsOptions };
        ZabbixProxyHistoryItem[] history = [.. _history];
        // It writes values into the server's history, like Send-ZbxData/-History (ConfirmImpact Low).
        if (!ShouldProcess($"{history.Length} value(s) as proxy {ProxyName}", "Send proxy data")) return;
        if (TryRun(async cancellationToken =>
            {
                // The connection lives for the upload: disposing it releases what certificate mode read.
                using ZabbixWireLink link = ZabbixWireLinks.Open(nodes, options, time, loggers: null);
                var uplink = new ZabbixProxyUplink(link.AsExchange(), ProxyName, Version, session: null, loggers: null, time);
                return await uplink.UploadAsync(new ZabbixProxyDataPage(History: history), More ? ZabbixProxyBacklog.More : ZabbixProxyBacklog.None,
                    TimeSpan.FromSeconds(ProxyDelay), cancellationToken).ConfigureAwait(false);
            }, out ZabbixProxyResponse? response, ProxyName))
        {
            WriteObject(response);
        }
    }
}
