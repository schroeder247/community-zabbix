# This Source Code Form is subject to the terms of the Mozilla Public
# License, v. 2.0. Copyright (c) 2026 schroeder247. SPDX-License-Identifier: MPL-2.0
<#
.SYNOPSIS
Applies a host group's permissions and tag filters to its subgroups.
.DESCRIPTION
The Invoke-ZbxHostGroupPropagation cmdlet copies the user group permissions (-Permissions) and tag filters
(-TagFilter) of the host groups given to -InputObject (names, ids or piped groups) to their subgroups, with
hostgroup.propagate; a subgroup is a group whose name starts with the group's name and a slash, such as
Linux servers/Databases. Give at least one of the two switches. The command writes nothing, and -WhatIf shows what it
would change.
#>
#region Give a group's subgroups its permissions
# Every user group with a permission on Linux servers gets the same permission on Linux servers/...
Invoke-ZbxHostGroupPropagation -InputObject 'Linux servers' -Permissions
#endregion

#region Preview propagating permissions and tag filters
# A group piped from Get-ZbxHostGroup binds -InputObject; -WhatIf names the group and changes nothing.
Get-ZbxHostGroup -Name 'Web servers' | Invoke-ZbxHostGroupPropagation -Permissions -TagFilters -WhatIf
#endregion
