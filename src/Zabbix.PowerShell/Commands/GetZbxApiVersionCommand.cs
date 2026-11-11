// This Source Code Form is subject to the terms of the Mozilla Public
// License, v. 2.0. Copyright (c) 2026 schroeder247. SPDX-License-Identifier: MPL-2.0
using System.Management.Automation;

namespace Community.Zabbix.PowerShell.Commands;

/// <summary>
/// <para type="synopsis">Gets the Zabbix API version of a server (<c>apiinfo.version</c>, no authentication).</para>
/// </summary>
[Cmdlet(VerbsCommon.Get, "ZbxApiVersion")]
[OutputType(typeof(Version))]
public sealed class GetZbxApiVersionCommand : ZabbixServerCmdlet
{
    /// <inheritdoc/>
    protected override void ProcessRecord()
    {
        if (TryRun(ZabbixClient.GetApiVersionAsync, out Version? version, Connected.Name)) WriteObject(version);
    }
}
