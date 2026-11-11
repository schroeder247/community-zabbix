# This Source Code Form is subject to the terms of the Mozilla Public
# License, v. 2.0. Copyright (c) 2026 schroeder247. SPDX-License-Identifier: MPL-2.0
$null = New-ZbxHost -Host 'web-02' -Group 'Web servers' -Confirm:$false

#region Delete a host by name
# -InputObject takes a name, an id or a host object. ConfirmImpact is High, so PowerShell asks first unless
# -Confirm:$false is given.
Remove-ZbxHost -InputObject 'web-02' -Confirm:$false
#endregion

$null = New-ZbxHost -Host 'web-02' -Group 'Web servers' -Confirm:$false

#region Delete every host a search finds, through the pipeline
# Get-ZbxHost writes host objects; Remove-ZbxHost binds each one. Run it with -WhatIf first to see what would go.
Get-ZbxHost -Name 'web-02*' | Remove-ZbxHost -WhatIf
Get-ZbxHost -Name 'web-02*' | Remove-ZbxHost -Confirm:$false
#endregion
