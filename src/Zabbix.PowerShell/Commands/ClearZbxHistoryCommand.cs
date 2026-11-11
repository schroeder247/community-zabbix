// This Source Code Form is subject to the terms of the Mozilla Public
// License, v. 2.0. Copyright (c) 2026 schroeder247. SPDX-License-Identifier: MPL-2.0
using System.Management.Automation;

namespace Community.Zabbix.PowerShell.Commands;

/// <summary>
/// <para type="synopsis">Deletes items' history and trends (<c>history.clear</c>) — the heaviest delete the server runs.</para>
/// </summary>
[Cmdlet(VerbsCommon.Clear, "ZbxHistory", SupportsShouldProcess = true, ConfirmImpact = ConfirmImpact.High)]
public sealed class ClearZbxHistoryCommand : ZabbixServerCmdlet
{
    /// <summary>The items: ids, names or objects.</summary>
    [Parameter(Mandatory = true, Position = 0, ValueFromPipeline = true)]
    [Alias("Id", "ItemId")]
    [ZabbixReference<ZabbixItem>]
    [ZabbixCompleter<ZabbixItem>]
    public ZabbixReference<ZabbixItem>[]? InputObject { get; set; }

    /// <summary>Clears without asking (both questions); <c>-WhatIf</c> and an explicit <c>-Confirm</c> still apply.</summary>
    [Parameter]
    public SwitchParameter Force { get; set; }

    private readonly List<ZabbixReference<ZabbixItem>> _items = [];
    private bool _yesToAll;
    private bool _noToAll;

    /// <inheritdoc/>
    protected override void ProcessRecord() => _items.AddRange(InputObject!);

    /// <inheritdoc/>
    protected override void EndProcessing()
    {
        // Resolved first, then confirmed per item by name: ShouldProcess (ConfirmImpact High) and, as the heaviest
        // delete the server runs, the second ShouldContinue question (Yes to All answers the rest), unless -Force.
        if (_items.Count == 0) return;
        if (!TryRun(cancellationToken => ResolveTargetsAsync(_items, cancellationToken), out IReadOnlyList<(ulong Id, string Target)>? targets, _items.ToArray())) return;
        List<ulong> ids = [];
        foreach ((ulong id, string target) in targets.DistinctBy(target => target.Id))
            if (ShouldChange(target, "Clear history", Force, ref _yesToAll, ref _noToAll)) ids.Add(id);
        if (ids.Count == 0) return;
        TryRun(cancellationToken => ZabbixClient.History.ClearAsync(ids, cancellationToken), out _, ids);
    }
}
