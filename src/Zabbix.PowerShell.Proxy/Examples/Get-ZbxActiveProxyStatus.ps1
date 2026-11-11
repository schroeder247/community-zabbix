# This Source Code Form is subject to the terms of the Mozilla Public
# License, v. 2.0. Copyright (c) 2026 schroeder247. SPDX-License-Identifier: MPL-2.0
$null = New-ZbxProxy -Name 'proxy-01' -OperatingMode Active
Start-ZbxActiveProxy -Name 'uplink' -ProxyName 'proxy-01' -Server 'zabbix.example:10051' -RefreshInterval '2s'
Start-Sleep -Seconds 3

<#
.SYNOPSIS
Gets the running active proxy sessions.
.DESCRIPTION
The Get-ZbxActiveProxyStatus cmdlet writes the active proxy sessions running in this runspace, or the one named in
-Name: each with its name, the proxy it acts as, when it started, when it last pulled the configuration, and the
configuration it holds. It reads only the runspace's own sessions and sends nothing. Start-ZbxActiveProxy starts a
session and Stop-ZbxActiveProxy ends it.
#>
#region List the running active proxy sessions
# Every active proxy session of this runspace.
Get-ZbxActiveProxyStatus | Select-Object Name, Started, LastPulledAt
#endregion

#region Check when a session last pulled its configuration
# LastPulledAt is empty until the server has answered a pull.
(Get-ZbxActiveProxyStatus -Name 'uplink').LastPulledAt
#endregion

Stop-ZbxActiveProxy -Name 'uplink'
