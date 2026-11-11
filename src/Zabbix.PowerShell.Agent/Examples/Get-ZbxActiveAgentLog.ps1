# This Source Code Form is subject to the terms of the Mozilla Public
# License, v. 2.0. Copyright (c) 2026 schroeder247. SPDX-License-Identifier: MPL-2.0
Start-ZbxActiveAgent -Name 'web-agent' -Hostname 'web-01' -Server 'zabbix.example:10051'
Start-Sleep -Seconds 3

<#
.SYNOPSIS
Gets the recent log entries of a running active agent session.
.DESCRIPTION
The Get-ZbxActiveAgentLog cmdlet writes the log entries the active agent session named in -Name keeps in its own
bounded buffer: what it asked the server, what it sent and what went wrong, with a timestamp, a level and a category.
The session runs in the background, so its log is the place to see why values do not arrive. -Tail keeps the last
entries and -Level the entries at or above a level; Clear-ZbxActiveAgentLog empties the buffer.
#>
#region Read the last log entries of an agent
# Each entry has a Timestamp, a Level, a Category and the Message.
Get-ZbxActiveAgentLog -Name 'web-agent' -Tail 10 | Select-Object Timestamp, Level, Message
#endregion

#region Show only warnings and errors
# -Level keeps the entries at or above the level given.
Get-ZbxActiveAgentLog -Name 'web-agent' -Level Warning
#endregion

Stop-ZbxActiveAgent -Name 'web-agent'
