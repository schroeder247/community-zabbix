# This Source Code Form is subject to the terms of the Mozilla Public
# License, v. 2.0. Copyright (c) 2026 schroeder247. SPDX-License-Identifier: MPL-2.0
$null = New-ZbxUser -Username 'jdoe' -Password (ConvertTo-SecureString 'Chang3-me-at-first-login!' -AsPlainText -Force) -RoleId (Get-ZbxRole -Name 'User role').RoleId -UserGroup 'Operators'

<#
.SYNOPSIS
Updates users from their user directory now.
.DESCRIPTION
The Invoke-ZbxUserProvision cmdlet asks the server, with user.provision, to update the users given to -InputObject
(names, ids or piped users) from the LDAP user directory they were provisioned from: their name, user groups, role and
media, as the directory's group and media mappings say. A user who was not provisioned from a directory is refused. The
command writes nothing, and -WhatIf shows what it would update.
#>
#region Update a provisioned user now
# The directory's mappings decide the user's groups and media; -WhatIf shows the user the command would update.
Invoke-ZbxUserProvision -InputObject 'jdoe' -WhatIf
#endregion

#region Update every user of a group
# Get-ZbxUser writes the users, and the command asks for all of them in one call.
Get-ZbxUser -UserGroup 'Operators' | Invoke-ZbxUserProvision -WhatIf
#endregion
