// This Source Code Form is subject to the terms of the Mozilla Public
// License, v. 2.0. Copyright (c) 2026 schroeder247. SPDX-License-Identifier: MPL-2.0
using System.Management.Automation;

namespace Community.Zabbix.PowerShell.Commands;

/// <summary>What <c>Disable-ZbxHost</c> and <c>Enable-ZbxHost</c> share: resolve, confirm each host, update the status in one call.</summary>
public abstract class ZabbixHostStatusCommand : ZabbixServerCmdlet
{
    /// <summary>The hosts: ids, names or objects.</summary>
    [Parameter(Mandatory = true, Position = 0, ValueFromPipeline = true)]
    [Alias("Id", "HostId")]
    [ZabbixReference<ZabbixHost>]
    [ZabbixCompleter<ZabbixHost>]
    public ZabbixReference<ZabbixHost>[]? InputObject { get; set; }

    /// <summary>Changes the hosts without asking, even when <c>$ConfirmPreference</c> would; <c>-WhatIf</c> and an explicit <c>-Confirm</c> still apply.</summary>
    [Parameter]
    public SwitchParameter Force { get; set; }

    /// <summary>Writes the changed hosts back (one <c>host.get</c> for the whole batch), instead of nothing.</summary>
    [Parameter]
    public SwitchParameter PassThru { get; set; }

    private readonly List<ZabbixReference<ZabbixHost>> _references = [];

    private protected abstract ZabbixHostStatus Status { get; }

    private protected abstract string Action { get; }

    /// <inheritdoc/>
    protected override void ProcessRecord() => _references.AddRange(InputObject!);

    /// <inheritdoc/>
    protected override void EndProcessing()
    {
        // Resolved first, then confirmed per host by name (one ShouldProcess per object, ConfirmImpact Medium, no
        // second ShouldContinue question), then one host.update for the confirmed ones.
        if (_references.Count == 0) return;
        if (!TryRun(cancellationToken => ResolveTargetsAsync(_references, cancellationToken), out IReadOnlyList<(ulong Id, string Target)>? targets, _references.ToArray())) return;
        IReadOnlyList<ulong> ids = Confirmed(targets, Action, Force);
        if (ids.Count == 0) return;
        if (!TryRun(cancellationToken => ZabbixClient.Hosts.UpdateAsync(
                ids.Select(id => new ZabbixHostUpdateRequest { HostId = id, Status = Status }), cancellationToken),
            out IReadOnlyList<ulong>? updated, ids)) return;
        if (!PassThru.IsPresent || updated.Count == 0) return;
        if (TryRun(cancellationToken => ZabbixClient.Hosts.GetAsync(new ZabbixHostGetParameters { HostIds = updated }, cancellationToken), out IReadOnlyList<ZabbixHost>? hosts, updated))
        {
            foreach (ZabbixHost host in hosts) WriteObject(host);
        }
    }
}
