// This Source Code Form is subject to the terms of the Mozilla Public
// License, v. 2.0. Copyright (c) 2026 schroeder247. SPDX-License-Identifier: MPL-2.0
using System.Management.Automation;

namespace Community.Zabbix.PowerShell.Commands;

/// <summary>
/// <para type="synopsis">Gets the server's housekeeping settings (<c>housekeeping.get</c>).</para>
/// </summary>
[Cmdlet(VerbsCommon.Get, "ZbxHousekeeping")]
[OutputType(typeof(ZabbixHousekeepingSettings))]
public sealed class GetZbxHousekeepingCommand : ZabbixSingletonGetCmdlet<ZabbixHousekeepingSettings>
{
    /// <inheritdoc/>
    protected override Task<ZabbixHousekeepingSettings> GetAsync(CancellationToken cancellationToken) => ZabbixClient.Housekeeping.GetAsync(cancellationToken);
}
