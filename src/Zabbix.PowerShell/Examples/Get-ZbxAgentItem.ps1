# This Source Code Form is subject to the terms of the Mozilla Public
# License, v. 2.0. Copyright (c) 2026 schroeder247. SPDX-License-Identifier: MPL-2.0
<#
.SYNOPSIS
Asks a Zabbix agent for item values, as zabbix_get does.
.DESCRIPTION
The Get-ZbxAgentItem cmdlet connects to the passive agent in -Agent and asks it for each key in -Key, over the same
protocol zabbix_get uses, and writes one answer per key with its value, or the error the agent gave for a key it does
not support. It talks to the agent directly, so it needs no Zabbix server and no connection. Use it to check what an
agent answers before an item is created for it.
#>
# An agent to query: a passive agent session on a spare port (a real Zabbix agent listens on 10050).
Start-ZbxPassiveAgent -Name 'web-agent' -Hostname 'web-01' -Port 30153
Register-ZbxPassiveAgentHandler -Name 'web-agent' -Key 'app.health' -ScriptBlock { 'ok' }

#region Ask an agent for one value
# The zabbix_get protocol, straight to the agent: no server and no Connect-ZbxServer needed. -Agent is host or
# host:port; the port is 10050 when the address names none.
Get-ZbxAgentItem -Agent '127.0.0.1:30153' -Key 'agent.ping'
#endregion

#region Ask for several keys at once
# One answer per key, in order. A key the agent does not know is not an error: IsSupported is False and Error says why.
Get-ZbxAgentItem -Agent '127.0.0.1:30153' -Key 'agent.hostname', 'app.health', 'no.such.key' |
    Select-Object Key, Value, IsSupported, Error
#endregion

#region Check that an agent answers before adding it to Zabbix
# A script's pre-flight check: -Timeout bounds the connect and the read ('3s', seconds or a time span; 10 seconds by
# default).
$answer = Get-ZbxAgentItem -Agent '127.0.0.1:30153' -Key 'agent.ping' -Timeout '3s'
if ($answer.Value -eq '1') { 'The agent answers' } else { 'No answer: {0}' -f $answer.Error }
#endregion

Stop-ZbxPassiveAgent -Name 'web-agent'
