// This Source Code Form is subject to the terms of the Mozilla Public
// License, v. 2.0. Copyright (c) 2026 schroeder247. SPDX-License-Identifier: MPL-2.0
using System.Management.Automation;

namespace Community.Zabbix.PowerShell.Commands;

/// <summary>
/// <para type="synopsis">Propagates a host group's permissions and tag filters to its subgroups (<c>hostgroup.propagate</c>).</para>
/// </summary>
[Cmdlet(VerbsLifecycle.Invoke, "ZbxHostGroupPropagation", SupportsShouldProcess = true, ConfirmImpact = ConfirmImpact.Medium)]
public sealed class InvokeZbxHostGroupPropagationCommand : ZabbixServerCmdlet
{
    /// <summary>The host groups: ids, names or objects.</summary>
    [Parameter(Mandatory = true, Position = 0, ValueFromPipeline = true)]
    [Alias("Id")]
    [ZabbixReference<ZabbixHostGroup>]
    [ZabbixCompleter<ZabbixHostGroup>]
    public ZabbixReference<ZabbixHostGroup>[]? InputObject { get; set; }

    /// <summary>Propagates the group's user permissions to its subgroups.</summary>
    [Parameter]
    public SwitchParameter Permissions { get; set; }

    /// <summary>Propagates the group's tag filters to its subgroups.</summary>
    [Parameter]
    public SwitchParameter TagFilters { get; set; }

    /// <summary>
    /// Propagates without the confirmation prompt a lowered <c>$ConfirmPreference</c> would raise; <c>-WhatIf</c> and an
    /// explicit <c>-Confirm</c> still apply.
    /// </summary>
    [Parameter]
    public SwitchParameter Force { get; set; }

    private readonly List<ZabbixReference<ZabbixHostGroup>> _groups = [];

    /// <inheritdoc/>
    protected override void ProcessRecord() => _groups.AddRange(InputObject!);

    /// <inheritdoc/>
    protected override void EndProcessing()
    {
        if (!TryConfirm(_groups, "Propagate to the subgroups of host group", Force, out IReadOnlyList<ulong> ids)) return;
        var request = new ZabbixHostGroupPropagateRequest
        {
            Groups = [.. ids.Select(id => new ZabbixHostGroupPropagateGroupRequest { GroupId = id })],
            Permissions = Permissions.IsPresent,
            TagFilters = TagFilters.IsPresent,
        };
        TryRun(cancellationToken => ZabbixClient.HostGroups.PropagateAsync(request, cancellationToken), out _, ids);
    }
}
