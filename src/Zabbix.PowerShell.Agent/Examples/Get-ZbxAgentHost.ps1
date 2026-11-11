# This Source Code Form is subject to the terms of the Mozilla Public
# License, v. 2.0. Copyright (c) 2026 schroeder247. SPDX-License-Identifier: MPL-2.0
Start-ZbxActiveAgent -Name 'web-agent' -Hostname 'web-02' -Server 'zabbix.example:10051'
Register-ZbxActiveAgentHandler -Name 'web-agent' -Key 'app.health' -ScriptBlock { 'ok' }
$null = Initialize-ZbxAgentHost -Session 'web-agent' -HostGroup 'Web servers'

<#
.SYNOPSIS
Gets the host an agent session created on the server, and which of its items the session answers.
.DESCRIPTION
The Get-ZbxAgentHost cmdlet reads, from the server, the host that Initialize-ZbxAgentHost created or reused for the
agent session named in -Session, and writes it with one entry per item: the item's key, its id, and whether a handler of
the session still answers it. An item without a local handler is one the session would report as not supported. It
needs a connection made with Connect-ZbxServer.
#>
#region Check which items the agent answers
# HasLocalHandler is false for an item no built-in key or registered handler answers.
(Get-ZbxAgentHost -Session 'web-agent').Items | Select-Object Key, HasLocalHandler
#endregion

#region Read the host as the server holds it
# Host is the full ZabbixHost record.
(Get-ZbxAgentHost -Session 'web-agent').Host | Select-Object HostName, HostId, Status
#endregion

Stop-ZbxActiveAgent -Name 'web-agent'
