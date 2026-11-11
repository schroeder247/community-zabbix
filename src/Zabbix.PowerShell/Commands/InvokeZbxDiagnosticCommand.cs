// This Source Code Form is subject to the terms of the Mozilla Public
// License, v. 2.0. Copyright (c) 2026 schroeder247. SPDX-License-Identifier: MPL-2.0
using System.Management.Automation;

namespace Community.Zabbix.PowerShell.Commands;

/// <summary>
/// <para type="synopsis">Asks the server for diagnostic information about its own internal state (<c>task.create</c>, <c>ZBX_TM_DATA_TYPE_DIAGINFO</c>).</para>
/// <para type="description"><c>-Request</c> selects what to report: the typed <c>ZabbixTaskRequest</c>'s <c>HistoryCache</c>/<c>ValueCache</c>/<c>Preprocessing</c>/<c>Alerting</c>/<c>Lld</c> sections, cast from a hashtable. Creates a task on the server, so it supports <c>-WhatIf</c>/<c>-Confirm</c> (ConfirmImpact Low).</para>
/// </summary>
[Cmdlet(VerbsLifecycle.Invoke, "ZbxDiagnostic", SupportsShouldProcess = true, ConfirmImpact = ConfirmImpact.Low)]
[OutputType(typeof(ulong))]
public sealed class InvokeZbxDiagnosticCommand : ZabbixServerCmdlet
{
    // CTask::validateCreate makes task.create's request fields exclusive per type (diaginfo takes only the
    // section objects — an itemid or proxyids beside them is 'unexpected parameter'), and a section with no options
    // means its default stats, "extend".
    private static readonly ZabbixTaskRequest s_everySection = new()
    {
        HistoryCache = new() { Statistics = ZabbixOutput.Extend },
        ValueCache = new() { Statistics = ZabbixOutput.Extend },
        Preprocessing = new() { Statistics = ZabbixOutput.Extend },
        Alerting = new() { Statistics = ZabbixOutput.Extend },
        LowLevelDiscovery = new() { Statistics = ZabbixOutput.Extend },
    };

    /// <summary>What to report; every section when omitted.</summary>
    [Parameter(Position = 0, ValueFromPipeline = true)]
    public ZabbixTaskRequest? Request { get; set; }

    /// <inheritdoc/>
    protected override void ProcessRecord()
    {
        // task.create writes a task row the server acts on: a change, so -WhatIf previews it (ConfirmImpact Low: it
        // reports, it alters nothing the server monitors, so it never prompts by default).
        if (!ShouldProcess(Connected.Name, "Create diagnostic task")) return;
        if (!TryRun(async cancellationToken =>
        {
            var request = new ZabbixTaskCreateRequest
            {
                Type = ZabbixTaskType.DiagnosticInformation,
                Request = Request ?? s_everySection,
            };
            return await ZabbixClient.Tasks.CreateAsync(request, cancellationToken).ConfigureAwait(false);
        }, out ulong taskId, Connected.Name)) return;
        // The created task's id (task.create's taskids).
        WriteObject(taskId);
    }
}
