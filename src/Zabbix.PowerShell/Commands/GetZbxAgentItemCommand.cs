// This Source Code Form is subject to the terms of the Mozilla Public
// License, v. 2.0. Copyright (c) 2026 schroeder247. SPDX-License-Identifier: MPL-2.0
using System.Management.Automation;
using System.Net;
using Community.Zabbix.PowerShell.Diagnostics;
using Community.Zabbix.Protocol;

namespace Community.Zabbix.PowerShell.Commands;

/// <summary>
/// <para type="synopsis">Queries an item value straight from a Zabbix agent (the <c>zabbix_get</c> protocol) — no server, no connection needed.</para>
/// <para type="description">An agent that accepts only TLS (its <c>TLSAccept</c>) is asked over TLS as <c>zabbix_get</c>
/// asks it: <c>-PskIdentity</c> and <c>-Psk</c>, or <c>-Certificate</c>, <c>-CertificateKey</c> and
/// <c>-CertificateAuthority</c>, with <c>-Tls</c> to choose when both are given.</para>
/// </summary>
[Cmdlet(VerbsCommon.Get, "ZbxAgentItem")]
[OutputType(typeof(ZabbixAgentAnswer))]
public sealed class GetZbxAgentItemCommand : ZabbixWireCmdlet
{
    /// <summary>
    /// The agent: <c>host</c> or <c>host:port</c> (port 10050 when omitted; an IPv6 address with a port is bracketed,
    /// <c>[::1]:10050</c>).
    /// </summary>
    [Parameter(Mandatory = true, Position = 0, ValueFromPipelineByPropertyName = true)]
    [Alias("ComputerName")]
    [ZabbixEndPoint(ZabbixWireClient.DefaultAgentPort)]
    public DnsEndPoint? Agent { get; set; }

    /// <summary>The item key(s) to query.</summary>
    [Parameter(Mandatory = true, Position = 1)]
    [ValidateNotNullOrEmpty]
    public string[]? Key { get; set; }

    /// <summary>The connect/read timeout, 1 to 300 seconds; 10 seconds by default. Takes Zabbix's time suffix (<c>'30s'</c>, <c>'5m'</c>), seconds or a time span.</summary>
    [Parameter]
    [ZabbixDuration(MinimumSeconds = 1, MaximumSeconds = 300)]
    public TimeSpan Timeout { get; set; } = TimeSpan.FromSeconds(10);

    /// <inheritdoc/>
    protected override void ProcessRecord()
    {
        DnsEndPoint node = Agent!;
        var connection = new ZabbixConnectionOptions { Timeout = Timeout, Tls = TlsOptions };
        // No connection: the client logs to this command's own streams.
        using var loggers = new ZabbixCommandLoggers(ZabbixContext.Time);
        TimeProvider time = ZabbixContext.Time;
        // Opened through the bridge: TLS words that do not validate, or certificate files that cannot be read, are this
        // record's error, as a failed exchange is. The connection lives for this record's keys: disposing it releases
        // what certificate mode read.
        if (!TryRun(_ => Task.FromResult(ZabbixWireLinks.Open([node], connection, time, loggers.Factory)), out ZabbixWireLink? link, node)) return;
        using ZabbixWireLink owned = link;
        using var wire = new ZabbixWireClient(owned.AsExchange(followRedirects: false), new ZabbixWireOptions(), loggers.Factory, time);
        foreach (string key in Key!)
        {
            if (TryRun(cancellationToken => wire.GetAgentItemAsync(ZabbixEndPoint.Format(node), key, cancellationToken), out ZabbixAgentAnswer? answer, key))
                WriteObject(answer);
        }
    }
}
