# This Source Code Form is subject to the terms of the Mozilla Public
# License, v. 2.0. Copyright (c) 2026 schroeder247. SPDX-License-Identifier: MPL-2.0
# Vocabulary: App endpoints; Mounted filesystems
$web = Get-ZbxHost -Name 'web-01'
$null = New-ZbxDiscoveryRule -HostId $web.HostId -Name 'App endpoints' -Key 'app.endpoints.discovery' -Type Trapper
$null = New-ZbxDiscoveryRule -HostId $web.HostId -Name 'Mounted filesystems' -Key 'vfs.fs.discovery' -Type ZabbixActive -Delay '1h'

#region Get a host's discovery rules
# -Host takes host names, ids or objects; the result is one ZabbixDiscoveryRule record per rule.
Get-ZbxDiscoveryRule -Host 'web-01' | Select-Object ItemId, Name, Key, Type, Delay
#endregion

#region Get a rule by name
# -Name matches the rule's name exactly; combine it with -Host when several hosts have a rule of that name.
Get-ZbxDiscoveryRule -Name 'App endpoints' -Host 'web-01' | Select-Object ItemId, Name, Key
#endregion

#region Read the rules a template brings
# -Template takes template names; built-in templates may be read like any other.
Get-ZbxDiscoveryRule -Template 'Linux by Zabbix agent' | Select-Object Name, Key, Delay
#endregion

#region Get the rules of the hosts a search finds, through the pipeline
# Get-ZbxHost writes host objects; Get-ZbxDiscoveryRule binds each one to -Host. -SelectHosts adds the owning host.
Get-ZbxHost -Name 'web-01' | Get-ZbxDiscoveryRule -SelectHosts |
    Select-Object @{ n = 'Host'; e = { $_.Hosts.HostName } }, Name, Key
#endregion
