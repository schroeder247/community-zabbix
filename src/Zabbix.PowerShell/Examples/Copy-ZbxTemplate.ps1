# This Source Code Form is subject to the terms of the Mozilla Public
# License, v. 2.0. Copyright (c) 2026 schroeder247. SPDX-License-Identifier: MPL-2.0
<#
.SYNOPSIS
Copies a template to a new name on the same server.
.DESCRIPTION
The Copy-ZbxTemplate cmdlet exports the template in -InputObject with configuration.export, renames the document to -NewName,
gives every part a new UUID and imports it with configuration.import, so the copy is a template of its own rather than
an update of the original. Items, triggers, graphs, discovery rules and dashboards come along. With -Force a template
already named -NewName is updated from the copy. The command writes the copy only with -PassThru.
#>
#region Copy a template to change it safely
# The copy is a separate template: change it, link it to one test host, and the original stays as it is.
Copy-ZbxTemplate -InputObject 'Web App' -NewName 'Web App v2' -PassThru | Select-Object HostName, TemplateId
#endregion

#region Preview a copy
# -WhatIf names the template the command would copy, and copies nothing.
Copy-ZbxTemplate -InputObject 'Web App' -NewName 'Web App v2' -WhatIf
#endregion
