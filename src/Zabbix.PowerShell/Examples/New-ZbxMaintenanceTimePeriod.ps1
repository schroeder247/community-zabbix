# This Source Code Form is subject to the terms of the Mozilla Public
# License, v. 2.0. Copyright (c) 2026 schroeder247. SPDX-License-Identifier: MPL-2.0
# Vocabulary: Sunday patching; Month-end freeze

#region Build a one-time window and use it in a maintenance
# A Onetime period starts at -StartDate (Unix seconds) and lasts -Period seconds. The builder only writes a
# hashtable in the wire shape; New-ZbxMaintenance sends it in -TimePeriod.
$start = [DateTimeOffset]::UtcNow.AddHours(1).ToUnixTimeSeconds()
$window = New-ZbxMaintenanceTimePeriod -TimePeriodType Onetime -StartDate $start -Period 7200
New-ZbxMaintenance -Name 'Patch window' -Host 'web-01' -ActiveSince $start -ActiveTill ($start + 7200) -TimePeriod $window |
    Select-Object MaintenanceId, Name
#endregion

#region A weekly window: Sundays 02:00 to 04:00
# Weekly periods use -DayOfWeek as a bit mask (Monday = 1 ... Sunday = 64), -StartTime as the time of day
# and -Every as the week interval. -SelectTimePeriods reads the stored periods back.
$weekly = New-ZbxMaintenanceTimePeriod -TimePeriodType Weekly -Every 1 -DayOfWeek 64 -StartTime '02:00' -Period (2 * 3600)
$since = [DateTimeOffset]::UtcNow.ToUnixTimeSeconds()
$null = New-ZbxMaintenance -Name 'Sunday patching' -Group 'Web servers' -ActiveSince $since -ActiveTill ($since + 90 * 86400) -TimePeriod $weekly
(Get-ZbxMaintenance -Name 'Sunday patching' -SelectTimePeriods).TimePeriods | Format-Table timeperiod_type, every, dayofweek, start_time, period
#endregion

#region Several windows in one maintenance: the last Friday of every month, and every night
# A Monthly period with -Every 5 (the API's "last" week) and -DayOfWeek 16 (Friday) means "the last Friday"; -Month is a bit mask of
# months (4095 = all twelve). -TimePeriod takes an array, so one maintenance can hold several windows.
$periods = @(
    New-ZbxMaintenanceTimePeriod -TimePeriodType Monthly -Every 5 -DayOfWeek 16 -Month 4095 -StartTime '22:00' -Period 3600
    New-ZbxMaintenanceTimePeriod -TimePeriodType Daily -Every 1 -StartTime '03:00' -Period 900
)
$since = [DateTimeOffset]::UtcNow.ToUnixTimeSeconds()
$null = New-ZbxMaintenance -Name 'Month-end freeze' -Host 'db-01' -ActiveSince $since -ActiveTill ($since + 365 * 86400) -TimePeriod $periods
(Get-ZbxMaintenance -Name 'Month-end freeze' -SelectTimePeriods).TimePeriods | Format-Table timeperiod_type, every, dayofweek, month, start_time, period
#endregion
