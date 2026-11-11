// This Source Code Form is subject to the terms of the Mozilla Public
// License, v. 2.0. Copyright (c) 2026 schroeder247. SPDX-License-Identifier: MPL-2.0
using System.Management.Automation;
using System.Net;
using System.Text.Json;

namespace Community.Zabbix.PowerShell.Commands;

/// <summary>
/// <para type="synopsis">Runs an item's collection and preprocessing on the server, as the UI's item "Test" does (<c>item.test</c>).</para>
/// <para type="description">
/// <c>-Data</c> is the request's <c>data</c> object exactly as the frontend sends it (<c>item</c>, <c>host</c>,
/// <c>options</c>) — a hashtable or a <c>[pscustomobject]</c>; every field <c>item.test</c> reads is available this way,
/// so the cmdlet stays a thin, typed door onto the server's own contract rather than a second copy of it.
/// </para>
/// </summary>
[Cmdlet(VerbsDiagnostic.Test, "ZbxItem")]
[OutputType("Community.Zabbix.PowerShell.ItemTestResult")]
public sealed class TestZbxItemCommand : ZabbixServerWireCmdlet
{
    /// <summary>The request's <c>data</c>: the item under test, its host, and the test options.</summary>
    [Parameter(Mandatory = true, Position = 0, ValueFromPipeline = true)]
    [ValidateNotNull]
    public object? Data { get; set; }

    /// <summary>
    /// The server's trapper: <c>host</c> or <c>host:port</c> (port 10051 when omitted; an IPv6 address with a port is
    /// bracketed, <c>[::1]:10051</c>). Several are the nodes of one HA cluster, tried in order. The connection's
    /// (<c>Connect-ZbxServer -Trapper</c>), else the active HA node's, when omitted.
    /// </summary>
    [Parameter]
    [ValidateNotNullOrEmpty]
    [ZabbixEndPoint]
    public DnsEndPoint[]? Server { get; set; }

    /// <inheritdoc/>
    protected override void ProcessRecord()
    {
        JsonElement data = ZabbixJsonShape.FromShell(Data);
        if (TryRun(async cancellationToken =>
            {
                return await CallWireAsync(Server, (wire, token) => wire.TestItemAsync(data, token), cancellationToken).ConfigureAwait(false);
            }, out ZabbixServerReply? reply, Data))
        {
            WriteObject(ZabbixJsonShape.ToShell(reply!.Data ?? ZabbixServerReplies.Envelope(reply), "Community.Zabbix.PowerShell.ItemTestResult"));
        }
    }
}
