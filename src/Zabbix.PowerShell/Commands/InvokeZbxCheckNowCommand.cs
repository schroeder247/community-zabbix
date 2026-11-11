// This Source Code Form is subject to the terms of the Mozilla Public
// License, v. 2.0. Copyright (c) 2026 schroeder247. SPDX-License-Identifier: MPL-2.0
using System.Management.Automation;

namespace Community.Zabbix.PowerShell.Commands;

/// <summary>
/// <para type="synopsis">Asks the server to poll items now, rather than at their next scheduled check (<c>task.create</c>, <c>ZBX_TM_TASK_CHECK_NOW</c>).</para>
/// </summary>
[Cmdlet(VerbsLifecycle.Invoke, "ZbxCheckNow", SupportsShouldProcess = true)]
[OutputType(typeof(ulong))]
public sealed class InvokeZbxCheckNowCommand : ZabbixServerCmdlet
{
    /// <summary>The items: ids, names or objects.</summary>
    [Parameter(Mandatory = true, Position = 0, ValueFromPipeline = true)]
    [Alias("Id")]
    [ZabbixReference<ZabbixItem>]
    [ZabbixCompleter<ZabbixItem>]
    public ZabbixReference<ZabbixItem>[]? InputObject { get; set; }

    private readonly List<ZabbixReference<ZabbixItem>> _items = [];

    /// <inheritdoc/>
    protected override void ProcessRecord() => _items.AddRange(InputObject!);

    /// <inheritdoc/>
    protected override void EndProcessing()
    {
        if (!TryConfirm(_items, "Check item now", force: false, out IReadOnlyList<ulong> ids)) return;
        ZabbixTaskCreateRequest[] requests = [.. ids.Select(id => new ZabbixTaskCreateRequest
        {
            Type = ZabbixTaskType.CheckNow,
            Request = new ZabbixTaskRequest { ItemId = id },
        })];
        // The created task ids (task.create's taskids).
        if (TryRun(cancellationToken => ZabbixClient.Tasks.CreateAsync(requests, cancellationToken), out IReadOnlyList<ulong>? taskIds, ids))
            WriteObject(taskIds, enumerateCollection: true);
    }
}
