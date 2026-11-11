# This Source Code Form is subject to the terms of the Mozilla Public
# License, v. 2.0. Copyright (c) 2026 schroeder247. SPDX-License-Identifier: MPL-2.0
$null = New-ZbxProxy -Name 'proxy-01' -OperatingMode Active
Start-ZbxActiveProxy -Name 'uplink' -ProxyName 'proxy-01' -Server 'zabbix.example:10051' -RefreshInterval '2s'
Start-Sleep -Seconds 3

<#
.SYNOPSIS
Gets the recent log entries of a running active proxy session.
.DESCRIPTION
The Get-ZbxActiveProxyLog cmdlet writes the log entries the active proxy session named in -Name keeps in its own bounded
buffer: each configuration pull, each upload and what went wrong, with a timestamp, a level and a category. The session
runs in the background, so its log is the place to see why the server does not take its data. -Tail keeps the last
entries and -Level the entries at or above a level; Clear-ZbxActiveProxyLog empties the buffer.
#>
#region Read the last log entries of a proxy session
# Each entry has a Timestamp, a Level, a Category and the Message.
Get-ZbxActiveProxyLog -Name 'uplink' -Tail 10 | Select-Object Timestamp, Level, Message
#endregion

#region Show only warnings and errors
# -Level keeps the entries at or above the level given.
Get-ZbxActiveProxyLog -Name 'uplink' -Level Warning
#endregion

Stop-ZbxActiveProxy -Name 'uplink'
