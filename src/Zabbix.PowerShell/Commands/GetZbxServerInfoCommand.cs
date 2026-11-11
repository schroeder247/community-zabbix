// This Source Code Form is subject to the terms of the Mozilla Public
// License, v. 2.0. Copyright (c) 2026 schroeder247. SPDX-License-Identifier: MPL-2.0
using System.Management.Automation;

namespace Community.Zabbix.PowerShell.Commands;

/// <summary>
/// <para type="synopsis">Gets a connected server's information: its endpoint, the API version, and its trapper.</para>
/// </summary>
[Cmdlet(VerbsCommon.Get, "ZbxServerInfo")]
[OutputType("Community.Zabbix.PowerShell.ServerInfo")]
public sealed class GetZbxServerInfoCommand : ZabbixServerCmdlet
{
    /// <inheritdoc/>
    protected override void ProcessRecord()
    {
        var result = new PSObject();
        result.Properties.Add(new PSNoteProperty("Name", Connected.Name));
        result.Properties.Add(new PSNoteProperty("Endpoint", Connected.Endpoint));
        result.Properties.Add(new PSNoteProperty("ApiVersion", Connected.ApiVersion));
        result.Properties.Add(new PSNoteProperty("Trapper", Connected.Trapper));
        result.TypeNames.Insert(0, "Community.Zabbix.PowerShell.ServerInfo");
        WriteObject(result);
    }
}
