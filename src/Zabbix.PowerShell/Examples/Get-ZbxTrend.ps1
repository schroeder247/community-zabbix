# This Source Code Form is subject to the terms of the Mozilla Public
# License, v. 2.0. Copyright (c) 2026 schroeder247. SPDX-License-Identifier: MPL-2.0
<#
.SYNOPSIS
Gets the hourly trends of items.
.DESCRIPTION
The Get-ZbxTrend cmdlet reads trends with trend.get: for each hour, the minimum, average and maximum of an item's
numeric values and how many there were, which the server keeps after the raw history ages out. It selects by item
(-Item: names, ids or piped items) and by time (-From and -Till), at most -Limit records; trends have no -Filter.
It writes ZabbixTrendRecord objects and needs a connection made with Connect-ZbxServer.
#>
#region Get a day of an item's trends
# A piped item binds -Item by its ItemId; -From takes a date, Unix seconds or a clock. An hour's trend appears once the hour ends.
Get-ZbxItem -Host 'web-01' -Name 'Requests' | Get-ZbxTrend -From (Get-Date).AddDays(-1) |
    Select-Object @{ n = 'Hour'; e = { $_.Time } }, ValueMin, ValueAverage, ValueMax
#endregion

#region Get the last week's peak of every item of a host
# Group the hourly records by item and keep the highest maximum.
Get-ZbxItem -Host 'web-01' | Get-ZbxTrend -From (Get-Date).AddDays(-7) |
    Group-Object ItemId | ForEach-Object { [pscustomobject]@{ ItemId = $_.Name; Peak = ($_.Group.ValueMax | Measure-Object -Maximum).Maximum } }
#endregion
