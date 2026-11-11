# This Source Code Form is subject to the terms of the Mozilla Public
# License, v. 2.0. Copyright (c) 2026 schroeder247. SPDX-License-Identifier: MPL-2.0
# Setup: a map with two image elements, created with map.create directly.
$icon = @(Invoke-ZbxApi -Method image.get -Body @{ output = @('imageid'); filter = @{ name = 'Server_(96)' } })[0].imageid
$null = Invoke-ZbxApi -Method map.create -Body @{ name = 'Office map'; width = 600; height = 300; selements = @(
    @{ selementid = '1'; elementtype = 4; iconid_off = $icon; x = 50; y = 100; label = 'Router' }
    @{ selementid = '2'; elementtype = 4; iconid_off = $icon; x = 400; y = 100; label = 'Switch' }) }

#region Build an element record
# The builder writes a hashtable in the wire shape of one map element (selements); only the fields bound are set.
New-ZbxMapElement -MapElementId 1 -ElementType Image -ZIndex 2
#endregion

#region Bring a map's elements to the front
# An element with an existing -MapElementId updates that element in place, keeping its label and position;
# Set-ZbxMap -Element sends them all. -ZIndex (and -ShowLabel) need Zabbix 7.4 or later.
$elements = (Get-ZbxMap -Name 'Office map' -SelectElements).Elements |
    ForEach-Object { New-ZbxMapElement -MapElementId $_.MapElementId -ElementType $_.ElementType -ZIndex 10 }
$null = Set-ZbxMap -InputObject 'Office map' -Element $elements
(Get-ZbxMap -Name 'Office map' -SelectElements).Elements | Format-Table MapElementId, Label, X, Y, ZIndex
#endregion
