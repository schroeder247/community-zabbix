# This Source Code Form is subject to the terms of the Mozilla Public
# License, v. 2.0. Copyright (c) 2026 schroeder247. SPDX-License-Identifier: MPL-2.0
<#
.SYNOPSIS
Writes values with past clocks into items through the API.
.DESCRIPTION
The Send-ZbxHistory cmdlet writes values into trapper items (and HTTP agent items that allow traps) with history.push,
each with its own -Clock, so a script can backfill data collected while the server could not be reached. Objects with
HostName (or Host), Key, Value and Clock bind by property name, and every piped value goes in one call when the
pipeline ends. Unlike Send-ZbxData, it goes through the API with the connection's login, not through the trapper. It
writes the API's answer, and -WhatIf shows what would be sent.
#>
# Wait until the server's configuration cache has loaded this web-01 (up to CacheUpdateFrequency, 60 s by default); a
# cache that still holds a deleted web-01 of the same name accepts values for it too, so check the value reached this probe item.
# Zabbix 8.0 limits new trapper items to {$TRAPPER.ALLOWED_HOSTS} (loopback); these accept every sender.
Get-ZbxItem -Host 'web-01' | Where-Object Type -eq 'Trapper' | Set-ZbxItem -TrapperHosts '0.0.0.0/0,::/0' | Out-Null
$web = Get-ZbxHost -Name 'web-01'
$probe = New-ZbxItem -HostId $web.HostId -Name 'Cache probe' -Key 'app.probe' -Type Trapper -ValueType Unsigned -TrapperHosts '0.0.0.0/0,::/0'
$deadline = [DateTime]::UtcNow.AddSeconds(120)
do {
    if ([DateTime]::UtcNow -gt $deadline) { throw 'the server did not load web-01 in time' }
    $token = Get-Random -Minimum 1 -Maximum 1000000
    $null = Send-ZbxData -HostName 'web-01' -Key 'app.probe' -Value $token -Server 'zabbix.example:10051' -WarningAction SilentlyContinue
    Start-Sleep -Seconds 1
} until ((Get-ZbxItem -Id $probe.ItemId).LastValue -eq "$token")

#region Backfill one value at a past time
# history.push through the API (not the trapper port): the item must be a trapper item (or an HTTP agent item that
# allows traps). -Clock is required, in Unix seconds; the result is the API's raw answer.
Send-ZbxHistory -HostName 'web-01' -Key 'app.requests' -Value 17 -Clock ([DateTimeOffset]::UtcNow.AddHours(-1).ToUnixTimeSeconds())
#endregion

#region Backfill a series through the pipeline
# Objects with HostName (or Host), Key, Value and Clock properties bind by name; every piped value goes in one
# history.push call when the pipeline ends. Get-ZbxHistory reads the series back once the server has written it.
$start = [DateTimeOffset]::UtcNow.AddMinutes(-30).ToUnixTimeSeconds()
0..2 | ForEach-Object { [pscustomobject]@{ Host = 'web-01'; Key = 'app.requests'; Value = 5 * $_; Clock = $start + 600 * $_ } } |
    Send-ZbxHistory | Out-Null
Start-Sleep -Seconds 8
Get-ZbxItem -Host 'web-01' | Where-Object Key -eq 'app.requests' | Get-ZbxHistory -SortField clock | Select-Object Clock, Value
#endregion

#region Preview a backfill with -WhatIf
# ConfirmImpact is Low: -WhatIf says how many values it would push, and pushes nothing.
Send-ZbxHistory -HostName 'web-01' -Key 'app.requests' -Value 99 -Clock ([DateTimeOffset]::UtcNow.AddDays(-1).ToUnixTimeSeconds()) -WhatIf
#endregion
