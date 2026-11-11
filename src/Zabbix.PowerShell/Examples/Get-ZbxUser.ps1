# This Source Code Form is subject to the terms of the Mozilla Public
# License, v. 2.0. Copyright (c) 2026 schroeder247. SPDX-License-Identifier: MPL-2.0
$null = New-ZbxUser -UserName 'jdoe' -FirstName 'John' -LastName 'Doe' -Password (ConvertTo-SecureString 'Chang3-me-at-first-login!' -AsPlainText -Force) -RoleId (Get-ZbxRole -Name 'User role').RoleId -UserGroup 'Operators' -Media @{ mediatypeid = (Get-ZbxMediaType -Name 'Email').MediaTypeId; sendto = @('jdoe@example.com') }

#region Get a user by username
# -Name matches the username exactly; a * turns it into a search. The result is a ZabbixUser record.
Get-ZbxUser -Name 'jdoe' | Select-Object UserId, UserName, FirstName, LastName, RoleId
#endregion

#region List the members of a user group
# -UserGroup takes a user group's name, id or object.
Get-ZbxUser -UserGroup 'Operators' | Select-Object UserName, FirstName, LastName
#endregion

#region Show each user's role and groups
# -SelectRole and -SelectUserGroups add the related objects to each user (user.get's selectRole, selectUsrgrps).
Get-ZbxUser -Name 'Admin', 'jdoe' -SelectRole -SelectUserGroups | Select-Object UserName,
    @{ n = 'Role'; e = { $_.Role.Name } }, @{ n = 'Groups'; e = { $_.UserGroups.Name -join ', ' } }
#endregion

#region Find the users who are notified by e-mail
# -MediaType takes a media type's name, id or object and returns the users with a media of that type.
Get-ZbxUser -MediaType 'Email' | Select-Object UserName
#endregion
