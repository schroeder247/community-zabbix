// This Source Code Form is subject to the terms of the Mozilla Public
// License, v. 2.0. Copyright (c) 2026 schroeder247. SPDX-License-Identifier: MPL-2.0
using System.Management.Automation;
using System.Net;
using System.Text.Json;
using System.Text.Json.Serialization.Metadata;

namespace Community.Zabbix.PowerShell.Commands;

/// <summary>
/// <para type="synopsis">Runs an item's collection and preprocessing on the server, as the UI's item "Test" does (<c>item.test</c>).</para>
/// <para type="description">
/// <c>-Data</c> is the request: a <c>ZabbixItemTestRequest</c>, or a hashtable or a <c>[pscustomobject]</c> shaped as the
/// frontend sends it (<c>item</c>, <c>host</c>, <c>options</c>), by wire names or member names. A field the request
/// doesn't model is sent as it is, so every field <c>item.test</c> reads is available this way.
/// </para>
/// </summary>
[Cmdlet(VerbsDiagnostic.Test, "ZbxItem")]
[OutputType(typeof(ZabbixItemTestResult))]
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
        ZabbixItemTestRequest request;
        try
        {
            request = ZabbixJsonBinder.Bind(Data, (JsonTypeInfo<ZabbixItemTestRequest>)ZabbixWireJson.Options.GetTypeInfo(typeof(ZabbixItemTestRequest)));
        }
        catch (Exception unshaped) when (unshaped is ArgumentException or JsonException)
        {
            WriteError(new ErrorRecord(unshaped, "ZabbixItemTestRequestInvalid", ErrorCategory.InvalidArgument, Data));
            return;
        }
        if (TryRun(async cancellationToken =>
            {
                return await CallWireAsync(Server, (wire, token) => wire.TestItemAsync(request, token), cancellationToken).ConfigureAwait(false);
            }, out ZabbixItemTestResult? result, Data))
        {
            WriteObject(result);
        }
    }
}
