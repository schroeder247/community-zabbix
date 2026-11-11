# This Source Code Form is subject to the terms of the Mozilla Public
# License, v. 2.0. Copyright (c) 2026 schroeder247. SPDX-License-Identifier: MPL-2.0
<#
.SYNOPSIS
Builds a handler for an item key, to register on agent sessions.
.DESCRIPTION
The New-ZbxAgentHandler cmdlet writes a binding of an item key and the scriptblock that answers it, without
registering it anywhere. Piped into Register-ZbxAgentHandler, Register-ZbxActiveAgentHandler or
Register-ZbxPassiveAgentHandler, the binding is added to that session, so a catalogue of handlers can be built once
and registered on several sessions. Nothing is sent anywhere.
#>
#region Build a handler binding
# A key and the scriptblock that answers it, as one object; nothing is registered yet.
New-ZbxAgentHandler -Key 'app.health' -ScriptBlock { 'ok' }
#endregion

#region Register one catalogue on several agents
# Built once, piped into Register-ZbxPassiveAgentHandler (or Register-ZbxActiveAgentHandler/Register-ZbxAgentHandler)
# for each session, which binds each object to -Binding.
$catalogue = @(
    New-ZbxAgentHandler -Key 'app.health' -ScriptBlock { 'ok' }
    New-ZbxAgentHandler -Key 'app.version' -ScriptBlock { '2.4.1' }
)
Start-ZbxPassiveAgent -Name 'web-agent' -Hostname 'web-01' -Port 30150
Start-ZbxPassiveAgent -Name 'db-agent' -Hostname 'db-01' -Port 30151
$catalogue | Register-ZbxPassiveAgentHandler -Name 'web-agent'
$catalogue | Register-ZbxPassiveAgentHandler -Name 'db-agent'
Get-ZbxAgentItem -Agent '127.0.0.1:30151' -Key 'app.health', 'app.version' | Select-Object Key, Value
Stop-ZbxPassiveAgent -Name 'web-agent'
Stop-ZbxPassiveAgent -Name 'db-agent'
#endregion

#region Build handlers from data
# A realistic script: one binding per service, keyed svc.<name>, ready to pipe into a Register-Zbx*AgentHandler.
$services = 'nginx', 'redis'
$services | ForEach-Object { New-ZbxAgentHandler -Key "svc.$_" -ScriptBlock { 'running' } } | Select-Object Key
#endregion
