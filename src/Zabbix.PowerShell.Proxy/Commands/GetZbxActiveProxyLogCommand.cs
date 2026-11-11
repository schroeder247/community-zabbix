// This Source Code Form is subject to the terms of the Mozilla Public
// License, v. 2.0. Copyright (c) 2026 schroeder247. SPDX-License-Identifier: MPL-2.0
using System.Management.Automation;
using Community.Zabbix.PowerShell.Diagnostics;
using Community.Zabbix.PowerShell.Sessions;

namespace Community.Zabbix.PowerShell.Commands;

/// <summary><para type="synopsis">Gets the recent log lines of a running active-proxy session's own ring buffer (its loop runs off any command's pipeline).</para></summary>
[Cmdlet(VerbsCommon.Get, "ZbxActiveProxyLog")]
[OutputType(typeof(ZabbixSessionLogEntry))]
public sealed class GetZbxActiveProxyLogCommand : ZabbixGetSessionLogCmdlet
{
    private protected override string SessionKind => "active proxy";

    private protected override string NotFoundErrorId => "ZabbixActiveProxyNotFound";

    private protected override IZabbixSession? Find(string name) => ZabbixContext.Sessions.TryGet(name, out ZabbixActiveProxySession? session) ? session : null;
}
