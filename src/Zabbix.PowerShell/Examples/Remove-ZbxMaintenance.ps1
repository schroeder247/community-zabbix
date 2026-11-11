# This Source Code Form is subject to the terms of the Mozilla Public
# License, v. 2.0. Copyright (c) 2026 schroeder247. SPDX-License-Identifier: MPL-2.0
# Vocabulary: Patch

$now = [DateTimeOffset]::UtcNow.ToUnixTimeSeconds()
$null = New-ZbxMaintenance -Name 'Patch window' -Host 'web-01' -ActiveSince $now -ActiveTill ($now + 3600) -TimePeriod (New-ZbxMaintenanceTimePeriod -TimePeriodType Onetime -StartDate $now -Period 3600)

#region Delete a maintenance by name
# -InputObject takes a name, an id or a maintenance object. ConfirmImpact is High, so PowerShell asks first unless
# -Confirm:$false is given. The hosts leave maintenance at once.
Remove-ZbxMaintenance -InputObject 'Patch window' -Confirm:$false
#endregion

$now = [DateTimeOffset]::UtcNow.ToUnixTimeSeconds()
$null = New-ZbxMaintenance -Name 'Patch window' -Host 'web-01' -ActiveSince ($now - 7200) -ActiveTill ($now - 3600) -TimePeriod (New-ZbxMaintenanceTimePeriod -TimePeriodType Onetime -StartDate ($now - 7200) -Period 3600)

#region Delete every expired maintenance, through the pipeline
# Get-ZbxMaintenance writes maintenance objects; the ones whose ActiveTill has passed are piped to
# Remove-ZbxMaintenance, which deletes them in one call. Run it with -WhatIf first to see what would go.
$now = [DateTimeOffset]::UtcNow
Get-ZbxMaintenance -Name 'Patch*' | Where-Object ActiveTill -LT $now | Remove-ZbxMaintenance -WhatIf
Get-ZbxMaintenance -Name 'Patch*' | Where-Object ActiveTill -LT $now | Remove-ZbxMaintenance -Confirm:$false
Get-ZbxMaintenance -Name 'Patch*' -CountOutput
#endregion
