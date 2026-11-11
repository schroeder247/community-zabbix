# This Source Code Form is subject to the terms of the Mozilla Public
# License, v. 2.0. Copyright (c) 2026 schroeder247. SPDX-License-Identifier: MPL-2.0
# Push a few values. They arrive once the server's configuration cache knows the new items (up to a minute).
$now = [DateTimeOffset]::UtcNow.ToUnixTimeSeconds()
$sw = [Diagnostics.Stopwatch]::StartNew()
do {
    Start-Sleep -Seconds 2
    $null = Send-ZbxHistory 'web-01' 'app.requests' 40 -Clock ($now - 120) -Confirm:$false
} until (@(Get-ZbxHistory -Host 'web-01').Count -ge 1 -or $sw.Elapsed.TotalSeconds -gt 240)
$null = Send-ZbxHistory 'web-01' 'app.requests' 55 -Clock ($now - 60) -Confirm:$false
$null = Send-ZbxHistory 'web-01' 'app.requests' 61 -Clock $now -Confirm:$false
$null = Send-ZbxData 'web-01' 'app.status' 'deploying' -Clock ($now - 60) -Server 'zabbix.example:10051' -Confirm:$false
$null = Send-ZbxData 'web-01' 'app.status' 'ok' -Clock $now -Server 'zabbix.example:10051' -Confirm:$false
$text = [Community.Zabbix.ZabbixHistoryGetParameters]@{ History = 'Text'; ItemIds = @((Get-ZbxItem -Host 'web-01' -Name 'Status').ItemId) }
while ((@(Get-ZbxHistory -Host 'web-01').Count -lt 3 -or @(Get-ZbxHistory -Filter $text).Count -lt 2) -and $sw.Elapsed.TotalSeconds -lt 240) {
    Start-Sleep -Seconds 1
}

#region Get an item's history through the pipeline
# A piped item binds its ItemId and its ValueType, which names the history table to read, so any item's values come
# back, numeric or text; Clock is the time of each value.
Get-ZbxItem -Host 'web-01' -Name 'Requests' | Get-ZbxHistory |
    Select-Object Time, Value
#endregion

#region Get the newest values of a host's numeric items
# -Host reads the history of the host's items of one value type (-ValueType, Unsigned when omitted);
# -SortField/-SortOrder/-Limit keep the newest.
Get-ZbxHistory -Host 'web-01' -SortField clock -SortOrder Descending -Limit 2 | Select-Object ItemId, Clock, Value
#endregion

#region Read text values in a time window
# -Filter takes history.get's own parameters: History picks the value type (Float, Character, Log, Unsigned,
# Text...), TimeFrom/TimeTo bound the window (Unix time). -ValueType, -From and -Till say the same without -Filter.
$item = Get-ZbxItem -Host 'web-01' -Name 'Status'
$filter = [Community.Zabbix.ZabbixHistoryGetParameters]@{
    History  = 'Text'
    ItemIds  = @($item.ItemId)
    TimeFrom = [int][DateTimeOffset]::UtcNow.AddHours(-1).ToUnixTimeSeconds()
}
Get-ZbxHistory -Filter $filter | Select-Object Clock, Value
#endregion
