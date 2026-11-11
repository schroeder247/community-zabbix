# This Source Code Form is subject to the terms of the Mozilla Public
# License, v. 2.0. Copyright (c) 2026 schroeder247. SPDX-License-Identifier: MPL-2.0
Disable-ZbxHost -InputObject 'web-01', 'db-01'

<#
.SYNOPSIS
Starts monitoring hosts.
.DESCRIPTION
The Enable-ZbxHost cmdlet sets the hosts given to -InputObject (names, ids or piped hosts) to monitored with
host.update, so the server collects their items and evaluates their triggers again. Every host the pipeline brings is
changed in one call when the pipeline ends. The command writes nothing unless -PassThru is given, which reads the hosts
back and writes them; -WhatIf shows what would change.
#>
#region Monitor a host again
# -InputObject (position 0) takes a host name, id or object.
Enable-ZbxHost -InputObject 'web-01'
#endregion

#region Monitor every host of a group and show the result
# The piped hosts are enabled in one host.update; -PassThru writes them back with their Status.
Get-ZbxHost -Group 'Linux servers' | Enable-ZbxHost -PassThru | Select-Object TechnicalName, Status
#endregion
