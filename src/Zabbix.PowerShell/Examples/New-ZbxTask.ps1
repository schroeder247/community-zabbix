# This Source Code Form is subject to the terms of the Mozilla Public
# License, v. 2.0. Copyright (c) 2026 schroeder247. SPDX-License-Identifier: MPL-2.0
$web = Get-ZbxHost -Name 'web-01' -SelectInterfaces
$null = New-ZbxItem -HostId $web.HostId -InterfaceId $web.Interfaces[0].InterfaceId -Name 'Uptime' -Key 'system.uptime' -Type Zabbix -ValueType Unsigned -Delay '1h'

<#
.SYNOPSIS
Creates a task for the server to run now.
.DESCRIPTION
The New-ZbxTask cmdlet creates a server task with task.create: a check of an item now (-Type CheckNow), a dump of the
server's internal state (DiagnosticInformation) or a configuration push to proxies (ProxyConfigurationReload).
-Request carries what the task needs, cast from a hashtable: the item id, the diagnostic sections, or the proxy ids.
It reads the created tasks back and writes them; Invoke-ZbxCheckNow, Invoke-ZbxDiagnostic and Sync-ZbxProxyConfig are
the shorter forms of the three.
#>
#region Check an item now
# The same task Invoke-ZbxCheckNow creates; -Request names the item by its id.
$item = Get-ZbxItem -Host 'web-01' -Name 'Uptime'
New-ZbxTask -Type CheckNow -Request @{ itemid = $item.ItemId } | Select-Object TaskId, Type, Status
#endregion

#region Ask for the history cache statistics
# A diagnostic task; Get-ZbxTask shows its Result once the server has run it.
New-ZbxTask -Type DiagnosticInformation -Request @{ historycache = @{ stats = 'extend' } } | Select-Object TaskId, Type
#endregion
