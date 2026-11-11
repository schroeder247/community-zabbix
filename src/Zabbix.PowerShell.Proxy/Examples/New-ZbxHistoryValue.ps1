# This Source Code Form is subject to the terms of the Mozilla Public
# License, v. 2.0. Copyright (c) 2026 schroeder247. SPDX-License-Identifier: MPL-2.0
<#
.SYNOPSIS
Builds one value for the history data of a proxy data request.
.DESCRIPTION
The New-ZbxHistoryValue cmdlet writes one ZabbixProxyHistoryItem: the item's id, the value as text, its clock (now when
-Clock is not given) and a sequence id the server removes duplicates by. It is one entry of the history data a proxy
sends, for New-ZbxProxyData, Send-ZbxProxyData or a data provider's page. -ItemId, -Value and -Clock bind by property
name, so items piped from Get-ZbxItem give their own ids. Nothing is sent to the server.
#>
#region Build a value for an item
# The id is the item's; the clock is now.
$item = Get-ZbxItem -Host 'web-01' -Name 'Requests'
New-ZbxHistoryValue -ItemId $item.ItemId -Value '42'
#endregion

#region Build a minute of values with past clocks
# -Clock takes a date, Unix seconds or a clock; each value gets the next sequence id.
$item = Get-ZbxItem -Host 'web-01' -Name 'Requests'
$start = [DateTimeOffset]::UtcNow.AddMinutes(-1).ToUnixTimeSeconds()
0..5 | ForEach-Object { New-ZbxHistoryValue -ItemId $item.ItemId -Value (10 * $_) -Clock ($start + 10 * $_) } | Select-Object ItemId, Clock, Value
#endregion
