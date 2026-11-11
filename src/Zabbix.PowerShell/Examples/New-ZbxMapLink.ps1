# This Source Code Form is subject to the terms of the Mozilla Public
# License, v. 2.0. Copyright (c) 2026 schroeder247. SPDX-License-Identifier: MPL-2.0
# Setup: a map with two image elements, created with map.create directly.
$icon = @(Invoke-ZbxApi -Method image.get -Body @{ output = @('imageid'); filter = @{ name = 'Server_(96)' } })[0].imageid
$null = Invoke-ZbxApi -Method map.create -Body @{ name = 'Office map'; width = 600; height = 300; selements = @(
    @{ selementid = '1'; elementtype = 4; iconid_off = $icon; x = 50; y = 100; label = 'Router' }
    @{ selementid = '2'; elementtype = 4; iconid_off = $icon; x = 400; y = 100; label = 'Switch' }) }

#region Link two elements of a map
# The builder writes one link hashtable between two element ids (-Element1Id/-Element2Id, read with
# Get-ZbxMap -SelectElements); Set-ZbxMap -Link replaces the map's links with it.
$router, $switch = (Get-ZbxMap -Name 'Office map' -SelectElements).Elements
$uplink = New-ZbxMapLink -Element1Id $router.MapElementId -Element2Id $switch.MapElementId -Label 'uplink' -Color '00AA00'
$null = Set-ZbxMap -InputObject 'Office map' -Link $uplink
(Get-ZbxMap -Name 'Office map' -SelectLinks).Links | Format-Table LinkId, Color, DrawType
#endregion

#region Draw a link as a dashed line
# -DrawType picks the line style (Line, BoldLine, Dot, DashedLine ...); -Link replaces the previous link.
$router, $switch = (Get-ZbxMap -Name 'Office map' -SelectElements).Elements
$uplink = New-ZbxMapLink -Element1Id $router.MapElementId -Element2Id $switch.MapElementId -Label 'backup' -Color 'CC0000' -DrawType DashedLine
$null = Set-ZbxMap -InputObject 'Office map' -Link $uplink
(Get-ZbxMap -Name 'Office map' -SelectLinks).Links | Format-Table LinkId, Color, DrawType
#endregion
