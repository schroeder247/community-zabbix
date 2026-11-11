// This Source Code Form is subject to the terms of the Mozilla Public
// License, v. 2.0. Copyright (c) 2026 schroeder247. SPDX-License-Identifier: MPL-2.0
using System.Management.Automation;

namespace Community.Zabbix.PowerShell.Commands;

/// <summary>
/// <para type="synopsis">Asks the server to push its configuration to proxies now, rather than at their next sync (<c>task.create</c>, <c>ZBX_TM_DATA_TYPE_PROXYIDS</c>).</para>
/// </summary>
[Cmdlet(VerbsData.Sync, "ZbxProxyConfig", SupportsShouldProcess = true)]
[OutputType(typeof(ulong))]
public sealed class SyncZbxProxyConfigCommand : ZabbixServerCmdlet
{
    /// <summary>The proxies: ids, names or objects.</summary>
    [Parameter(Mandatory = true, Position = 0, ValueFromPipeline = true)]
    [Alias("Id")]
    [ZabbixReference<ZabbixProxy>]
    [ZabbixCompleter<ZabbixProxy>]
    public ZabbixReference<ZabbixProxy>[]? InputObject { get; set; }

    private readonly List<ZabbixReference<ZabbixProxy>> _proxies = [];

    /// <inheritdoc/>
    protected override void ProcessRecord() => _proxies.AddRange(InputObject!);

    /// <inheritdoc/>
    protected override void EndProcessing()
    {
        if (!TryConfirm(_proxies, "Sync configuration of proxy", force: false, out IReadOnlyList<ulong> ids)) return;
        var request = new ZabbixTaskCreateRequest
        {
            Type = ZabbixTaskType.ProxyConfigurationReload,
            Request = new ZabbixTaskRequest { ProxyIds = [.. ids] },
        };
        // The created task ids (task.create's taskids).
        if (TryRun(cancellationToken => ZabbixClient.Tasks.CreateAsync(request, cancellationToken), out ulong taskId, ids))
            WriteObject(taskId);
    }
}
