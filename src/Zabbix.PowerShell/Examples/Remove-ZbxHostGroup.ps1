# This Source Code Form is subject to the terms of the Mozilla Public
# License, v. 2.0. Copyright (c) 2026 schroeder247. SPDX-License-Identifier: MPL-2.0
# Vocabulary: Databases/PostgreSQL
$null = New-ZbxHostGroup -Name 'Databases'

#region Delete a host group by name
# -InputObject takes a name, an id or a group object. ConfirmImpact is High, so PowerShell asks first unless
# -Confirm:$false is given. A group that is the only group of some host cannot be deleted (the API refuses).
Remove-ZbxHostGroup -InputObject 'Databases' -Confirm:$false
#endregion

$null = New-ZbxHostGroup -Name 'Databases'
$null = New-ZbxHostGroup -Name 'Databases/PostgreSQL'

#region Delete the groups a search finds, through the pipeline
# Get-ZbxHostGroup writes group objects; Remove-ZbxHostGroup binds each one. -WhatIf lists what would go.
Get-ZbxHostGroup -Name 'Databases*' | Remove-ZbxHostGroup -WhatIf
Get-ZbxHostGroup -Name 'Databases*' | Remove-ZbxHostGroup -Confirm:$false
#endregion
