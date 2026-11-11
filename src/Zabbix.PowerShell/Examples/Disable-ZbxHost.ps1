# This Source Code Form is subject to the terms of the Mozilla Public
# License, v. 2.0. Copyright (c) 2026 schroeder247. SPDX-License-Identifier: MPL-2.0
<#
.SYNOPSIS
Stops monitoring hosts.
.DESCRIPTION
The Disable-ZbxHost cmdlet sets the hosts given to -InputObject (names, ids or piped hosts) to not monitored with
host.update: the server stops collecting their items and evaluating their triggers, and keeps their configuration and
history. Every host the pipeline brings is changed in one call when the pipeline ends. The command writes nothing unless
-PassThru is given; Enable-ZbxHost monitors the hosts again.
#>
#region Stop monitoring a host
# -InputObject (position 0) takes a host name, id or object.
Disable-ZbxHost -InputObject 'db-01'
#endregion

#region Stop monitoring the hosts a search finds
# -WhatIf names each host the command would disable; -PassThru writes the hosts back with their Status.
Get-ZbxHost -Name 'web-*' | Disable-ZbxHost -WhatIf
Get-ZbxHost -Name 'web-*' | Disable-ZbxHost -PassThru | Select-Object TechnicalName, Status
#endregion
