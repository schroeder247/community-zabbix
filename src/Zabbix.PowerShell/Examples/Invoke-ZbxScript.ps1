# This Source Code Form is subject to the terms of the Mozilla Public
# License, v. 2.0. Copyright (c) 2026 schroeder247. SPDX-License-Identifier: MPL-2.0
# script.execute runs through the server, which knows a host only once its configuration cache has loaded it (up to
# CacheUpdateFrequency, 60 s by default): a trapper item created now receives a probe value once it has.
$web = Get-ZbxHost -Name 'web-01'
$probe = New-ZbxItem -HostId $web.HostId -Name 'Cache probe' -Key 'app.probe' -Type Trapper -ValueType Unsigned -TrapperHosts '0.0.0.0/0,::/0'
$deadline = [DateTime]::UtcNow.AddSeconds(120)
do {
    if ([DateTime]::UtcNow -gt $deadline) { throw 'the server did not load web-01 in time' }
    $token = Get-Random -Minimum 1 -Maximum 1000000
    $null = Send-ZbxData -HostName 'web-01' -Key 'app.probe' -Value $token -Server 'zabbix.example:10051' -WarningAction SilentlyContinue
    Start-Sleep -Seconds 1
} until ((Get-ZbxItem -Id $probe.ItemId).LastValue -eq "$token")
$null = New-ZbxScript -Name 'Ping' -Scope ManualHost -Type Webhook -Command 'return "pong";'

<#
.SYNOPSIS
Runs a script on a host or for an event.
.DESCRIPTION
The Invoke-ZbxScript cmdlet runs the script in -Script against the host in -Host (a name, an id or a piped host),
or for the event in -EventId, with script.execute, as the host and event menus of the frontend do. The script runs where
it is configured to: on the agent, the server or the proxy, or as a webhook on the server. The command writes the
script's response and its output; -ManualInput answers a script that asks for input, and -WhatIf shows what it would
run.
#>
#region Run a script against a host
# -Script (position 0) takes the script's name, id or object, and -HostReference (position 1) the host.
Invoke-ZbxScript -Script 'Ping' -Host 'web-01'
#endregion

#region Run a script against the hosts a search finds
# A host piped from Get-ZbxHost binds -HostReference by its HostId; the script runs once per host.
Get-ZbxHost -Name 'web-*' | Invoke-ZbxScript -Script 'Ping' | Select-Object Response, Value
#endregion
