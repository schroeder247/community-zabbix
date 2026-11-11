# This Source Code Form is subject to the terms of the Mozilla Public
# License, v. 2.0. Copyright (c) 2026 schroeder247. SPDX-License-Identifier: MPL-2.0
<#
.SYNOPSIS
Links templates to hosts.
.DESCRIPTION
The Add-ZbxHostTemplate cmdlet links the template in -Template to the hosts given to -InputObject (names, ids or piped
hosts) with host.massadd; the templates already linked stay. Each host then gets the template's items, triggers, graphs,
discovery rules and dashboards. The command writes nothing unless -PassThru is given, and needs a connection made with
Connect-ZbxServer.
#>
#region Link a template to a host
# -InputObject (position 0) takes the host and -Template (position 1) the template, each by name, id or object.
Add-ZbxHostTemplate -InputObject 'db-01' -Template 'Web App'
Get-ZbxHost -Name 'db-01' -SelectParentTemplates | ForEach-Object { $_.ParentTemplates.HostName }
#endregion

#region Link a template to every host of a group
# The hosts piped from Get-ZbxHost are linked in one call; -PassThru writes them back.
Get-ZbxHost -Group 'Web servers' | Add-ZbxHostTemplate -Template 'Web App' -PassThru | Select-Object HostName
#endregion
