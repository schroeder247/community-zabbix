# This Source Code Form is subject to the terms of the Mozilla Public
# License, v. 2.0. Copyright (c) 2026 schroeder247. SPDX-License-Identifier: MPL-2.0
$null = New-ZbxProxy -Name 'proxy-01' -OperatingMode Active

#region Delete a proxy by name
# -InputObject takes a name, an id or a proxy object. ConfirmImpact is High, so PowerShell asks first unless
# -Confirm:$false is given.
Remove-ZbxProxy -InputObject 'proxy-01' -Confirm:$false
#endregion

$null = New-ZbxProxy -Name 'proxy-01' -OperatingMode Active

#region Delete the proxies a search finds, through the pipeline
# Get-ZbxProxy writes proxy objects; Remove-ZbxProxy binds each one and deletes them in one proxy.delete call.
# Run it with -WhatIf first to see what would go.
Get-ZbxProxy -Name 'proxy-01' | Remove-ZbxProxy -WhatIf
Get-ZbxProxy -Name 'proxy-01' | Remove-ZbxProxy -Confirm:$false
#endregion

$null = New-ZbxProxy -Name 'proxy-01' -OperatingMode Active -Host 'web-01'

#region Delete a proxy that still monitors hosts
# The API refuses to delete a proxy that monitors hosts, so hand them back to the server first.
$proxy = Get-ZbxProxy -Name 'proxy-01' -SelectHosts
if ($proxy.Hosts) { Set-ZbxHost -InputObject $proxy.Hosts.HostId -MonitoredBy Server | Out-Null }
Remove-ZbxProxy -InputObject $proxy -Confirm:$false
Get-ZbxHost -Name 'web-01' | Select-Object HostName, MonitoredBy
#endregion
