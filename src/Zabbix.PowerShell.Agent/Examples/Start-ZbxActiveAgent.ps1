# This Source Code Form is subject to the terms of the Mozilla Public
# License, v. 2.0. Copyright (c) 2026 schroeder247. SPDX-License-Identifier: MPL-2.0
# Active items for the agent on web-01, then wait until the server's configuration cache has loaded them (up to
# CacheUpdateFrequency, 60 s by default): a trapper item created after them receives a probe value once it has (a cache
# that still holds a deleted host of the same name accepts values too, so check that the value reached this item).
$web = Get-ZbxHost -Name 'web-01'
$null = New-ZbxItem -HostId $web.HostId -Name 'Agent ping' -Key 'agent.ping' -Type ZabbixActive -ValueType Unsigned -Delay '5s'
$null = New-ZbxItem -HostId $web.HostId -Name 'Agent version' -Key 'agent.version' -Type ZabbixActive -ValueType Character -Delay '5s'
$probe = New-ZbxItem -HostId $web.HostId -Name 'Cache probe' -Key 'app.probe' -Type Trapper -ValueType Unsigned -TrapperHosts '0.0.0.0/0,::/0'
$deadline = [DateTime]::UtcNow.AddSeconds(120)
do {
    if ([DateTime]::UtcNow -gt $deadline) { throw 'the server did not load web-01 in time' }
    $token = Get-Random -Minimum 1 -Maximum 1000000
    $null = Send-ZbxData -HostName 'web-01' -Key 'app.probe' -Value $token -Server 'zabbix.example:10051' -WarningAction SilentlyContinue
    Start-Sleep -Seconds 1
} until ((Get-ZbxItem -Id $probe.ItemId).LastValue -eq "$token")

#region Start an active agent for a host
# The agent asks the server's trapper which active items (type ZabbixActive) host web-01 has, answers the built-in
# keys (agent.ping, agent.version, agent.hostname, system.uname, and Zabbix's own keys such as vfs.fs.size and
# proc.num) and pushes the values, in the background of this runspace, until Stop-ZbxActiveAgent or the runspace
# closes. -Name is only the session's local name; -KeyRule limits the keys it answers, as on Start-ZbxPassiveAgent.
Start-ZbxActiveAgent -Name 'web-agent' -Hostname 'web-01' -Server 'zabbix.example:10051'
Get-ZbxActiveAgentStatus -Name 'web-agent' | Select-Object Name, Kind, Started
Stop-ZbxActiveAgent -Name 'web-agent'
#endregion

#region Watch the values arrive on the server
# The agent re-asks for its items every -RefreshInterval (5 seconds by default) and sends each value when the item's
# own interval is due; the server then shows it as the item's LastValue.
Start-ZbxActiveAgent -Name 'web-agent' -Hostname 'web-01' -Server 'zabbix.example:10051'
Start-Sleep -Seconds 10
Get-ZbxItem -Host 'web-01' | Where-Object Type -eq 'ZabbixActive' | Select-Object Key, LastValue
Stop-ZbxActiveAgent -Name 'web-agent'
#endregion

#region Start an agent with host metadata and its own cadence
# -HostMetadata is what auto-registration actions filter on; -RefreshInterval and -HeartbeatInterval take Zabbix's
# time suffixes ('30s', '2m'), seconds or a time span.
# -PassThru writes the session. -Server is Zabbix's ServerActive text: 'a;b' is one HA cluster, tried in turn; 'a,b'
# is two independent destinations, each sent every value. Quote it: PowerShell ends a command at a bare ';'.
Start-ZbxActiveAgent -Name 'web-agent' -Hostname 'web-01' -Server 'zabbix.example:10051' -HostMetadata 'Linux web' `
    -RefreshInterval '30s' -HeartbeatInterval '2m' -PassThru |
    Select-Object Name, Kind, Started
Stop-ZbxActiveAgent -Name 'web-agent'
#endregion

#region Run an agent for the length of a script
# A script that pushes agent data while it does its own work, and always stops the agent, even on failure.
Start-ZbxActiveAgent -Name 'web-agent' -Hostname 'web-01' -Server 'zabbix.example:10051'
try {
    Start-Sleep -Seconds 6
    $status = (Get-ZbxActiveAgentStatus -Name 'web-agent').AgentStatus
    'Delivered {0} value(s), {1} rejected' -f $status.Delivered, $status.Rejected
}
finally {
    Stop-ZbxActiveAgent -Name 'web-agent'
}
#endregion
