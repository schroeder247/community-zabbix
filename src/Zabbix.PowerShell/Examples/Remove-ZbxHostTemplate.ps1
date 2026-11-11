# This Source Code Form is subject to the terms of the Mozilla Public
# License, v. 2.0. Copyright (c) 2026 schroeder247. SPDX-License-Identifier: MPL-2.0
Add-ZbxHostTemplate -InputObject 'web-01', 'db-01' -Template 'Web App'

<#
.SYNOPSIS
Unlinks templates from hosts.
.DESCRIPTION
The Remove-ZbxHostTemplate cmdlet unlinks the template in -Template from the hosts given to -InputObject (names, ids or
piped hosts) with host.massremove, in one call when the pipeline ends. The items, triggers and graphs the template
brought stay on each host as the host's own, no longer linked. The command asks for confirmation first unless
-Confirm:$false or -Force is given, and writes the hosts only with -PassThru.
#>
#region Unlink a template from a host
# ConfirmImpact is High; -Confirm:$false skips the prompt.
Remove-ZbxHostTemplate -InputObject 'web-01' -Template 'Web App' -Confirm:$false
#endregion

#region Unlink a template from the hosts a search finds
# -WhatIf first shows each host the command would change; the second call changes them.
Get-ZbxHost -Name 'db-*' | Remove-ZbxHostTemplate -Template 'Web App' -WhatIf
Get-ZbxHost -Name 'db-*' | Remove-ZbxHostTemplate -Template 'Web App' -Confirm:$false
#endregion
