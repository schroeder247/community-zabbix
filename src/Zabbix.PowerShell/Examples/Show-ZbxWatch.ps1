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
Watch-ZbxItem -Name 'web' -HostName 'web-01' -Interval '2s'
Start-Sleep -Seconds 6

<#
.SYNOPSIS
Draws a watch's current values as a table in the console.
.DESCRIPTION
The Show-ZbxWatch cmdlet draws the newest value of each item the watch named in -Name collects, as a console table, one
row per item. Without -Live it draws the table once and returns; with -Live it redraws every -Interval until
Ctrl+C. It reads only what the watch holds and sends nothing to the server. Watch-ZbxItem starts the watch.
#>
#region Show a watch's values once
# The table shows the newest value of each item of web-01 the watch has collected so far. Add -Live, in an interactive
# console, to redraw it every -Interval until Ctrl+C.
Show-ZbxWatch -Name 'web'
#endregion

Stop-ZbxWatch -Name 'web'
