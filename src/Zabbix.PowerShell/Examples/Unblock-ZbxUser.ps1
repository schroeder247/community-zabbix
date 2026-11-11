# This Source Code Form is subject to the terms of the Mozilla Public
# License, v. 2.0. Copyright (c) 2026 schroeder247. SPDX-License-Identifier: MPL-2.0
$null = New-ZbxUser -UserName 'jdoe' -Password (ConvertTo-SecureString 'Chang3-me-at-first-login!' -AsPlainText -Force) -RoleId (Get-ZbxRole -Name 'User role').RoleId -UserGroup 'Operators'

<#
.SYNOPSIS
Unblocks users locked out after failed logins.
.DESCRIPTION
The Unblock-ZbxUser cmdlet clears the failed login count of the users given to -InputObject (names, ids or piped users)
with user.unblock, so a user the frontend blocked after too many failed attempts can log in again at once. Every user
the pipeline brings is unblocked in one call. The command writes the users only with -PassThru, and -WhatIf shows what it
would change.
#>
#region Unblock a user
# AttemptFailed is the user's failed login count; after the unblock it is 0.
Unblock-ZbxUser -InputObject 'jdoe' -PassThru | Select-Object UserName, AttemptFailed
#endregion

#region Unblock every user with failed logins
# Get-ZbxUser writes every user; Where-Object keeps those with failed attempts.
Get-ZbxUser | Where-Object AttemptFailed -GT 0 | Unblock-ZbxUser
#endregion
