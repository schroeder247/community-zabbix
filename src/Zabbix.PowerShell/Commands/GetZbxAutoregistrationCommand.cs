// This Source Code Form is subject to the terms of the Mozilla Public
// License, v. 2.0. Copyright (c) 2026 schroeder247. SPDX-License-Identifier: MPL-2.0
using System.Management.Automation;

namespace Community.Zabbix.PowerShell.Commands;

/// <summary>
/// <para type="synopsis">Gets the server's autoregistration settings (<c>autoregistration.get</c>).</para>
/// </summary>
[Cmdlet(VerbsCommon.Get, "ZbxAutoregistration")]
[OutputType(typeof(ZabbixAutoregistrationSettings))]
public sealed class GetZbxAutoregistrationCommand : ZabbixSingletonGetCmdlet<ZabbixAutoregistrationSettings>
{
    /// <inheritdoc/>
    protected override Task<ZabbixAutoregistrationSettings> GetAsync(CancellationToken cancellationToken) => ZabbixClient.Autoregistration.GetAsync(cancellationToken);
}
