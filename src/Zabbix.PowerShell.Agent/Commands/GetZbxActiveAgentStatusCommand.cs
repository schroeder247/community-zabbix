// This Source Code Form is subject to the terms of the Mozilla Public
// License, v. 2.0. Copyright (c) 2026 schroeder247. SPDX-License-Identifier: MPL-2.0
using System.Management.Automation;

namespace Community.Zabbix.PowerShell.Commands;

/// <summary><para type="synopsis">Gets the running active-agent sessions of this runspace, or one by name.</para></summary>
[Cmdlet(VerbsCommon.Get, "ZbxActiveAgentStatus")]
[OutputType(typeof(ZabbixAgentSession))]
public sealed class GetZbxActiveAgentStatusCommand : ZabbixAgentKindCommand
{
    private protected override string Kind => "active";

    /// <summary>The session's name; every running active-agent session when omitted.</summary>
    [Parameter(Position = 0)]
    public string? Name { get; set; }

    /// <inheritdoc/>
    protected override void ProcessRecord() => GetByName(Name);
}
