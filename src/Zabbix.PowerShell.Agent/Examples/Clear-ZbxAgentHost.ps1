# This Source Code Form is subject to the terms of the Mozilla Public
# License, v. 2.0. Copyright (c) 2026 schroeder247. SPDX-License-Identifier: MPL-2.0
Start-ZbxActiveAgent -Name 'web-agent' -Hostname 'web-02' -Server 'zabbix.example:10051'
Register-ZbxActiveAgentHandler -Name 'web-agent' -Key 'app.health' -ScriptBlock { 'ok' }
$null = Initialize-ZbxAgentHost -Session 'web-agent' -HostGroup 'Web servers'

<#
.SYNOPSIS
Deletes what an agent session provisioned on the server.
.DESCRIPTION
The Clear-ZbxAgentHost cmdlet deletes the items Initialize-ZbxAgentHost and Publish-ZbxAgentHost created for the agent
session named in -Session, and the host and its interface when Initialize-ZbxAgentHost created them; a host that existed
before is never deleted, only the items the session added to it. It asks for confirmation first unless -Confirm:$false
or -Force is given, and -WhatIf shows what it would delete. It writes nothing.
#>
#region Remove what an agent session provisioned
# Initialize-ZbxAgentHost created web-02, so the host goes with its items; a search for it then finds nothing.
Clear-ZbxAgentHost -Session 'web-agent' -Confirm:$false
@(Get-ZbxHost -Name 'web-02*').Count
#endregion

Stop-ZbxActiveAgent -Name 'web-agent'
