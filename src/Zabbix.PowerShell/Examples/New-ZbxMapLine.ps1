# This Source Code Form is subject to the terms of the Mozilla Public
# License, v. 2.0. Copyright (c) 2026 schroeder247. SPDX-License-Identifier: MPL-2.0
#region Build a line and use it in a new map
# The builder writes one line hashtable: the two end points (-X1/-Y1, -X2/-Y2), style, width and hex colour.
# New-ZbxMap sends it in -Line.
$divider = New-ZbxMapLine -X1 10 -Y1 50 -X2 590 -Y2 50 -LineType Solid -LineWidth 2 -LineColor '336699'
New-ZbxMap -Name 'Office map' -Width 600 -Height 400 -Line $divider | Select-Object MapId, Name
#endregion

#region Replace the lines of an existing map
# Set-ZbxMap -Line replaces all of the map's lines; -SelectLines reads them back (x1, y1, x2, y2, line_type ...).
$lines = @(
    New-ZbxMapLine -X1 300 -Y1 60 -X2 300 -Y2 390 -LineType Dashed -LineWidth 1 -LineColor '999999'
    New-ZbxMapLine -X1 10 -Y1 50 -X2 590 -Y2 50 -LineType Solid -LineWidth 2 -LineColor '336699'
)
$null = Set-ZbxMap -InputObject 'Office map' -Line $lines
(Get-ZbxMap -Name 'Office map' -SelectLines).Lines | Format-Table x1, y1, x2, y2, line_type, line_color
#endregion
