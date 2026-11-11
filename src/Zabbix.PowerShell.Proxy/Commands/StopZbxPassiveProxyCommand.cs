// This Source Code Form is subject to the terms of the Mozilla Public
// License, v. 2.0. Copyright (c) 2026 schroeder247. SPDX-License-Identifier: MPL-2.0
using System.Management.Automation;
using Community.Zabbix.PowerShell.Sessions;

namespace Community.Zabbix.PowerShell.Commands;

/// <summary><para type="synopsis">Stops a passive-proxy session.</para></summary>
[Cmdlet(VerbsLifecycle.Stop, "ZbxPassiveProxy", SupportsShouldProcess = true)]
public sealed class StopZbxPassiveProxyCommand : ZabbixStopSessionCmdlet
{
    private protected override string SessionKind => "passive proxy";

    private protected override string NotFoundErrorId => "ZabbixPassiveProxyNotFound";

    private protected override IZabbixSession? Find(string name) => ZabbixContext.Sessions.TryGet(name, out ZabbixPassiveProxySession? session) ? session : null;
}
