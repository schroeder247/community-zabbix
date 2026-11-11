// This Source Code Form is subject to the terms of the Mozilla Public
// License, v. 2.0. Copyright (c) 2026 schroeder247. SPDX-License-Identifier: MPL-2.0
using System.Management.Automation;

namespace Community.Zabbix.PowerShell.Commands;

/// <summary>
/// <para type="synopsis">Stops monitoring hosts (<c>host.update status=1</c>).</para>
/// </summary>
[Cmdlet(VerbsLifecycle.Disable, "ZbxHost", SupportsShouldProcess = true)]
[OutputType(typeof(ZabbixHost))]
public sealed class DisableZbxHostCommand : ZabbixHostStatusCommand
{
    private protected override ZabbixHostStatus Status => ZabbixHostStatus.Disabled;

    private protected override string Action => "Disable host";
}
