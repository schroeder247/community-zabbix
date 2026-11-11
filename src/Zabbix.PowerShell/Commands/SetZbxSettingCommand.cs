// This Source Code Form is subject to the terms of the Mozilla Public
// License, v. 2.0. Copyright (c) 2026 schroeder247. SPDX-License-Identifier: MPL-2.0
using System.Management.Automation;

namespace Community.Zabbix.PowerShell.Commands;

/// <summary>
/// <para type="synopsis">Updates the frontend's global settings (<c>settings.update</c>).</para>
/// <para type="description">
/// <c>-Request</c> is a <c>ZabbixSettingsUpdateRequest</c> carrying only the fields to change; cast it from a
/// hashtable: <c>[Community.Zabbix.ZabbixSettingsUpdateRequest]@{ WorkPeriod = '1-5,09:00-18:00' }</c>. Every field
/// <c>settings.update</c> takes is a property of that type. Fields you leave out keep their current value. Writes
/// the settings as read back after the update. Supports <c>-WhatIf</c>/<c>-Confirm</c> (ConfirmImpact Medium).
/// </para>
/// </summary>
/// <remarks>
/// Unlike <c>Set-ZbxHousekeeping</c>/<c>-Authentication</c>/<c>-Autoregistration</c> (a handful of fields each, one
/// named <c>[Parameter]</c> per field), <c>settings.update</c>'s request is too wide for one parameter per field to
/// stay readable, so the command takes the typed request whole while still deriving from the shared singleton base
/// (no <c>-Id</c>, one <c>ShouldProcess</c>).
/// </remarks>
[Cmdlet(VerbsCommon.Set, "ZbxSetting", SupportsShouldProcess = true, ConfirmImpact = ConfirmImpact.Medium)]
[OutputType(typeof(ZabbixSettingsRecord))]
public sealed class SetZbxSettingCommand : ZabbixSingletonSetCmdlet<ZabbixSettingsUpdateRequest>
{
    /// <summary>The fields to change.</summary>
    [Parameter(Mandatory = true, Position = 0, ValueFromPipeline = true)]
    [ValidateNotNull]
    public ZabbixSettingsUpdateRequest? Request { get; set; }

    /// <inheritdoc/>
    protected override string Subject => "the frontend's global settings";

    /// <inheritdoc/>
    protected override ZabbixSettingsUpdateRequest BuildRequest() => Request!;

    /// <inheritdoc/>
    protected override async Task<object?> UpdateAsync(ZabbixClient client, ZabbixSettingsUpdateRequest request, CancellationToken cancellationToken)
        => await client.Settings.UpdateAsync(request, cancellationToken).ConfigureAwait(false);

    /// <inheritdoc/>
    protected override async Task<object?> GetAsync(CancellationToken cancellationToken)
        => await ZabbixClient.Settings.GetAsync(cancellationToken).ConfigureAwait(false);
}
