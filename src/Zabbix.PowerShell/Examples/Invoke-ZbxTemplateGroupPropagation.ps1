# This Source Code Form is subject to the terms of the Mozilla Public
# License, v. 2.0. Copyright (c) 2026 schroeder247. SPDX-License-Identifier: MPL-2.0
<#
.SYNOPSIS
Applies a template group's permissions to its subgroups.
.DESCRIPTION
The Invoke-ZbxTemplateGroupPropagation cmdlet copies the user group permissions of the template groups given to
-InputObject (names, ids or piped groups) to their subgroups, with templategroup.propagate; a subgroup is a group whose
name starts with the group's name and a slash, such as Templates/Web/Shop. -Permissions is required by the server. The
command writes nothing, and -WhatIf shows what it would change.
#>
#region Give a template group's subgroups its permissions
# Every user group with a permission on Templates/Web gets the same permission on Templates/Web/...
Invoke-ZbxTemplateGroupPropagation -InputObject 'Templates/Web' -Permissions
#endregion

#region Preview propagating every template group's permissions
# -WhatIf names each group the command would propagate, and changes nothing.
Get-ZbxTemplateGroup | Invoke-ZbxTemplateGroupPropagation -Permissions -WhatIf
#endregion
