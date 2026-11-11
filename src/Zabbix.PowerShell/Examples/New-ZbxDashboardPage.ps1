# This Source Code Form is subject to the terms of the Mozilla Public
# License, v. 2.0. Copyright (c) 2026 schroeder247. SPDX-License-Identifier: MPL-2.0
#region Build a page and use it in a new dashboard
# The builder writes one page hashtable holding its widgets (built with New-ZbxDashboardWidget); New-ZbxDashboard
# sends the pages in -Page, in order.
$page = New-ZbxDashboardPage -Name 'Overview' -Widget (New-ZbxDashboardWidget -Type 'clock' -Name 'Server time' -Width 12 -Height 3)
New-ZbxDashboard -Name 'Ops overview' -Page $page | Select-Object DashboardId, Name
#endregion

#region A slide show of several pages
# -DisplayPeriod is how many seconds a page stays on screen in slide-show mode (0 = the dashboard's own period);
# -AutoStart starts the slide show when the dashboard opens.
$group = Get-ZbxHostGroup -Name 'Linux servers'
$pages = @(
    New-ZbxDashboardPage -Name 'Problems' -DisplayPeriod 60 -Widget (New-ZbxDashboardWidget -Type 'problems' -Width 72 -Height 8 -Field @{ type = 2; name = 'groupids.0'; value = $group.GroupId })
    New-ZbxDashboardPage -Name 'Clock' -DisplayPeriod 30 -Widget (New-ZbxDashboardWidget -Type 'clock' -Width 12 -Height 3)
)
$null = New-ZbxDashboard -Name 'Service status' -DisplayPeriod 60 -AutoStart $true -Page $pages
(Get-ZbxDashboard -Name 'Service status' -SelectPages).Pages | Select-Object Name, DisplayPeriod
#endregion
