// This Source Code Form is subject to the terms of the Mozilla Public
// License, v. 2.0. Copyright (c) 2026 schroeder247. SPDX-License-Identifier: MPL-2.0
using System.Management.Automation;

namespace Community.Zabbix.PowerShell.Commands;

/// <summary>
/// <para type="synopsis">Propagates a template group's permissions to its subgroups (<c>templategroup.propagate</c>).</para>
/// </summary>
[Cmdlet(VerbsLifecycle.Invoke, "ZbxTemplateGroupPropagation", SupportsShouldProcess = true, ConfirmImpact = ConfirmImpact.Medium)]
public sealed class InvokeZbxTemplateGroupPropagationCommand : ZabbixServerCmdlet
{
    /// <summary>The template groups: ids, names or objects.</summary>
    [Parameter(Mandatory = true, Position = 0, ValueFromPipeline = true)]
    [Alias("Id")]
    [ZabbixReference<ZabbixTemplateGroup>]
    [ZabbixCompleter<ZabbixTemplateGroup>]
    public ZabbixReference<ZabbixTemplateGroup>[]? InputObject { get; set; }

    /// <summary>Propagates the group's user permissions to its subgroups.</summary>
    [Parameter]
    public SwitchParameter Permissions { get; set; }

    /// <summary>
    /// Propagates without the confirmation prompt a lowered <c>$ConfirmPreference</c> would raise; <c>-WhatIf</c> and an
    /// explicit <c>-Confirm</c> still apply.
    /// </summary>
    [Parameter]
    public SwitchParameter Force { get; set; }

    private readonly List<ZabbixReference<ZabbixTemplateGroup>> _groups = [];

    /// <inheritdoc/>
    protected override void ProcessRecord() => _groups.AddRange(InputObject!);

    /// <inheritdoc/>
    protected override void EndProcessing()
    {
        if (!TryConfirm(_groups, "Propagate to the subgroups of template group", Force, out IReadOnlyList<ulong> ids)) return;
        var request = new ZabbixTemplateGroupPropagateRequest
        {
            Groups = [.. ids.Select(id => new ZabbixTemplateGroupPropagateGroupRequest { GroupId = id })],
            Permissions = Permissions.IsPresent,
        };
        TryRun(cancellationToken => ZabbixClient.TemplateGroups.PropagateAsync(request, cancellationToken), out _, ids);
    }
}
