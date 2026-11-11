# This Source Code Form is subject to the terms of the Mozilla Public
# License, v. 2.0. Copyright (c) 2026 schroeder247. SPDX-License-Identifier: MPL-2.0
$now = [DateTimeOffset]::UtcNow.ToUnixTimeSeconds()
$null = New-ZbxMaintenance -Name 'Patch window' -Group 'Web servers' -Host 'db-01' -ActiveSince $now -ActiveTill ($now + 7200) -TimePeriod (New-ZbxMaintenanceTimePeriod -TimePeriodType Onetime -StartDate $now -Period 7200)

#region Get a maintenance by name
# -Name matches exactly; a * turns it into a search.
Get-ZbxMaintenance -Name 'Patch window' | Select-Object MaintenanceId, Name, MaintenanceType, ActiveSince, ActiveTill
#endregion

#region Find the maintenances a host or a group is assigned to
# -Host (the alias of -HostReference) and -Group (maintenance.get's hostids, groupids) take names, ids or objects and
# match direct assignment only: a maintenance on 'Web servers' is not returned for -Host 'web-01'.
Get-ZbxMaintenance -Host 'db-01' | Select-Object Name
Get-ZbxMaintenance -Group 'Web servers' | Select-Object Name
#endregion

#region Show what a maintenance covers and when
# The -Select* switches add the related objects: the host groups (in HostGroupsRead), hosts and time periods.
Get-ZbxMaintenance -Name 'Patch window' -SelectHostGroups -SelectHosts -SelectTimePeriods | ForEach-Object {
    [pscustomobject]@{
        Name   = $_.Name
        Groups = $_.HostGroupsRead.Name -join ', '
        Hosts  = $_.Hosts.TechnicalName -join ', '
        Until  = $_.ActiveTill.UtcDateTime
    }
}
#endregion

#region List the maintenances that are active right now
# ActiveSince/ActiveTill are times; compare them with the current time. (A maintenance's periods decide
# when inside that range the hosts are actually in maintenance.)
$now = [DateTimeOffset]::UtcNow
Get-ZbxMaintenance | Where-Object { $_.ActiveSince -le $now -and $_.ActiveTill -gt $now } | Select-Object Name, ActiveTill
#endregion
