# This Source Code Form is subject to the terms of the Mozilla Public
# License, v. 2.0. Copyright (c) 2026 schroeder247. SPDX-License-Identifier: MPL-2.0
# Vocabulary: proxy-02
#region Create an active proxy
# An active proxy connects to the server itself, so only its name is needed: it must match the proxy's Hostname=
# setting. The created proxy is read back and written.
New-ZbxProxy -Name 'proxy-01' -OperatingMode Active | Select-Object ProxyId, Name, OperatingMode
#endregion

#region Create a passive proxy the server polls
# A passive proxy is polled by the server at -Address/-Port (strings, as the API has them).
New-ZbxProxy -Name 'proxy-02' -OperatingMode Passive -Address '10.0.0.40' -Port '10051' -Description 'DMZ collector' |
    Select-Object Name, OperatingMode, Address, Port, Description
#endregion

Remove-ZbxProxy -InputObject 'proxy-01', 'proxy-02' -Confirm:$false

#region Create a proxy and hand it hosts
# -Host takes host names, ids or objects: those hosts are monitored by this proxy from now on (their MonitoredBy
# becomes Proxy). -AllowedAddresses limits where an active proxy may connect from.
New-ZbxProxy -Name 'proxy-01' -OperatingMode Active -AllowedAddresses '10.0.0.0/24' -Host 'web-01', 'db-01' | Out-Null
Get-ZbxHost -Name 'web-01', 'db-01' | Select-Object TechnicalName, MonitoredBy, ProxyId
#endregion

Set-ZbxHost -InputObject 'web-01', 'db-01' -MonitoredBy Server | Out-Null
Remove-ZbxProxy -InputObject 'proxy-01' -Confirm:$false

#region Create an active proxy that must use TLS with a pre-shared key
# -TlsAccept is what the server accepts from an active proxy; the PSK and its identity are SecureStrings and are
# never read back.
$identity = ConvertTo-SecureString 'proxy-01-psk' -AsPlainText -Force
$psk = ConvertTo-SecureString '1f87b595725ac58dd977beef14b97461a7c1045b9a1c963065002c5473194952' -AsPlainText -Force
New-ZbxProxy -Name 'proxy-01' -OperatingMode Active -TlsAccept Psk -TlsPskIdentity $identity -TlsPsk $psk |
    Select-Object Name, TlsAccept
#endregion
