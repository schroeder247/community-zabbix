// This Source Code Form is subject to the terms of the Mozilla Public
// License, v. 2.0. Copyright (c) 2026 schroeder247. SPDX-License-Identifier: MPL-2.0
using System.Management.Automation;

namespace Community.Zabbix.PowerShell.Commands;

/// <summary>
/// <para type="synopsis">Resets users' TOTP secret, forcing MFA re-enrollment (<c>user.resettotp</c>).</para>
/// </summary>
[Cmdlet(VerbsCommon.Reset, "ZbxUserTotp", SupportsShouldProcess = true, ConfirmImpact = ConfirmImpact.Medium)]
[OutputType(typeof(ZabbixUser))]
public sealed class ResetZbxUserTotpCommand : ZabbixServerCmdlet
{
    /// <summary>The users: ids, names or objects.</summary>
    [Parameter(Mandatory = true, Position = 0, ValueFromPipeline = true)]
    [Alias("Id")]
    [ZabbixReference<ZabbixUser>]
    [ZabbixCompleter<ZabbixUser>]
    public ZabbixReference<ZabbixUser>[]? InputObject { get; set; }

    /// <summary>Writes the users, read back after the change; without it the command writes nothing.</summary>
    [Parameter]
    public SwitchParameter PassThru { get; set; }

    private readonly List<ZabbixReference<ZabbixUser>> _users = [];

    /// <inheritdoc/>
    protected override void ProcessRecord() => _users.AddRange(InputObject!);

    /// <inheritdoc/>
    protected override void EndProcessing()
    {
        if (!TryConfirm(_users, "Reset TOTP of user", force: false, out IReadOnlyList<ulong> ids)) return;
        if (!TryRun(async cancellationToken =>
        {
            IReadOnlyList<ulong> changed = await ZabbixClient.Users.ResetTotpAsync(ids, cancellationToken).ConfigureAwait(false);
            return PassThru ? await ZabbixEntities.Of<ZabbixUser>().SelectByIdAsync(ZabbixClient, changed, cancellationToken).ConfigureAwait(false) : [];
        }, out IReadOnlyList<ZabbixUser>? users, ids)) return;
        foreach (ZabbixUser user in users) WriteObject(user);
    }
}
