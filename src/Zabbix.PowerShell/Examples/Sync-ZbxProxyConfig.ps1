# This Source Code Form is subject to the terms of the Mozilla Public
# License, v. 2.0. Copyright (c) 2026 schroeder247. SPDX-License-Identifier: MPL-2.0
$null = New-ZbxProxy -Name 'proxy-01' -OperatingMode Active

<#
.SYNOPSIS
Asks the server to send its configuration to proxies now.
.DESCRIPTION
The Sync-ZbxProxyConfig cmdlet asks the server, with task.create, to send the current configuration to the proxies
given to -InputObject (names, ids or piped proxies) now, rather than at their next configuration sync; it is the
frontend's Refresh configuration. Every proxy the pipeline brings goes into one task. The command writes the task's id,
and -WhatIf shows what it would ask.
#>
#region Refresh a proxy's configuration
# -InputObject (position 0) takes a proxy's name, id or object.
Sync-ZbxProxyConfig -InputObject 'proxy-01'
#endregion

#region Refresh every proxy
# Get-ZbxProxy writes every proxy, and the command asks for all of them in one task.
Get-ZbxProxy | Sync-ZbxProxyConfig -WhatIf
#endregion
