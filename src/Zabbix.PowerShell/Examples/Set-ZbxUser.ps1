# This Source Code Form is subject to the terms of the Mozilla Public
# License, v. 2.0. Copyright (c) 2026 schroeder247. SPDX-License-Identifier: MPL-2.0
$null = New-ZbxUser -UserName 'jdoe' -Password (ConvertTo-SecureString 'Chang3-me-at-first-login!' -AsPlainText -Force) -RoleId (Get-ZbxRole -Name 'User role').RoleId -UserGroup 'Operators'

#region Change a user's language and time zone
# -InputObject (position 0) takes the user's name, id or object; a parameter not given keeps its value.
Set-ZbxUser -InputObject 'jdoe' -Language 'de_DE' -Timezone 'Europe/Berlin' -PassThru | Select-Object UserName, Language, Timezone
#endregion

#region Move a user to other user groups
# -UserGroup replaces the user's groups; give every group the user should stay in.
Set-ZbxUser -InputObject 'jdoe' -UserGroup 'Operators' -WhatIf
#endregion

#region Set a new password
# The password is a SecureString, never a plain string.
Set-ZbxUser -InputObject 'jdoe' -Password (ConvertTo-SecureString 'N3w-passw0rd-for-jdoe!' -AsPlainText -Force)
#endregion
