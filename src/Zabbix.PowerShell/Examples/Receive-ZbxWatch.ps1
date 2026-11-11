# This Source Code Form is subject to the terms of the Mozilla Public
# License, v. 2.0. Copyright (c) 2026 schroeder247. SPDX-License-Identifier: MPL-2.0
# Wait until the server's configuration cache has loaded this web-01 (up to CacheUpdateFrequency, 60 s by default): a
# trapper item created with the others receives a probe value once it has.
$web = Get-ZbxHost -Name 'web-01'
# An internal item the server itself computes every two seconds: a steady stream of values to watch.
$null = New-ZbxItem -HostId $web.HostId -Name 'Item count' -Key 'zabbix[host,,items]' -Type Internal -ValueType Unsigned -Delay '2s'
$probe = New-ZbxItem -HostId $web.HostId -Name 'Cache probe' -Key 'app.probe' -Type Trapper -ValueType Unsigned -TrapperHosts '0.0.0.0/0,::/0'
$deadline = [DateTime]::UtcNow.AddSeconds(120)
do {
    if ([DateTime]::UtcNow -gt $deadline) { throw 'the server did not load web-01 in time' }
    $token = Get-Random -Minimum 1 -Maximum 1000000
    $null = Send-ZbxData -HostName 'web-01' -Key 'app.probe' -Value $token -Server 'zabbix.example:10051' -WarningAction SilentlyContinue
    Start-Sleep -Seconds 1
} until ((Get-ZbxItem -Id $probe.ItemId).LastValue -eq "$token")
Watch-ZbxItem -Name 'items' -HostName 'web-01' -Key 'zabbix[host,,items]' -Interval '2s'

<#
.SYNOPSIS
Reads the values a watch collects, as they arrive.
.DESCRIPTION
The Receive-ZbxWatch cmdlet writes each value the watch named in -Name collects, as a ZabbixValue with its host, key,
value and clock, and keeps waiting for the next until the watch stops or the pipeline ends (Ctrl+C, or Select-Object
-First). Each call is its own reader with a bounded buffer, so a slow reader loses the oldest values, which Get-ZbxWatch
counts in Dropped. It sends nothing to the server itself; the watch does the reading.
#>
#region Read the next value of a watched item
# The reader sees the values that arrive after it starts; Select-Object -First ends it after one.
Receive-ZbxWatch -Name 'items' | Select-Object -First 1 | Select-Object HostName, Key, Value
#endregion

#region React to values as they arrive
# ForEach-Object handles each value as it comes: here it reports the time of the next three.
Receive-ZbxWatch -Name 'items' | Select-Object -First 3 |
    ForEach-Object { '{0} {1} = {2} at {3}' -f $_.HostName, $_.Key, $_.Value, $_.Time }
#endregion

Stop-ZbxWatch -Name 'items'
