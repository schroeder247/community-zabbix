// This Source Code Form is subject to the terms of the Mozilla Public
// License, v. 2.0. Copyright (c) 2026 schroeder247. SPDX-License-Identifier: MPL-2.0
using System.Management.Automation;
using System.Net;

namespace Community.Zabbix.PowerShell.Commands;

/// <summary>
/// <para type="synopsis">Evaluates trigger expressions on the server (<c>expressions.evaluate</c>).</para>
/// </summary>
[Cmdlet(VerbsDiagnostic.Test, "ZbxExpression")]
[OutputType(typeof(ZabbixExpressionResult))]
public sealed class TestZbxExpressionCommand : ZabbixServerWireCmdlet
{
    /// <summary>The trigger expressions, e.g. <c>last(/host/key)&gt;100</c>.</summary>
    [Parameter(Mandatory = true, Position = 0, ValueFromPipeline = true)]
    [ValidateNotNullOrEmpty]
    public string[]? Expression { get; set; }

    /// <summary>
    /// The server's trapper: <c>host</c> or <c>host:port</c> (port 10051 when omitted; an IPv6 address with a port is
    /// bracketed, <c>[::1]:10051</c>). Several are the nodes of one HA cluster, tried in order. The connection's
    /// (<c>Connect-ZbxServer -Trapper</c>), else the active HA node's, when omitted.
    /// </summary>
    [Parameter]
    [ValidateNotNullOrEmpty]
    [ZabbixEndPoint]
    public DnsEndPoint[]? Server { get; set; }

    private readonly List<string> _expressions = [];

    /// <inheritdoc/>
    protected override void ProcessRecord() => _expressions.AddRange(Expression!);

    /// <inheritdoc/>
    protected override void EndProcessing()
    {
        if (TryRun(async cancellationToken =>
            {
                return await CallWireAsync(Server, (wire, token) => wire.EvaluateExpressionsAsync(_expressions, token), cancellationToken).ConfigureAwait(false);
            }, out IReadOnlyList<ZabbixExpressionResult>? results, Expression))
        {
            foreach (ZabbixExpressionResult result in results) WriteObject(result);
        }
    }
}
