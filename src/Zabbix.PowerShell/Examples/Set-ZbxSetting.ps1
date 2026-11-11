# This Source Code Form is subject to the terms of the Mozilla Public
# License, v. 2.0. Copyright (c) 2026 schroeder247. SPDX-License-Identifier: MPL-2.0
$before = Get-ZbxSetting

<#
.SYNOPSIS
Changes the frontend's global settings.
.DESCRIPTION
The Set-ZbxSetting cmdlet changes the global settings with settings.update. -Request is a ZabbixSettingsUpdateRequest
that carries only the settings to change; a hashtable casts to it, keyed by its property names or the API's field
names. Every setting left out keeps its value. The command writes the settings as read back after the update, and
-WhatIf shows what would change.
#>
#region Change the work period
# Monday to Friday, 08:00 to 18:00; graphs shade the rest of the week as non-working time.
Set-ZbxSetting -Request @{ WorkPeriod = '1-5,08:00-18:00' } | Select-Object WorkPeriod
#endregion

#region Preview renaming a severity
# -WhatIf shows the change and makes none.
Set-ZbxSetting -Request @{ SeverityName5 = 'Critical' } -WhatIf
#endregion

$null = Set-ZbxSetting -Request @{ WorkPeriod = $before.WorkPeriod }
