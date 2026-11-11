# This Source Code Form is subject to the terms of the Mozilla Public
# License, v. 2.0. Copyright (c) 2026 schroeder247. SPDX-License-Identifier: MPL-2.0
Start-ZbxActiveAgent -Name 'web-agent' -Hostname 'web-01' -Server 'zabbix.example:10051'
Start-ZbxActiveAgent -Name 'db-agent' -Hostname 'db-01' -Server 'zabbix.example:10051'

<#
.SYNOPSIS
Gets the running active agent sessions.
.DESCRIPTION
The Get-ZbxActiveAgentStatus cmdlet writes the active agent sessions running in this runspace, or the one named in
-Name: each with its name, the host it reports as, when it started, and AgentStatus, the counts of values delivered and
rejected and the time of its last exchange with the server. It reads only the runspace's own sessions and sends nothing.
Start-ZbxActiveAgent starts a session and Stop-ZbxActiveAgent ends it.
#>
#region List the running active agents
# Every active agent session of this runspace.
Get-ZbxActiveAgentStatus | Select-Object Name, Kind, Started
#endregion

#region Check what an agent has delivered
# AgentStatus counts what the server accepted and refused.
(Get-ZbxActiveAgentStatus -Name 'web-agent').AgentStatus
#endregion

Stop-ZbxActiveAgent -Name 'web-agent'
Stop-ZbxActiveAgent -Name 'db-agent'
