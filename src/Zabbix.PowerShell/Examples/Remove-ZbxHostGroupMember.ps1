# This Source Code Form is subject to the terms of the Mozilla Public
# License, v. 2.0. Copyright (c) 2026 schroeder247. SPDX-License-Identifier: MPL-2.0
<#
.SYNOPSIS
Removes hosts from a host group.
.DESCRIPTION
The Remove-ZbxHostGroupMember cmdlet takes the hosts given to -InputObject (names, ids or piped hosts) out of the host
group in -Group with host.massremove, in one call when the pipeline ends; their other groups stay. A host must stay in at
least one group, so the server refuses to remove its last. The command asks for confirmation first unless
-Confirm:$false or -Force is given, and writes the hosts only with -PassThru.
#>
#region Take a host out of a group
# ConfirmImpact is High, so PowerShell asks first; -Confirm:$false answers for a script that has already decided.
Remove-ZbxHostGroupMember -InputObject 'web-01' -Group 'Web servers' -Confirm:$false
#endregion

#region Preview taking every host of a group out of it
# -WhatIf names each host the command would change, and changes nothing.
Get-ZbxHost -Group 'Linux servers' | Remove-ZbxHostGroupMember -Group 'Linux servers' -WhatIf
#endregion
