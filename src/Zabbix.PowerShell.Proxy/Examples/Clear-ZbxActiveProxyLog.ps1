# This Source Code Form is subject to the terms of the Mozilla Public
# License, v. 2.0. Copyright (c) 2026 schroeder247. SPDX-License-Identifier: MPL-2.0
$null = New-ZbxProxy -Name 'proxy-01' -OperatingMode Active
Start-ZbxActiveProxy -Name 'uplink' -ProxyName 'proxy-01' -Server 'zabbix.example:10051' -RefreshInterval '2s'
Start-Sleep -Seconds 3

<#
.SYNOPSIS
Empties the log buffer of a running active proxy session.
.DESCRIPTION
The Clear-ZbxActiveProxyLog cmdlet deletes every entry the active proxy session named in -Name keeps in its log buffer,
so the next Get-ZbxActiveProxyLog shows only what happens from now on. The session keeps running and logging. -WhatIf
shows which session it would clear. It writes nothing.
#>
#region Start the log afresh
# After the clear, the log holds only the pulls and uploads that follow.
Clear-ZbxActiveProxyLog -Name 'uplink'
Start-Sleep -Seconds 3
Get-ZbxActiveProxyLog -Name 'uplink' | Select-Object Timestamp, Message
#endregion

Stop-ZbxActiveProxy -Name 'uplink'
