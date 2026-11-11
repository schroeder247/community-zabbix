// This Source Code Form is subject to the terms of the Mozilla Public
// License, v. 2.0. Copyright (c) 2026 schroeder247. SPDX-License-Identifier: MPL-2.0
using System.Management.Automation;

namespace Community.Zabbix.PowerShell.Commands;

/// <summary>
/// <para type="synopsis">Gets the frontend's global settings (<c>settings.get</c>).</para>
/// </summary>
[Cmdlet(VerbsCommon.Get, "ZbxSetting")]
[OutputType(typeof(ZabbixSettingsRecord))]
public sealed class GetZbxSettingCommand : ZabbixSingletonGetCmdlet<ZabbixSettingsRecord>
{
    /// <inheritdoc/>
    protected override Task<ZabbixSettingsRecord> GetAsync(CancellationToken cancellationToken) => ZabbixClient.Settings.GetAsync(cancellationToken: cancellationToken);
}
