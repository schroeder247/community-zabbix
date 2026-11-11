// This Source Code Form is subject to the terms of the Mozilla Public
// License, v. 2.0. Copyright (c) 2026 schroeder247. SPDX-License-Identifier: MPL-2.0
using System.Collections;
using System.Management.Automation;
using System.Text.Json;

namespace Community.Zabbix.PowerShell.Commands;

/// <summary>
/// <para type="synopsis">Calls a raw Zabbix API method — the escape hatch for anything the module has no typed command for.</para>
/// <para type="description">
/// <c>-Body</c> is the method's parameters exactly as the API takes them: a hashtable, a <c>[pscustomobject]</c>, or a
/// JSON string; the result comes back the same way <c>ConvertFrom-Json</c> would shape it. A <c>*.get</c> method
/// (and <c>apiinfo.version</c>) is read-only and skips the <c>-WhatIf</c>/<c>-Confirm</c> gate; every other method can
/// mutate, so it is gated like any other change (ConfirmImpact Medium).
/// </para>
/// </summary>
/// <remarks><c>-Body</c> is shaped into JSON through <see cref="ZabbixJsonShape"/>.</remarks>
[Cmdlet(VerbsLifecycle.Invoke, "ZbxApi", SupportsShouldProcess = true, ConfirmImpact = ConfirmImpact.Medium)]
[OutputType(typeof(object))]
public sealed class InvokeZbxApiCommand : ZabbixServerCmdlet
{
    /// <summary>The API method, e.g. <c>host.get</c>; Tab completes the methods the connected server's version has.</summary>
    [Parameter(Mandatory = true, Position = 0)]
    [ValidateNotNullOrWhiteSpace]
    [ZabbixApiMethodCompleter]
    public string? Method { get; set; }

    /// <summary>The method's parameters; none (<c>{}</c>) when omitted.</summary>
    [Parameter(Position = 1, ValueFromPipeline = true)]
    public object? Body { get; set; }

    /// <inheritdoc/>
    protected override void ProcessRecord()
    {
        bool isRead = Method!.EndsWith(".get", StringComparison.OrdinalIgnoreCase) || string.Equals(Method, "apiinfo.version", StringComparison.OrdinalIgnoreCase);
        if (!isRead && !ShouldProcess($"Zabbix API method '{Method}'", "Invoke API method")) return;

        // No -Body is no parameters: {} (JSON-RPC params must be an object or an array; null is -32600).
        JsonElement parameters = ZabbixJsonShape.FromShell(Body ?? new Hashtable());
        if (TryRun(async cancellationToken => await ZabbixClient.SendAsync(Method!, parameters, cancellationToken).ConfigureAwait(false),
                out JsonElement result, Method))
        {
            WriteObject(ZabbixJsonShape.ToShell(result), enumerateCollection: true);
        }
    }
}
