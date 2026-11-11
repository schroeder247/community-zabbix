# This Source Code Form is subject to the terms of the Mozilla Public
# License, v. 2.0. Copyright (c) 2026 schroeder247. SPDX-License-Identifier: MPL-2.0
$null = New-ZbxUser -UserName 'jdoe' -Password (ConvertTo-SecureString 'Chang3-me-at-first-login!' -AsPlainText -Force) -RoleId (Get-ZbxRole -Name 'User role').RoleId -UserGroup 'Operators'

#region List the roles
# Without parameters every role is returned. Type is the user type the role grants (User, Admin, SuperAdmin);
# ReadOnly marks the built-in roles that cannot be changed.
Get-ZbxRole | Select-Object RoleId, Name, Type, ReadOnly
#endregion

#region Get a role's id to create a user with it
# New-ZbxUser needs -RoleId; read it by name.
(Get-ZbxRole -Name 'User role').RoleId
#endregion

#region Show which users have a role
# -SelectUsers adds the role's users to each role.
Get-ZbxRole -Name 'User role' -SelectUsers | Select-Object -ExpandProperty Users | Select-Object UserName
#endregion

#region Check whether a role may use the API
# -SelectRules adds the role's rules; they keep the API's dotted names, so read them with PSObject.Properties.
$rules = (Get-ZbxRole -Name 'User role' -SelectRules).Rules
$rules.PSObject.Properties['api.access'].Value
#endregion
