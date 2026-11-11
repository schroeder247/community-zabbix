# This Source Code Form is subject to the terms of the Mozilla Public
# License, v. 2.0. Copyright (c) 2026 schroeder247. SPDX-License-Identifier: MPL-2.0
$null = New-ZbxUser -Username 'jdoe' -Password (ConvertTo-SecureString 'Chang3-me-at-first-login!' -AsPlainText -Force) -RoleId (Get-ZbxRole -Name 'User role').RoleId -UserGroup 'Operators'

#region Get a user group by name
# -Name matches exactly; a * turns it into a search. The result is a ZabbixUserGroup record.
Get-ZbxUserGroup -Name 'Operators' | Select-Object UserGroupId, Name, GuiAccess, UsersStatus
#endregion

#region List a group's members
# -SelectUsers adds the group's users to each group.
(Get-ZbxUserGroup -Name 'Operators' -SelectUsers).Users | Select-Object Username
#endregion

#region Find the groups a user is in
# -User takes a username, id or object.
Get-ZbxUserGroup -User 'jdoe' | Select-Object Name
#endregion

#region Report every group with its member count
# A realistic inventory: one row per group, sorted by the number of members.
Get-ZbxUserGroup -SelectUsers | Select-Object Name, @{ n = 'Members'; e = { @($_.Users).Count } } |
    Sort-Object Members -Descending
#endregion
