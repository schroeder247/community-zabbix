# This Source Code Form is subject to the terms of the Mozilla Public
# License, v. 2.0. Copyright (c) 2026 schroeder247. SPDX-License-Identifier: MPL-2.0
$null = New-ZbxProxy -Name 'proxy-01' -OperatingMode Passive -Address '10.0.0.40' -Port '10051' -Host 'db-01'

<#
.SYNOPSIS
Finds the trapper a host's values must be sent to.
.DESCRIPTION
The Resolve-ZbxTrapperEndpoint cmdlet says where values for each host piped in must go: for a host monitored by a proxy,
the proxy's own address and port; otherwise the server's trapper, the one given to Connect-ZbxServer -Trapper or else the
active high-availability node's. It is the rule the Zabbix frontend uses, and never guesses a port. It writes one object
per host with HostName, HostId, Server, Address and Port, and sends nothing to the trapper itself.
#>
#region Find where to send a host's values
# The server monitors web-01 itself, so the answer is the server's trapper.
Get-ZbxHost -Name 'web-01' | Resolve-ZbxTrapperEndpoint
#endregion

#region Find the trappers of a group's hosts
# The proxy proxy-01 monitors db-01, so its values go to the proxy's address.
Get-ZbxHost -Group 'Linux servers' | Resolve-ZbxTrapperEndpoint | Select-Object HostName, Address, Port
#endregion
