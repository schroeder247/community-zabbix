// This Source Code Form is subject to the terms of the Mozilla Public
// License, v. 2.0. Copyright (c) 2026 schroeder247. SPDX-License-Identifier: MPL-2.0
using System.Management.Automation;
using Community.Zabbix.PowerShell.Sessions;

namespace Community.Zabbix.PowerShell.Commands;

/// <summary><para type="synopsis">Empties a running active-proxy session's ring buffer of log lines.</para></summary>
[Cmdlet(VerbsCommon.Clear, "ZbxActiveProxyLog", SupportsShouldProcess = true)]
public sealed class ClearZbxActiveProxyLogCommand : ZabbixClearSessionLogCmdlet
{
    private protected override string SessionKind => "active proxy";

    private protected override string NotFoundErrorId => "ZabbixActiveProxyNotFound";

    private protected override IZabbixSession? Find(string name) => ZabbixContext.Sessions.TryGet(name, out ZabbixActiveProxySession? session) ? session : null;
}
