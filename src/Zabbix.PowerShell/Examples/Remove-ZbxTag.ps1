# This Source Code Form is subject to the terms of the Mozilla Public
# License, v. 2.0. Copyright (c) 2026 schroeder247. SPDX-License-Identifier: MPL-2.0
<#
.SYNOPSIS
Removes a tag from hosts and keeps their other tags.
.DESCRIPTION
The Remove-ZbxTag cmdlet reads the current tags of the hosts given to -InputObject (names, ids or piped hosts), takes
out every tag named -Name, or only the one with -Value, and writes the rest back with host.update, so the other tags
stay. Every host the pipeline brings is changed in one call. It asks for confirmation first unless -Confirm:$false is
given, and writes the hosts only with -PassThru.
#>
$null = Add-ZbxTag -InputObject 'web-01' -Name 'team' -Value 'shop'
$null = Add-ZbxTag -InputObject 'web-01' -Name 'team' -Value 'checkout'

#region Remove a tag from a host
# Every tag named team goes, whatever its value; the host's other tags stay. Removing asks for a confirmation
# even at ConfirmImpact Medium, so a script passes -Confirm:$false (or -Force). -PassThru writes the updated host.
Remove-ZbxTag -InputObject 'web-01' -Name 'team' -Confirm:$false -PassThru | ForEach-Object Tags | Select-Object Tag, Value
#endregion

$null = Add-ZbxTag -InputObject 'web-01' -Name 'team' -Value 'shop'
$null = Add-ZbxTag -InputObject 'web-01' -Name 'team' -Value 'checkout'

#region Remove only one value of a tag
# With -Value, only the tag with that exact name and value is removed.
Remove-ZbxTag 'team' 'checkout' -InputObject 'web-01' -Confirm:$false
(Get-ZbxHost -Name 'web-01' -SelectTags).Tags | Select-Object Tag, Value
#endregion

#region Remove a tag from every host of a group, through the pipeline
# Get-ZbxHost writes host objects; Remove-ZbxTag binds each one. Hosts without the tag are left as they are.
Get-ZbxHost -Group 'Web servers' | Remove-ZbxTag -Name 'env' -WhatIf
Get-ZbxHost -Group 'Web servers' | Remove-ZbxTag -Name 'env' -Confirm:$false
Get-ZbxHost -Group 'Web servers' -SelectTags | Select-Object TechnicalName, @{ n = 'TagCount'; e = { @($_.Tags).Count } }
#endregion
