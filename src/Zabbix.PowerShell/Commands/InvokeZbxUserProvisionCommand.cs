// This Source Code Form is subject to the terms of the Mozilla Public
// License, v. 2.0. Copyright (c) 2026 schroeder247. SPDX-License-Identifier: MPL-2.0
using System.Management.Automation;

namespace Community.Zabbix.PowerShell.Commands;

/// <summary>
/// <para type="synopsis">Provisions users from their directory's mapped groups and media (<c>user.provision</c>).</para>
/// </summary>
[Cmdlet(VerbsLifecycle.Invoke, "ZbxUserProvision", SupportsShouldProcess = true)]
public sealed class InvokeZbxUserProvisionCommand : ZabbixServerCmdlet
{
    /// <summary>The users: ids, names or objects.</summary>
    [Parameter(Mandatory = true, Position = 0, ValueFromPipeline = true)]
    [Alias("Id")]
    [ZabbixReference<ZabbixUser>]
    [ZabbixCompleter<ZabbixUser>]
    public ZabbixReference<ZabbixUser>[]? InputObject { get; set; }

    private readonly List<ZabbixReference<ZabbixUser>> _users = [];

    /// <inheritdoc/>
    protected override void ProcessRecord() => _users.AddRange(InputObject!);

    /// <inheritdoc/>
    protected override void EndProcessing()
    {
        if (!TryConfirm(_users, "Provision user", force: false, out IReadOnlyList<ulong> ids)) return;
        TryRun(cancellationToken => ZabbixClient.Users.ProvisionAsync(ids, cancellationToken), out _, ids);
    }
}
