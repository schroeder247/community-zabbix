# This Source Code Form is subject to the terms of the Mozilla Public
# License, v. 2.0. Copyright (c) 2026 schroeder247. SPDX-License-Identifier: MPL-2.0
$web = Get-ZbxHost -Name 'web-01' -SelectInterfaces
$null = New-ZbxItem -HostId $web.HostId -InterfaceId $web.Interfaces[0].InterfaceId -Name 'Uptime' -Key 'system.uptime' -Type Zabbix -ValueType Unsigned -Delay '1h'

<#
.SYNOPSIS
Asks the server to check items now.
.DESCRIPTION
The Invoke-ZbxCheckNow cmdlet asks the server to poll the items given to -InputObject (names, ids or piped items) at
once, rather than at their next scheduled check, with task.create; it is the frontend's Execute now. Only polled items
can be checked now; a trapper, dependent or active agent item cannot. The command writes the id of each task it
created, which Get-ZbxTask reads, and -WhatIf shows what it would ask.
#>
#region Check an item now
# The task runs on the server (or the host's proxy) within a few seconds; the new value arrives as usual.
Get-ZbxItem -Host 'web-01' -Name 'Uptime' | Invoke-ZbxCheckNow
#endregion

#region Check now and follow the task
# Get-ZbxTask reads the task the command created; Status shows whether it ran.
$taskId = Get-ZbxItem -Host 'web-01' -Name 'Uptime' | Invoke-ZbxCheckNow
Get-ZbxTask -Id $taskId | Select-Object TaskId, Type, Status
#endregion
