# This Source Code Form is subject to the terms of the Mozilla Public
# License, v. 2.0. Copyright (c) 2026 schroeder247. SPDX-License-Identifier: MPL-2.0
Start-ZbxPassiveAgent -Name 'web-agent' -Hostname 'web-01' -Port 30150
$null = Get-ZbxAgentItem -Agent '127.0.0.1:30150' -Key 'agent.ping'

<#
.SYNOPSIS
Gets the recent log entries of a running passive agent session.
.DESCRIPTION
The Get-ZbxPassiveAgentLog cmdlet writes the log entries the passive agent session named in -Name keeps in its own
bounded buffer: the connections it accepted or refused, the keys it answered and what went wrong, with a timestamp, a
level and a category. The listener runs in the background, so its log is the place to see why a check fails. -Tail
keeps the last entries and -Level the entries at or above a level.
#>
#region Read the last log entries of an agent
# Each entry has a Timestamp, a Level, a Category and the Message.
Get-ZbxPassiveAgentLog -Name 'web-agent' -Tail 10 | Select-Object Timestamp, Level, Message
#endregion

#region Find refused connections
# A peer that -AllowedPeer does not list is refused, and logged as a warning.
Get-ZbxPassiveAgentLog -Name 'web-agent' -Level Warning
#endregion

Stop-ZbxPassiveAgent -Name 'web-agent'
