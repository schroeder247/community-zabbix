# This Source Code Form is subject to the terms of the Mozilla Public
# License, v. 2.0. Copyright (c) 2026 schroeder247. SPDX-License-Identifier: MPL-2.0
# Vocabulary: Data center map

#region Build a title shape and use it in a new map
# The builder writes one shape hashtable (type, position, size, text, colours); New-ZbxMap sends it in -Shape.
# Colours are hex RGB without '#'.
$title = New-ZbxMapShape -Type Rectangle -X 10 -Y 10 -Width 580 -Height 30 -Text 'Office network' -FontSize 14 -BackgroundColor 'DDEEFF' -BorderType Solid -BorderWidth 1 -BorderColor '336699'
New-ZbxMap -Name 'Office map' -Width 600 -Height 400 -Shape $title | Select-Object MapId, Name
#endregion

#region Group elements visually with ellipses
# -Shape takes an array; shapes are drawn under the map's elements and -ZIndex orders overlapping ones.
# -TextVAlign places the label inside the shape. -SelectShapes reads them back.
$shapes = @(
    New-ZbxMapShape -Type Ellipse -X 20 -Y 60 -Width 260 -Height 300 -Text 'Web tier' -TextVAlign Top -BorderType Dashed -BorderColor '009900' -ZIndex 0
    New-ZbxMapShape -Type Ellipse -X 320 -Y 60 -Width 260 -Height 300 -Text 'Data tier' -TextVAlign Top -BorderType Dashed -BorderColor '990000' -ZIndex 1
)
$null = New-ZbxMap -Name 'Data center map' -Width 600 -Height 400 -Shape $shapes
(Get-ZbxMap -Name 'Data center map' -SelectShapes).Shapes | Format-Table Type, X, Y, Width, Height, Text
#endregion
