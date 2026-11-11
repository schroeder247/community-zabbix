# This Source Code Form is subject to the terms of the Mozilla Public
# License, v. 2.0. Copyright (c) 2026 schroeder247. SPDX-License-Identifier: MPL-2.0
$before = Get-ZbxHousekeeping

<#
.SYNOPSIS
Changes how long the server keeps history, trends, events and records.
.DESCRIPTION
The Set-ZbxHousekeeping cmdlet changes the housekeeping settings with housekeeping.update: each parameter given sets
one setting, and every setting not given keeps its value. The housekeeper applies the new periods on its next run and
deletes what is older. The command writes the settings only with -PassThru, -WhatIf shows what would change, and it
needs a Super admin's connection.
#>
#region Keep audit records for a year
# A period is a time with a unit; the housekeeper deletes older records on its next run.
Set-ZbxHousekeeping -HousekeepingAuditMode $true -HousekeepingAudit '365d' -PassThru | Select-Object HousekeepingAuditMode, HousekeepingAudit
#endregion

#region Preview overriding every item's history period
# With HousekeepingHistoryGlobal, the one period applies to every item; -WhatIf makes no change.
Set-ZbxHousekeeping -HousekeepingHistoryGlobal $true -HousekeepingHistory '90d' -WhatIf
#endregion

Set-ZbxHousekeeping -HousekeepingAuditMode $before.HousekeepingAuditMode -HousekeepingAudit $before.HousekeepingAudit
