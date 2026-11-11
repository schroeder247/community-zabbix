# This Source Code Form is subject to the terms of the Mozilla Public
# License, v. 2.0. Copyright (c) 2026 schroeder247. SPDX-License-Identifier: MPL-2.0
# Vocabulary: Patch

$now = [DateTimeOffset]::UtcNow.ToUnixTimeSeconds()
$null = New-ZbxMaintenance -Name 'Patch window' -Host 'web-01' -ActiveSince $now -ActiveTill ($now + 3600) -TimePeriod (New-ZbxMaintenanceTimePeriod -TimePeriodType Onetime -StartDate $now -Period 3600)

#region Extend a maintenance by an hour
# -InputObject takes a name, id or object; only the parameters given are sent (maintenance.update). With -PassThru
# the changed maintenance is read back and written to the pipeline.
$m = Get-ZbxMaintenance -Name 'Patch window' -SelectTimePeriods
$period = New-ZbxMaintenanceTimePeriod -TimePeriodType Onetime -StartDate $m.ActiveSince -Period 7200
Set-ZbxMaintenance -InputObject 'Patch window' -ActiveTill ($m.ActiveSince + [TimeSpan]::FromHours(2)) -TimePeriod $period -PassThru |
    Select-Object Name, ActiveSince, ActiveTill
#endregion

#region Change the hosts a maintenance covers
# -Host (the alias of -HostReference) and -Group replace the whole list, they do not add to it: include every host
# that should stay covered.
Set-ZbxMaintenance -InputObject 'Patch window' -Host 'web-01', 'db-01'
(Get-ZbxMaintenance -Name 'Patch window' -SelectHosts).Hosts.HostName
#endregion

#region Update every maintenance a search finds, through the pipeline
# Get-ZbxMaintenance writes maintenance objects; Set-ZbxMaintenance binds each one. Run it with -WhatIf first.
Get-ZbxMaintenance -Name 'Patch*' | Set-ZbxMaintenance -Description 'Approved in CHG-1042' -WhatIf
Get-ZbxMaintenance -Name 'Patch*' | Set-ZbxMaintenance -Description 'Approved in CHG-1042' -PassThru | Select-Object Name, Description
#endregion
