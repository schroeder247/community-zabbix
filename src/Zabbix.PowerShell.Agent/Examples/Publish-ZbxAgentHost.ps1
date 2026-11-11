# This Source Code Form is subject to the terms of the Mozilla Public
# License, v. 2.0. Copyright (c) 2026 schroeder247. SPDX-License-Identifier: MPL-2.0
Start-ZbxActiveAgent -Name 'web-agent' -Hostname 'web-02' -Server 'zabbix.example:10051'
Register-ZbxActiveAgentHandler -Name 'web-agent' -Key 'app.health' -ScriptBlock { 'ok' }
$null = Initialize-ZbxAgentHost -Session 'web-agent' -HostGroup 'Web servers'

<#
.SYNOPSIS
Adds an item to the session's host for every key the session now answers.
.DESCRIPTION
The Publish-ZbxAgentHost cmdlet compares the keys the agent session named in -Session answers, its built-in keys and
registered handlers, with the items of the host Initialize-ZbxAgentHost provisioned, and creates an item for each key
that has none. With -RemoveStale it also deletes the items it created for keys the session no longer answers; it never
touches an item something else created. It writes the host's status only with -PassThru, and -WhatIf shows what would
change.
#>
#region Publish a handler registered after the host was provisioned
# app.version gets its item; -ValueType sets the type of the items created for handler keys (Text when omitted,
# which stores any answer; the built-in keys always get their own).
Register-ZbxActiveAgentHandler -Name 'web-agent' -Key 'app.version' -ScriptBlock { '2.4.1' }
Publish-ZbxAgentHost -Session 'web-agent' -ValueType Character -PassThru | ForEach-Object Items | Select-Object Key, HasLocalHandler
#endregion

#region Preview removing the items of keys the session dropped
# -WhatIf shows what -RemoveStale would delete, and deletes nothing.
Publish-ZbxAgentHost -Session 'web-agent' -RemoveStale -WhatIf
#endregion

Stop-ZbxActiveAgent -Name 'web-agent'
