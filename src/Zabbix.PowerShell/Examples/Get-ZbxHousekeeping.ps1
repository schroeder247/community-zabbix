# This Source Code Form is subject to the terms of the Mozilla Public
# License, v. 2.0. Copyright (c) 2026 schroeder247. SPDX-License-Identifier: MPL-2.0
<#
.SYNOPSIS
Gets the server's housekeeping settings.
.DESCRIPTION
The Get-ZbxHousekeeping cmdlet reads the housekeeping settings with housekeeping.get: whether the housekeeper deletes
old events, history, trends, sessions and audit records, and how long it keeps each. A global history or trends period
overrides every item's own when its Global setting is true. It writes one ZabbixHousekeepingSettings object and needs a
Super admin's connection.
#>
#region Read how long history and trends are kept
# A period is a time with a unit, such as 31d or 365d.
Get-ZbxHousekeeping | Select-Object HousekeepingHistoryGlobal, HousekeepingHistory, HousekeepingTrendsGlobal, HousekeepingTrends
#endregion

#region Read every setting
# Format-List shows each setting on its own line.
Get-ZbxHousekeeping | Format-List
#endregion
