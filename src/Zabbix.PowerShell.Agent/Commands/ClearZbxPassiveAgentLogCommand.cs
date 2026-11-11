// This Source Code Form is subject to the terms of the Mozilla Public
// License, v. 2.0. Copyright (c) 2026 schroeder247. SPDX-License-Identifier: MPL-2.0
using System.Management.Automation;
using Community.Zabbix.PowerShell.Sessions;

namespace Community.Zabbix.PowerShell.Commands;

/// <summary><para type="synopsis">Empties a running passive-agent session's ring buffer of log lines.</para></summary>
[Cmdlet(VerbsCommon.Clear, "ZbxPassiveAgentLog", SupportsShouldProcess = true)]
public sealed class ClearZbxPassiveAgentLogCommand : ZabbixClearSessionLogCmdlet
{
    private protected override string SessionKind => "passive agent";

    private protected override string NotFoundErrorId => "ZabbixAgentNotFound";

    private protected override IZabbixSession? Find(string name) => ZabbixAgentSession.Find(ZabbixContext, name, "passive");
}
