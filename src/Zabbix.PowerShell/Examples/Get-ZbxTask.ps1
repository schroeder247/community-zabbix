# This Source Code Form is subject to the terms of the Mozilla Public
# License, v. 2.0. Copyright (c) 2026 schroeder247. SPDX-License-Identifier: MPL-2.0
$web = Get-ZbxHost -Name 'web-01' -SelectInterfaces
$null = New-ZbxItem -HostId $web.HostId -InterfaceId $web.Interfaces[0].InterfaceId -Name 'Uptime' -Key 'system.uptime' -Type Zabbix -ValueType Unsigned -Delay '1h'

<#
.SYNOPSIS
Gets the tasks the server still tracks.
.DESCRIPTION
The Get-ZbxTask cmdlet reads tasks with task.get: the check-now, diagnostic and proxy reload requests made with
Invoke-ZbxCheckNow, Invoke-ZbxDiagnostic, Sync-ZbxProxyConfig or New-ZbxTask, with their status and, for a
diagnostic, its result. It writes every task the server still tracks, or the tasks with the ids in -Id; tasks have no
name or -Filter. It needs a connection made with Connect-ZbxServer.
#>
#region Follow a check-now task
# Invoke-ZbxCheckNow writes the id of the task it created.
$taskId = Get-ZbxItem -Host 'web-01' -Name 'Uptime' | Invoke-ZbxCheckNow
Get-ZbxTask -Id $taskId | Select-Object TaskId, Type, Status, Clock
#endregion

#region List every task the server still tracks
# The server forgets a task some time after it is done.
Get-ZbxTask | Select-Object TaskId, Type, Status
#endregion
