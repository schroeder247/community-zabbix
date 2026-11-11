# This Source Code Form is subject to the terms of the Mozilla Public
# License, v. 2.0. Copyright (c) 2026 schroeder247. SPDX-License-Identifier: MPL-2.0
<#
.SYNOPSIS
Copies a host to a new name on the same server.
.DESCRIPTION
The Copy-ZbxHost cmdlet exports the host in -InputObject with configuration.export, renames the document to -NewName, points
its graphs and trigger expressions at the copy, and imports it with configuration.import. The copy gets the host's
items, triggers, graphs, discovery rules, macros, tags and interfaces; a calculated item's formula that names the
original host is copied unchanged. With -Force a host already named -NewName is updated from the copy; without it only
what is missing is created. The command writes the copy only with -PassThru.
#>
#region Copy a host
# The copy takes web-01's groups, interfaces, items and triggers under the name web-02.
Get-ZbxHost -Name 'web-01' | Copy-ZbxHost -NewName 'web-02' -PassThru | Select-Object TechnicalName, HostId
#endregion

#region Copy a host and check what came along
# -InputObject (position 0) and -NewName (position 1) are positional; the copy's items are the host's.
Copy-ZbxHost 'web-01' 'web-03'
Get-ZbxItem -Host 'web-03' | Select-Object Key, Type
#endregion
