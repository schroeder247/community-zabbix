// This Source Code Form is subject to the terms of the Mozilla Public
// License, v. 2.0. Copyright (c) 2026 schroeder247. SPDX-License-Identifier: MPL-2.0
using System.Management.Automation;
using Community.Zabbix.PowerShell.Diagnostics;
using Community.Zabbix.PowerShell.Sessions;

namespace Community.Zabbix.PowerShell.Commands;

/// <summary><para type="synopsis">Gets the recent log lines of a running passive-agent session's own ring buffer (its loop runs off any command's pipeline).</para></summary>
[Cmdlet(VerbsCommon.Get, "ZbxPassiveAgentLog")]
[OutputType(typeof(ZabbixSessionLogEntry))]
public sealed class GetZbxPassiveAgentLogCommand : ZabbixGetSessionLogCmdlet
{
    private protected override string SessionKind => "passive agent";

    private protected override string NotFoundErrorId => "ZabbixAgentNotFound";

    private protected override IZabbixSession? Find(string name) => ZabbixAgentSession.Find(ZabbixContext, name, "passive");
}
