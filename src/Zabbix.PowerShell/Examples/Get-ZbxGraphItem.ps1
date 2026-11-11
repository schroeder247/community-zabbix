# This Source Code Form is subject to the terms of the Mozilla Public
# License, v. 2.0. Copyright (c) 2026 schroeder247. SPDX-License-Identifier: MPL-2.0
$item = Get-ZbxItem -Host 'web-01' -Name 'Requests'
$graph = New-ZbxGraph -Name 'Request rate' -GraphItem @{ itemid = $item.ItemId; color = '1A7C11' }
# Vocabulary: Request rate

<#
.SYNOPSIS
Gets the items a graph plots, with how it draws each.
.DESCRIPTION
The Get-ZbxGraphItem cmdlet reads graph items with graphitem.get: for each item a graph plots, its color, draw style,
function and Y axis side. It selects by graph (-Id, or a graph piped from Get-ZbxGraph) or by item (-ItemId); graph
items have no name, so there is no -Name or -Filter. -SelectGraphs adds each graph item's graphs. It writes
ZabbixGraphItem objects and needs a connection made with Connect-ZbxServer.
#>
#region Get the items of a graph
# A graph piped from Get-ZbxGraph binds -Id by its GraphId.
Get-ZbxGraph -Host 'web-01' -Name 'Request rate' | Get-ZbxGraphItem | Select-Object ItemId, Color, DrawType
#endregion

#region Find the graphs that plot an item
# -ItemId selects by item instead; -SelectGraphs adds each graph item's graphs.
Get-ZbxGraphItem -ItemId $item.ItemId -SelectGraphs | ForEach-Object { $_.Graphs.Name }
#endregion
