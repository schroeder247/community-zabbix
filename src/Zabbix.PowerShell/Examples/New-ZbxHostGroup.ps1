# This Source Code Form is subject to the terms of the Mozilla Public
# License, v. 2.0. Copyright (c) 2026 schroeder247. SPDX-License-Identifier: MPL-2.0
# Vocabulary: Databases/PostgreSQL
#region Create a host group
# Writes the created group (hostgroup.create, read back with hostgroup.get).
New-ZbxHostGroup -Name 'Databases'
#endregion

#region Create a nested group
# Zabbix nests groups by name: a / in the name puts it under the group named by the part before it.
New-ZbxHostGroup -Name 'Databases/PostgreSQL' | Select-Object GroupId, Name
#endregion

Remove-ZbxHostGroup -InputObject 'Databases/PostgreSQL', 'Databases' -Confirm:$false

#region Create a group and move a host into it
# The written group object can be passed on directly: -Group takes names, ids or objects. Set-ZbxHost -Group
# replaces the host's groups, so list every group the host should stay in.
$group = New-ZbxHostGroup -Name 'Databases'
Set-ZbxHost 'db-01' -Group 'Linux servers', $group | Out-Null
Get-ZbxHost -Group $group | Select-Object HostId, HostName
#endregion

#region Preview the change first
# ConfirmImpact is Medium: -WhatIf describes the create without sending it; -Confirm asks first.
New-ZbxHostGroup -Name 'Databases/PostgreSQL' -WhatIf
#endregion
