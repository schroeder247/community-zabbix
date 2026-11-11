# This Source Code Form is subject to the terms of the Mozilla Public
# License, v. 2.0. Copyright (c) 2026 schroeder247. SPDX-License-Identifier: MPL-2.0
# Vocabulary: Request rate
$item = Get-ZbxItem -Host 'web-01' | Where-Object Key -EQ 'app.requests'
$null = New-ZbxGraph -Name 'Request rate' -Width 900 -Height 200 -AdditionalProperty @{ gitems = @(@{ itemid = [string]$item.ItemId; color = '00AA00' }) }

#region Get the graphs of a host
# -Host takes a host's name, id or object. The result is a ZabbixGraph record.
Get-ZbxGraph -Host 'web-01' | Select-Object GraphId, Name, Width, Height, GraphType
#endregion

#region Show which items a graph draws
# -SelectGraphItems adds the graph items (item id, colour, draw type); -SelectItems would add the items themselves.
(Get-ZbxGraph -Name 'Request rate' -SelectGraphItems).GraphItems | Select-Object ItemId, Color, DrawType
#endregion

#region Count the graphs a template provides
# -CountOutput asks the server for the number only (graph.get's countOutput).
Get-ZbxGraph -Template 'Linux by Zabbix agent' -CountOutput
#endregion
