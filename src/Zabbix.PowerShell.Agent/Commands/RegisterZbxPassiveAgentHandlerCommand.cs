// This Source Code Form is subject to the terms of the Mozilla Public
// License, v. 2.0. Copyright (c) 2026 schroeder247. SPDX-License-Identifier: MPL-2.0
using System.Management.Automation;

namespace Community.Zabbix.PowerShell.Commands;

/// <summary><para type="synopsis">Registers a scriptblock handler on a running passive agent session.</para></summary>
[Cmdlet(VerbsLifecycle.Register, "ZbxPassiveAgentHandler", DefaultParameterSetName = ParameterSets.Direct)]
[OutputType(typeof(ZabbixAgentSession))]
public sealed class RegisterZbxPassiveAgentHandlerCommand : ZabbixRegisterAgentHandlerCommand
{
    private protected override string Kind => "passive";
}
