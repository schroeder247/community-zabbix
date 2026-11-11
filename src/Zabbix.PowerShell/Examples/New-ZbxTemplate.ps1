# This Source Code Form is subject to the terms of the Mozilla Public
# License, v. 2.0. Copyright (c) 2026 schroeder247. SPDX-License-Identifier: MPL-2.0
# Vocabulary: Nginx by HTTP; Web App Nginx
#region Create a template in a template group
# -Host is the template's technical name; -Group takes template group names, ids or objects. The created
# template is read back and written.
New-ZbxTemplate -Host 'Nginx by HTTP' -Group 'Templates/Web' | Select-Object TemplateId, HostName
#endregion

#region Create a template that links another, with tags and a macro
# -LinkedTemplate nests templates: 'Web App Nginx' brings along everything 'Nginx by HTTP' has. -Tag and
# -Macro take hashtables shaped like the API's objects.
New-ZbxTemplate -Host 'Web App Nginx' -Group 'Templates/Web' -LinkedTemplate 'Nginx by HTTP' `
    -Description 'Nginx in front of the shop' -Tag @{ tag = 'class'; value = 'software' } `
    -Macro @{ macro = '{$NGINX.PORT}'; value = '8080' } | Out-Null
Get-ZbxTemplate -Name 'Web App Nginx' -SelectParentTemplates -SelectTags -SelectMacros |
    Select-Object HostName, Description,
        @{ n = 'Links'; e = { $_.ParentTemplates.HostName } },
        @{ n = 'Macros'; e = { $_.Macros.Macro } }
#endregion

Remove-ZbxTemplate -InputObject 'Web App Nginx', 'Nginx by HTTP' -Confirm:$false

#region Create a template, give it an item and link it to a host
# The written template can be piped on: New-ZbxItem binds -HostId from its TemplateId.
$template = New-ZbxTemplate -Host 'Nginx by HTTP' -Group 'Templates/Web'
$template | New-ZbxItem -Name 'Nginx status' -Key 'nginx.status' -Type Trapper -ValueType Text | Out-Null
Set-ZbxHost 'web-01' -Template $template | Out-Null
Get-ZbxItem -Host 'web-01' -Name 'Nginx*' | Select-Object HostId, Name, Key
#endregion
