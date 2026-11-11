# This Source Code Form is subject to the terms of the Mozilla Public
# License, v. 2.0. Copyright (c) 2026 schroeder247. SPDX-License-Identifier: MPL-2.0
# Vocabulary: Database upgrade; Checkout release

#region Put a host into maintenance for the next two hours
# -ActiveSince/-ActiveTill bound the maintenance (Unix seconds); the one-time period inside it is built with
# New-ZbxMaintenanceTimePeriod. -HostReference (alias -Host) takes host names, ids or objects. Data is still collected
# (Normal).
$now = [DateTimeOffset]::UtcNow.ToUnixTimeSeconds()
$window = New-ZbxMaintenanceTimePeriod -TimePeriodType Onetime -StartDate $now -Period 7200
New-ZbxMaintenance -Name 'Patch window' -Host 'web-01' -ActiveSince $now -ActiveTill ($now + 7200) -TimePeriod $window |
    Select-Object MaintenanceId, Name, MaintenanceType, ActiveSince, ActiveTill
#endregion

#region Stop data collection for a group during a planned outage
# -MaintenanceType Nodata tells the server to drop incoming data for the hosts while the window is on. -Group takes
# host group names, ids or objects; -Description is free text shown in the frontend.
$start = [DateTimeOffset]::new([datetime]::UtcNow.Date.AddDays(1).AddHours(22)).ToUnixTimeSeconds()
$window = New-ZbxMaintenanceTimePeriod -TimePeriodType Onetime -StartDate $start -Period (4 * 3600)
New-ZbxMaintenance -Name 'Database upgrade' -Group 'Linux servers' -MaintenanceType Nodata -Description 'PostgreSQL 16 upgrade' -ActiveSince $start -ActiveTill ($start + 4 * 3600) -TimePeriod $window |
    Select-Object Name, MaintenanceType, Description
#endregion

#region Suppress only the problems that carry a tag
# With data collection on, -Tag limits the maintenance to problems whose tags match; other problems on the same
# hosts still alert. Each tag is a hashtable (operator 2 = Like, 0 = Equals).
$now = [DateTimeOffset]::UtcNow.ToUnixTimeSeconds()
$window = New-ZbxMaintenanceTimePeriod -TimePeriodType Onetime -StartDate $now -Period 3600
$null = New-ZbxMaintenance -Name 'Checkout release' -Host 'web-01', 'db-01' -Tag @{ tag = 'env'; operator = 0; value = 'prod' } -ActiveSince $now -ActiveTill ($now + 3600) -TimePeriod $window
(Get-ZbxMaintenance -Name 'Checkout release' -SelectTags -SelectHosts) | Select-Object Name, @{ n = 'Hosts'; e = { $_.Hosts.HostName -join ', ' } }, @{ n = 'Tags'; e = { $_.Tags | ForEach-Object { '{0}={1}' -f $_.tag, $_.value } } }
#endregion
