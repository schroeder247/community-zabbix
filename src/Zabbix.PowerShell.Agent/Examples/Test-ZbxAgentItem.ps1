# This Source Code Form is subject to the terms of the Mozilla Public
# License, v. 2.0. Copyright (c) 2026 schroeder247. SPDX-License-Identifier: MPL-2.0
<#
.SYNOPSIS
Answers an item key the way an agent session would, without a server.
#>
#region Answer a built-in key locally
# No session, no network: the built-in keys answer agent.ping, agent.version, agent.hostname and system.uname, and the
# bundled items answer Zabbix's own keys, measured on this computer.
Test-ZbxAgentItem -Key 'agent.version'
Test-ZbxAgentItem -Key 'vfs.fs.size[/,pfree]' -Raw
#endregion

#region Get the bare value with -Raw
# -Raw writes the answer's value (or its error text) instead of the whole answer; -Hostname sets what
# agent.hostname answers (the computer name by default).
Test-ZbxAgentItem -Key 'agent.hostname' -Hostname 'web-01' -Raw
#endregion

#region Test a running session's own handlers
# -Session dispatches through that session's full handler set, its scriptblocks and plugins included, exactly as a
# server's check would. A key nothing answers comes back with Error set rather than as a terminating error.
Start-ZbxPassiveAgent -Name 'web-agent' -Hostname 'web-01' -Port 30150
Register-ZbxPassiveAgentHandler -Name 'web-agent' -Key 'app.health' -ScriptBlock { 'ok' }
Test-ZbxAgentItem -Key 'app.health' -Session 'web-agent' | Select-Object Key, Value, IsSupported, Error
Test-ZbxAgentItem -Key 'no.such.key' -Session 'web-agent' | Select-Object Key, Value, IsSupported, Error
Stop-ZbxPassiveAgent -Name 'web-agent'
#endregion

#region Build a key of your own from bundled ones
# A scriptblock handler reads bundled keys with Test-ZbxAgentItem -Raw: here app.disk.alarm answers 1 when the root file
# system is more than 90 percent full.
Start-ZbxPassiveAgent -Name 'web-agent' -Hostname 'web-01' -Port 30150
Register-ZbxPassiveAgentHandler -Name 'web-agent' -Key 'app.disk.alarm' -ScriptBlock {
    if ([double](Test-ZbxAgentItem -Key 'vfs.fs.size[/,pused]' -Raw) -gt 90) { 1 } else { 0 }
}
Test-ZbxAgentItem -Key 'app.disk.alarm' -Session 'web-agent' -Raw
Stop-ZbxPassiveAgent -Name 'web-agent'
#endregion

#region Test several keys through the pipeline
# -Key binds from the pipeline, one answer per key.
'agent.ping', 'agent.version', 'system.uname' | Test-ZbxAgentItem | Select-Object Key, Value
#endregion
