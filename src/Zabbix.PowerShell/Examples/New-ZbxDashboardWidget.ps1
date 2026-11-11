# This Source Code Form is subject to the terms of the Mozilla Public
# License, v. 2.0. Copyright (c) 2026 schroeder247. SPDX-License-Identifier: MPL-2.0
#region Build a widget and put it on a new dashboard
# The builder writes one widget hashtable (type, name, position, size); it reaches the server inside a page
# (New-ZbxDashboardPage) of New-ZbxDashboard. 'clock' is the widget type name the frontend uses.
$clock = New-ZbxDashboardWidget -Type 'clock' -Name 'Server time' -X 0 -Y 0 -Width 12 -Height 3
New-ZbxDashboard -Name 'Ops overview' -Page (New-ZbxDashboardPage -Name 'Overview' -Widget $clock) |
    Select-Object DashboardId, Name
#endregion

#region Configure a widget with fields
# -Field carries the widget's settings as (type, name, value) triples in the API's widget-field format: type 2 is a
# host group id and type 4 an item id; array fields are numbered (groupids.0, itemid.0).
$group = Get-ZbxHostGroup -Name 'Web servers'
$item = Get-ZbxItem -Host 'web-01' | Where-Object Key -EQ 'app.requests'
$widgets = @(
    New-ZbxDashboardWidget -Type 'problems' -Name 'Web problems' -X 0 -Y 0 -Width 36 -Height 5 -Field @{ type = 2; name = 'groupids.0'; value = $group.GroupId }
    New-ZbxDashboardWidget -Type 'item' -Name 'Requests' -X 36 -Y 0 -Width 18 -Height 5 -ViewMode HiddenHeader -Field @{ type = 4; name = 'itemid.0'; value = $item.ItemId }
)
New-ZbxDashboard -Name 'Service status' -Page (New-ZbxDashboardPage -Widget $widgets) | Select-Object DashboardId, Name
#endregion
