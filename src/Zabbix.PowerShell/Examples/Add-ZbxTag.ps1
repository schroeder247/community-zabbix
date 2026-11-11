# This Source Code Form is subject to the terms of the Mozilla Public
# License, v. 2.0. Copyright (c) 2026 schroeder247. SPDX-License-Identifier: MPL-2.0
<#
.SYNOPSIS
Adds a tag to hosts and keeps their other tags.
#>
#region Add a tag to a host
# The tag is merged with the host's current tags (host.update with the full list), so env=prod stays. -PassThru
# writes the updated host.
Add-ZbxTag -InputObject 'web-01' -Name 'team' -Value 'shop' -PassThru | ForEach-Object Tags | Select-Object Tag, Value
#endregion

#region Tag every host a search finds, through the pipeline
# Get-ZbxHost writes host objects; Add-ZbxTag binds each one.
Get-ZbxHost -Group 'Linux servers' | Add-ZbxTag -Name 'backup' -Value 'daily'
Get-ZbxHost -Group 'Linux servers' -SelectTags |
    Select-Object TechnicalName, @{ n = 'Tags'; e = { ($_.Tags | ForEach-Object { '{0}={1}' -f $_.Tag, $_.Value }) -join ', ' } }
#endregion

#region Add a tag without a value, positionally
# -Name and -Value are positions 0 and 1 (-Value may be left out); the hosts come from the pipeline or -InputObject.
Add-ZbxTag 'maintenance' -InputObject 'db-01'
(Get-ZbxHost -Name 'db-01' -SelectTags).Tags | Select-Object Tag, Value
#endregion
