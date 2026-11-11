# This Source Code Form is subject to the terms of the Mozilla Public
# License, v. 2.0. Copyright (c) 2026 schroeder247. SPDX-License-Identifier: MPL-2.0
$null = New-ZbxUser -UserName 'jdoe' -Password (ConvertTo-SecureString 'Chang3-me-at-first-login!' -AsPlainText -Force) -RoleId (Get-ZbxRole -Name 'User role').RoleId -UserGroup 'Operators'

<#
.SYNOPSIS
Resets users' one-time password secrets.
.DESCRIPTION
The Reset-ZbxUserTotp cmdlet deletes the time-based one-time password secret of the users given to -InputObject (names,
ids or piped users) with user.resettotp, so each enrolls a new authenticator at the next login, for a lost or replaced
phone. Every user the pipeline brings is reset in one call. The command writes the users only with -PassThru, and
-WhatIf shows what it would reset.
#>
#region Reset a user's authenticator
# -InputObject (position 0) takes a user's name, id or object.
Reset-ZbxUserTotp -InputObject 'jdoe'
#endregion

#region Preview resetting every user of a group
# -WhatIf names each user the command would reset, and resets nothing.
Get-ZbxUser -UserGroup 'Operators' | Reset-ZbxUserTotp -WhatIf
#endregion
