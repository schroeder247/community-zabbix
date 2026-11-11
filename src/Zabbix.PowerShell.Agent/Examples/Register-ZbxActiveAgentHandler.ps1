# This Source Code Form is subject to the terms of the Mozilla Public
# License, v. 2.0. Copyright (c) 2026 schroeder247. SPDX-License-Identifier: MPL-2.0
<#
.SYNOPSIS
Registers a scriptblock that answers an item key on a running active agent session.
.DESCRIPTION
The Register-ZbxActiveAgentHandler cmdlet adds a handler to the active agent session named in -Name: a scriptblock
that answers -Key, run on the session's own runspace with the parsed key as its argument; its last output, as text, is
the value the agent pushes for every active item with that key. A binding from New-ZbxAgentHandler can be piped in
instead. It refuses a passive session; Register-ZbxAgentHandler takes either kind. It writes the session only with
-PassThru.
#>
# Active items for the agent on web-01, then wait until the server's configuration cache has loaded them (up to
# CacheUpdateFrequency, 60 s by default): a trapper item created after them receives a probe value once it has (a cache
# that still holds a deleted host of the same name accepts values too, so check that the value reached this item).
$web = Get-ZbxHost -Name 'web-01'
$null = New-ZbxItem -HostId $web.HostId -Name 'App health' -Key 'app.health' -Type ZabbixActive -ValueType Character -Delay '5s'
$null = New-ZbxItem -HostId $web.HostId -Name 'Orders queue' -Key 'app.queue[orders]' -Type ZabbixActive -ValueType Unsigned -Delay '5s'
$probe = New-ZbxItem -HostId $web.HostId -Name 'Cache probe' -Key 'app.probe' -Type Trapper -ValueType Unsigned -TrapperHosts '0.0.0.0/0,::/0'
$deadline = [DateTime]::UtcNow.AddSeconds(120)
do {
    if ([DateTime]::UtcNow -gt $deadline) { throw 'the server did not load web-01 in time' }
    $token = Get-Random -Minimum 1 -Maximum 1000000
    $null = Send-ZbxData -HostName 'web-01' -Key 'app.probe' -Value $token -Server 'zabbix.example:10051' -WarningAction SilentlyContinue
    Start-Sleep -Seconds 1
} until ((Get-ZbxItem -Id $probe.ItemId).LastValue -eq "$token")

#region Answer a custom active item
# The scriptblock runs on the session's own runspace (module commands and your variables are not there); its last
# output, as text, is the value the agent pushes for app.health. Test-ZbxAgentItem -Session answers it locally.
Start-ZbxActiveAgent -Name 'web-agent' -Hostname 'web-01' -Server 'zabbix.example:10051'
Register-ZbxActiveAgentHandler -Name 'web-agent' -Key 'app.health' -ScriptBlock { 'ok' }
Test-ZbxAgentItem -Key 'app.health' -Session 'web-agent' | Select-Object Key, Value, IsSupported
Stop-ZbxActiveAgent -Name 'web-agent'
#endregion

#region Push a handler's value to the server
# Register right after starting: a check that runs before the handler exists is sent as not supported, and the
# server shows the item unsupported until the next good value. The item's LastValue is what the server stored.
Start-ZbxActiveAgent -Name 'web-agent' -Hostname 'web-01' -Server 'zabbix.example:10051'
Register-ZbxActiveAgentHandler -Name 'web-agent' -Key 'app.queue' -ScriptBlock {
    param($key)
    @{ orders = 12; mail = 0 }[$key.Parameters[0]]
}
Start-Sleep -Seconds 12
Get-ZbxItem -Host 'web-01' | Where-Object Key -eq 'app.queue[orders]' | Select-Object Key, LastValue
Stop-ZbxActiveAgent -Name 'web-agent'
#endregion

#region Register a catalogue of handlers through the pipeline
# New-ZbxAgentHandler bindings bind to -Binding from the pipeline, so one catalogue serves several sessions.
Start-ZbxActiveAgent -Name 'web-agent' -Hostname 'web-01' -Server 'zabbix.example:10051'
@(
    New-ZbxAgentHandler -Key 'app.health' -ScriptBlock { 'ok' }
    New-ZbxAgentHandler -Key 'app.version' -ScriptBlock { '2.4.1' }
) | Register-ZbxActiveAgentHandler -Name 'web-agent'
'app.health', 'app.version' | Test-ZbxAgentItem -Session 'web-agent' | Select-Object Key, Value
Stop-ZbxActiveAgent -Name 'web-agent'
#endregion
