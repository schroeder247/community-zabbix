// This Source Code Form is subject to the terms of the Mozilla Public
// License, v. 2.0. Copyright (c) 2026 schroeder247. SPDX-License-Identifier: MPL-2.0
using System.Management.Automation;

namespace Community.Zabbix.PowerShell.Commands;

/// <summary>
/// <para type="synopsis">Creates a server-side task (<c>task.create</c>): a check-now, a diagnostic information
/// dump, or a proxy configuration reload.</para>
/// <para type="description">
/// <c>-Type</c> selects the task (<c>CheckNow</c>, <c>DiagnosticInformation</c> or <c>ProxyConfigurationReload</c>);
/// <c>-Request</c> is its body, a <c>ZabbixTaskRequest</c> whose members carry each kind's fields (<c>ItemId</c> for
/// a check-now; <c>Historycache</c>, <c>Valuecache</c>, <c>Preprocessing</c>, <c>Alerting</c> or <c>Lld</c> for a
/// diagnostic). Reads the created tasks back (<c>task.get</c>) and writes them. For the common cases,
/// <c>Invoke-ZbxCheckNow</c>, <c>Invoke-ZbxDiagnostic</c> and <c>Sync-ZbxProxyConfig</c> are simpler. Supports
/// <c>-WhatIf</c>/<c>-Confirm</c> (ConfirmImpact Medium).
/// </para>
/// </summary>
/// <remarks>
/// Hand-written, not generated: a thin cmdlet over <c>task.create</c>. The typed <see cref="ZabbixTaskRequest"/>
/// already carries each kind's shape, so nothing here duplicates it. Reads the created task(s) back in one
/// <c>task.get</c>, the create-then-get contract every generated <c>New-</c> cmdlet follows. It is the lower-level
/// escape hatch beside <see cref="InvokeZbxCheckNowCommand"/>, <see cref="InvokeZbxDiagnosticCommand"/> and
/// <see cref="SyncZbxProxyConfigCommand"/>, not a replacement.
/// </remarks>
[Cmdlet(VerbsCommon.New, "ZbxTask", SupportsShouldProcess = true, ConfirmImpact = ConfirmImpact.Medium)]
[OutputType(typeof(ZabbixTaskRecord))]
public sealed class NewZbxTaskCommand : ZabbixServerCmdlet
{
    /// <summary>The kind of task.</summary>
    [Parameter(Mandatory = true, Position = 0)]
    public ZabbixTaskType Type { get; set; }

    /// <summary>The task's body, matching <c>-Type</c> (<c>ItemId</c> for <c>CheckNow</c>, ...).</summary>
    [Parameter(Mandatory = true, Position = 1)]
    public ZabbixTaskRequest Request { get; set; } = null!;

    /// <summary>The proxy to run the task on, when the target is proxy-monitored.</summary>
    [Parameter]
    public ulong? ProxyId { get; set; }

    /// <inheritdoc/>
    protected override void ProcessRecord()
    {
        if (!ShouldProcess($"new {Type} task", "New task")) return;
        if (!TryRun(CreateAndReadBackAsync, out IReadOnlyList<ZabbixTaskRecord>? created, Type)) return;
        foreach (ZabbixTaskRecord task in created) WriteObject(task);
    }

    private async Task<IReadOnlyList<ZabbixTaskRecord>> CreateAndReadBackAsync(CancellationToken cancellationToken)
    {
        var request = new ZabbixTaskCreateRequest { Type = Type, Request = Request, ProxyId = ProxyId };
        ulong id = await ZabbixClient.Tasks.CreateAsync(request, cancellationToken).ConfigureAwait(false);
        var parameters = new ZabbixTaskGetParameters { TaskIds = [id] };
        return await ZabbixClient.Tasks.GetAsync(parameters, cancellationToken).ConfigureAwait(false);
    }
}
