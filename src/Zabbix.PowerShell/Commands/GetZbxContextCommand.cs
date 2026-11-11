// This Source Code Form is subject to the terms of the Mozilla Public
// License, v. 2.0. Copyright (c) 2026 schroeder247. SPDX-License-Identifier: MPL-2.0
using System.Management.Automation;

namespace Community.Zabbix.PowerShell.Commands;

/// <summary>
/// <para type="synopsis">Gets the session's context: its connections and the current one.</para>
/// <para type="description">Each runspace has its own context; a job or a parallel branch starts unconnected.</para>
/// </summary>
[Cmdlet(VerbsCommon.Get, "ZbxContext")]
[OutputType(typeof(ZabbixContext))]
public sealed class GetZbxContextCommand : ZabbixCmdlet
{
    /// <inheritdoc/>
    protected override void ProcessRecord() => WriteObject(ZabbixContext);
}
