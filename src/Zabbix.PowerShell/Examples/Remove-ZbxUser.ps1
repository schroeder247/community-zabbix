# This Source Code Form is subject to the terms of the Mozilla Public
# License, v. 2.0. Copyright (c) 2026 schroeder247. SPDX-License-Identifier: MPL-2.0
$password = ConvertTo-SecureString 'Chang3-me-at-first-login!' -AsPlainText -Force
$null = New-ZbxUser -Username 'jdoe' -Password $password -RoleId (Get-ZbxRole -Name 'User role').RoleId -UserGroup 'Operators'

#region Delete a user by name
# -InputObject takes a username, an id or a user object. ConfirmImpact is High, so PowerShell asks first unless
# -Confirm:$false is given. A user still named in an action's operations cannot be deleted (the API refuses).
Remove-ZbxUser -InputObject 'jdoe' -Confirm:$false
#endregion

$null = New-ZbxUser -Username 'jdoe' -Password $password -RoleId (Get-ZbxRole -Name 'User role').RoleId -UserGroup 'Operators'

#region Delete every member of a user group, through the pipeline
# Get-ZbxUser -UserGroup writes the group's users; Remove-ZbxUser binds each one and deletes them in one call.
# Run it with -WhatIf first to see what would go.
Get-ZbxUser -UserGroup 'Operators' | Remove-ZbxUser -WhatIf
Get-ZbxUser -UserGroup 'Operators' | Remove-ZbxUser -Confirm:$false
Get-ZbxUser -UserGroup 'Operators' -CountOutput
#endregion
