// This Source Code Form is subject to the terms of the Mozilla Public
// License, v. 2.0. Copyright (c) 2026 schroeder247. SPDX-License-Identifier: MPL-2.0
using System.Management.Automation;
using Community.Zabbix.PowerShell.Sessions;

namespace Community.Zabbix.PowerShell.Commands;

/// <summary><para type="synopsis">Stops an active-agent session.</para></summary>
[Cmdlet(VerbsLifecycle.Stop, "ZbxActiveAgent", SupportsShouldProcess = true)]
public sealed class StopZbxActiveAgentCommand : ZabbixStopSessionCmdlet
{
    private protected override string SessionKind => "active agent";

    private protected override string NotFoundErrorId => "ZabbixAgentNotFound";

    private protected override IZabbixSession? Find(string name) => ZabbixAgentSession.Find(ZabbixContext, name, "active");
}
