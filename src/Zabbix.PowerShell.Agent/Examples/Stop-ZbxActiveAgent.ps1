# This Source Code Form is subject to the terms of the Mozilla Public
# License, v. 2.0. Copyright (c) 2026 schroeder247. SPDX-License-Identifier: MPL-2.0
<#
.SYNOPSIS
Stops an active agent session.
.DESCRIPTION
The Stop-ZbxActiveAgent cmdlet stops the active agent session named in -Name, or each session piped from
Get-ZbxActiveAgentStatus: the agent stops asking the server for its items and pushing values, and is removed from the
runspace. A session also stops when the runspace closes. -WhatIf shows which session it would stop, and it writes
nothing.
#>
#region Stop an active agent by name
# Stops the session's send loop and disposes it; values it had not delivered yet are dropped with it.
Start-ZbxActiveAgent -Name 'web-agent' -Hostname 'web-01' -Server 'zabbix.example:10051'
Stop-ZbxActiveAgent -Name 'web-agent'
@(Get-ZbxActiveAgentStatus).Count
#endregion

#region Stop every active agent of this runspace
# Get-ZbxActiveAgentStatus writes each running session; Stop-ZbxActiveAgent binds its Name from the pipeline.
Start-ZbxActiveAgent -Name 'web-agent' -Hostname 'web-01' -Server 'zabbix.example:10051'
Start-ZbxActiveAgent -Name 'db-agent' -Hostname 'db-01' -Server 'zabbix.example:10051'
Get-ZbxActiveAgentStatus | Stop-ZbxActiveAgent
@(Get-ZbxActiveAgentStatus).Count
#endregion

#region Preview a stop with -WhatIf
# -WhatIf names the session it would stop and leaves it running. ConfirmImpact is Medium: -Confirm asks first.
Start-ZbxActiveAgent -Name 'web-agent' -Hostname 'web-01' -Server 'zabbix.example:10051'
Stop-ZbxActiveAgent -Name 'web-agent' -WhatIf
Stop-ZbxActiveAgent -Name 'web-agent'
#endregion
