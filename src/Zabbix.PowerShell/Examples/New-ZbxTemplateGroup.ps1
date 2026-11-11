# This Source Code Form is subject to the terms of the Mozilla Public
# License, v. 2.0. Copyright (c) 2026 schroeder247. SPDX-License-Identifier: MPL-2.0
# Vocabulary: Templates/Applications
#region Create a template group
# Writes the created group (templategroup.create, read back with templategroup.get). A / in the name nests it.
New-ZbxTemplateGroup -Name 'Templates/Applications' | Select-Object GroupId, Name
#endregion

Remove-ZbxTemplateGroup -InputObject 'Templates/Applications' -Confirm:$false

#region Create a group and put a template in it
# The written group object can be passed on: -Group takes names, ids or objects. Set-ZbxTemplate -Group replaces
# the template's groups.
$group = New-ZbxTemplateGroup -Name 'Templates/Applications'
Set-ZbxTemplate -InputObject 'Web App' -Group 'Templates/Web', $group | Out-Null
Get-ZbxTemplate -Group $group | Select-Object TemplateId, HostName
#endregion

#region Preview the change first
# ConfirmImpact is Medium: -WhatIf describes the create without sending it; -Confirm asks first.
New-ZbxTemplateGroup -Name 'Templates/Applications' -WhatIf
#endregion
