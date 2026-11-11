# This Source Code Form is subject to the terms of the Mozilla Public
# License, v. 2.0. Copyright (c) 2026 schroeder247. SPDX-License-Identifier: MPL-2.0
Start-ZbxActiveAgent -Name 'web-agent' -Hostname 'web-01' -Server 'zabbix.example:10051'
Start-Sleep -Seconds 3

<#
.SYNOPSIS
Empties the log buffer of a running active agent session.
.DESCRIPTION
The Clear-ZbxActiveAgentLog cmdlet deletes every entry the active agent session named in -Name keeps in its log buffer,
so the next Get-ZbxActiveAgentLog shows only what happens from now on. The session keeps running and logging. -WhatIf
shows which session it would clear. It writes nothing.
#>
#region Start the log afresh before a test
# After the clear, the log holds only what the agent does next.
Clear-ZbxActiveAgentLog -Name 'web-agent'
Start-Sleep -Seconds 3
Get-ZbxActiveAgentLog -Name 'web-agent' | Select-Object Timestamp, Message
#endregion

Stop-ZbxActiveAgent -Name 'web-agent'
