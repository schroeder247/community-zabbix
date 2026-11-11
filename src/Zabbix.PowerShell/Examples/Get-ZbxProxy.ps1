# This Source Code Form is subject to the terms of the Mozilla Public
# License, v. 2.0. Copyright (c) 2026 schroeder247. SPDX-License-Identifier: MPL-2.0
# Vocabulary: proxy-02; proxy-
$null = New-ZbxProxy -Name 'proxy-01' -OperatingMode Active -Host 'web-01'
$null = New-ZbxProxy -Name 'proxy-02' -OperatingMode Passive -Address '10.0.0.40' -Port '10051'

#region Get a proxy by name
# -Name matches the proxy's name exactly. State, Version and LastAccess are what the server last heard from it.
Get-ZbxProxy -Name 'proxy-01' | Select-Object ProxyId, Name, OperatingMode, State, LastAccess
#endregion

#region Find proxies with a wildcard
# A * in -Name turns the exact match into a search.
Get-ZbxProxy -Name 'proxy-*' | Select-Object Name, OperatingMode, Address, Port
#endregion

#region List the hosts each proxy monitors
# -SelectHosts adds the hosts to each proxy; they are host records (HostName is the host's technical name).
Get-ZbxProxy -Name 'proxy-*' -SelectHosts |
    Select-Object Name, @{ n = 'Hosts'; e = { $_.Hosts.HostName -join ', ' } }
#endregion

#region Count proxies without reading them
# -Search matches substrings of wire fields (here name); -CountOutput returns the number only (proxy.get's
# countOutput).
Get-ZbxProxy -Search @{ name = 'proxy-' } -CountOutput
#endregion
