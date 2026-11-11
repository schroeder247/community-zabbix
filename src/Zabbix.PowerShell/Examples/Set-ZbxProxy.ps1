# This Source Code Form is subject to the terms of the Mozilla Public
# License, v. 2.0. Copyright (c) 2026 schroeder247. SPDX-License-Identifier: MPL-2.0
$null = New-ZbxProxy -Name 'proxy-01' -OperatingMode Active

#region Change a proxy's description
# -InputObject (position 0) takes a proxy name, id or object; a parameter not given is left unchanged. Set-ZbxProxy
# writes nothing by itself; -PassThru reads the changed proxy back and writes it.
Set-ZbxProxy 'proxy-01' -Description 'Branch office collector' -PassThru | Select-Object Name, Description
#endregion

#region Change the proxies a search finds, through the pipeline
# Get-ZbxProxy writes proxy objects and Set-ZbxProxy binds each one (proxy.update, one call).
Get-ZbxProxy -Name 'proxy-01' | Set-ZbxProxy -AllowedAddresses '10.0.0.0/24' -PassThru | Select-Object Name, AllowedAddresses
#endregion

#region Move hosts onto the proxy
# -Host replaces the list of hosts the proxy monitors; the hosts' MonitoredBy becomes Proxy.
Set-ZbxProxy 'proxy-01' -Host 'web-01', 'db-01'
Get-ZbxHost -Name 'web-01', 'db-01' | Select-Object HostName, MonitoredBy
#endregion

#region Turn an active proxy into a passive one
# A passive proxy needs the address and port the server polls; -WhatIf would show the change without making it.
Set-ZbxProxy 'proxy-01' -OperatingMode Passive -Address '10.0.0.41' -Port '10051' -PassThru |
    Select-Object Name, OperatingMode, Address, Port
#endregion
