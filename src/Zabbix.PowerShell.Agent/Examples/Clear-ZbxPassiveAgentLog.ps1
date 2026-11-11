# This Source Code Form is subject to the terms of the Mozilla Public
# License, v. 2.0. Copyright (c) 2026 schroeder247. SPDX-License-Identifier: MPL-2.0
Start-ZbxPassiveAgent -Name 'web-agent' -Hostname 'web-01' -Port 30150
$null = Get-ZbxAgentItem -Agent '127.0.0.1:30150' -Key 'agent.ping'

<#
.SYNOPSIS
Empties the log buffer of a running passive agent session.
.DESCRIPTION
The Clear-ZbxPassiveAgentLog cmdlet deletes every entry the passive agent session named in -Name keeps in its log
buffer, so the next Get-ZbxPassiveAgentLog shows only what happens from now on. The listener keeps running and logging.
-WhatIf shows which session it would clear. It writes nothing.
#>
#region Start the log afresh before a test
# After the clear, the log holds only the check made next.
Clear-ZbxPassiveAgentLog -Name 'web-agent'
Get-ZbxAgentItem -Agent '127.0.0.1:30150' -Key 'agent.version' | Out-Null
Get-ZbxPassiveAgentLog -Name 'web-agent' | Select-Object Timestamp, Message
#endregion

Stop-ZbxPassiveAgent -Name 'web-agent'
