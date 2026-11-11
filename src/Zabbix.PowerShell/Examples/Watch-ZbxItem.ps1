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

<#
.SYNOPSIS
Watches items and collects every value they report, in the background.
.DESCRIPTION
The Watch-ZbxItem cmdlet starts a watch: a session of this runspace that reads, every -Interval, the values the items in
its scope collected since the last read, and keeps the newest value of each. The scope is one item (-HostName and -Key),
a host's items (-HostName), a host group's items (-Group), or every host. Receive-ZbxWatch reads the values as they
arrive, Show-ZbxWatch draws the current ones as a table, and Stop-ZbxWatch ends the watch; it writes the watch only
with -PassThru.
#>
#region Watch one item and read its values as they arrive
# -Interval takes Zabbix's time suffixes ('2s', '1m'), seconds or a time span; the watch reads every two seconds and
# Receive-ZbxWatch writes each new value until Select-Object -First ends it.
Watch-ZbxItem -Name 'items' -HostName 'web-01' -Key 'zabbix[host,,items]' -Interval '2s'
Receive-ZbxWatch -Name 'items' | Select-Object -First 1 | Select-Object HostName, Key, Value
Stop-ZbxWatch -Name 'items'
#endregion

#region Watch every item of a host group
# -Group watches each item of each host in the group; -PassThru writes the watch.
Watch-ZbxItem -Name 'linux' -Group 'Linux servers' -Interval '30s' -PassThru | Select-Object Name, Started
Stop-ZbxWatch -Name 'linux'
#endregion
