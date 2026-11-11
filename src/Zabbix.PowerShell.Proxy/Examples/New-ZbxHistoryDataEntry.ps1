# This Source Code Form is subject to the terms of the Mozilla Public
# License, v. 2.0. Copyright (c) 2026 schroeder247. SPDX-License-Identifier: MPL-2.0
#region Build one history value for an item
# One entry of a proxy's 'history data' array: the item's id, the value as text, and now as its clock (Unix seconds,
# with a stamped sub-second -Ns so two values in one second never collide). Id is a sequence number, auto-assigned.
$item = Get-ZbxItem -Host 'web-01' | Where-Object Key -eq 'app.requests'
New-ZbxHistoryDataEntry -ItemId $item.ItemId -Value 42
#endregion

#region Build a series with past timestamps
# -Clock stamps each value (Unix seconds); -Ns and -Id may be given too when replaying stored data.
$item = Get-ZbxItem -Host 'web-01' | Where-Object Key -eq 'app.requests'
$start = [DateTimeOffset]::UtcNow.AddMinutes(-5).ToUnixTimeSeconds()
0..4 | ForEach-Object { New-ZbxHistoryDataEntry -ItemId $item.ItemId -Value (100 + $_) -Clock ($start + 60 * $_) } |
    Select-Object ItemId, Clock, Value
#endregion

#region Take the item id from the pipeline
# -ItemId (and -Value, -Clock, -Ns, -Id) bind by property name, so an item from Get-ZbxItem supplies its own ItemId.
Get-ZbxItem -Host 'web-01' | Where-Object Key -eq 'app.status' | New-ZbxHistoryDataEntry -Value 'degraded' |
    Select-Object ItemId, Value
#endregion

#region Put the entries into a proxy data request
# The entries are what Send-ZbxProxyData -History pushes, and what New-ZbxProxyData -History wraps for inspection.
$item = Get-ZbxItem -Host 'web-01' | Where-Object Key -eq 'app.requests'
$history = 1..3 | ForEach-Object { New-ZbxHistoryDataEntry -ItemId $item.ItemId -Value $_ }
(New-ZbxProxyData -ProxyName 'proxy-01' -Session ([guid]::NewGuid().ToString('N')) -History $history).History.Count
#endregion
