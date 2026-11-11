# This Source Code Form is subject to the terms of the Mozilla Public
# License, v. 2.0. Copyright (c) 2026 schroeder247. SPDX-License-Identifier: MPL-2.0
<#
.SYNOPSIS
Sends values to a server's or proxy's trapper, as zabbix_sender does.
.DESCRIPTION
The Send-ZbxData cmdlet sends values to trapper items over the sender protocol zabbix_sender uses: one value from
-HostName, -Key and -Value, or a batch piped by property name (any object with HostName or Host, Key and Value, and
optionally Clock and Ns), collected into one request. The trapper is -Server, else the connection's
(Connect-ZbxServer -Trapper), else the active high-availability node's. With -Server no connection is needed, because
the sender protocol carries no login. It writes the trapper's answer: how many values it processed and how many
failed.
#>
# Wait until the server's configuration cache has loaded this web-01 (up to CacheUpdateFrequency, 60 s by default); a
# cache that still holds a deleted web-01 of the same name accepts values for it too, so check the value reached this probe item.
# Zabbix 8.0 limits new trapper items to {$TRAPPER.ALLOWED_HOSTS} (loopback); these accept every sender.
Get-ZbxItem -Host 'web-01' | Where-Object Type -eq 'Trapper' | Set-ZbxItem -TrapperHosts '0.0.0.0/0,::/0' | Out-Null
$web = Get-ZbxHost -Name 'web-01'
# web-01 takes values unencrypted or over its pre-shared key, from a key file as Zabbix's TLSPSKFile holds one; the
# wait below also lets the configuration cache load the key.
Set-Location (New-Item -ItemType Directory -Path (Join-Path ([IO.Path]::GetTempPath()) ([guid]::NewGuid().ToString('N'))))
$hex = -join (1..32 | ForEach-Object { '{0:x2}' -f (Get-Random -Maximum 256) })
Set-Content -Path 'web-01.psk' -Value $hex
$web | Set-ZbxHost -TlsAccept Unencrypted, Psk -TlsPskIdentity 'web-01' -TlsPsk (ConvertTo-SecureString $hex -AsPlainText -Force)
$probe = New-ZbxItem -HostId $web.HostId -Name 'Cache probe' -Key 'app.probe' -Type Trapper -ValueType Unsigned -TrapperHosts '0.0.0.0/0,::/0'
$deadline = [DateTime]::UtcNow.AddSeconds(120)
do {
    if ([DateTime]::UtcNow -gt $deadline) { throw 'the server did not load web-01 in time' }
    $token = Get-Random -Minimum 1 -Maximum 1000000
    $null = Send-ZbxData -HostName 'web-01' -Key 'app.probe' -Value $token -Server 'zabbix.example:10051' -WarningAction SilentlyContinue
    Start-Sleep -Seconds 1
} until ((Get-ZbxItem -Id $probe.ItemId).LastValue -eq "$token")

#region Send one value to a trapper item
# The sender protocol (zabbix_sender's), to the trapper on -Server: the server's or a proxy's, host:port. Without
# -Server the trapper is Connect-ZbxServer -Trapper's, else the active HA node's (hanode.get), never a guessed port.
# Processed/Failed count values.
Send-ZbxData -HostName 'web-01' -Key 'app.status' -Value 'ok' -Server 'zabbix.example:10051'
#endregion

#region Send to an HA pair, with no API connection
# With -Server no connection is needed: the sender protocol carries no credentials. Several nodes are one HA cluster,
# tried in order, so the standby is asked only when the first node cannot be reached.
Send-ZbxData -HostName 'web-01' -Key 'app.status' -Value 'ok' -Server 'zabbix.example:10051', 'zabbix-standby.example:10051'
#endregion

#region Send a value and read it back
# The server writes the value to history within a second or two; Get-ZbxHistory reads the latest values through the
# API (history.get, newest first).
Send-ZbxData -HostName 'web-01' -Key 'app.requests' -Value 57 -Server 'zabbix.example:10051' | Out-Null
Start-Sleep -Seconds 5
Get-ZbxItem -Host 'web-01' | Where-Object Key -eq 'app.requests' |
    Get-ZbxHistory -SortField clock -SortOrder Descending | Select-Object -First 3 Clock, Value
#endregion

#region Send a batch through the pipeline
# Objects with Host (or HostName), Key and Value properties (Clock and Ns optional) bind by property name and go to
# the trapper as one request. A value the server rejects is counted in Failed and reported as a warning.
@(
    [pscustomobject]@{ Host = 'web-01'; Key = 'app.requests'; Value = 12 }
    [pscustomobject]@{ Host = 'web-01'; Key = 'app.status'; Value = 'ok' }
) | Send-ZbxData -Server 'zabbix.example:10051' | Select-Object Processed, Failed, Total
#endregion

#region Send a value with its own timestamp
# -Clock (Unix seconds) and -Ns stamp the value as collected earlier; the server keeps it at that time.
$collected = [DateTimeOffset]::UtcNow.AddMinutes(-2)
Send-ZbxData -HostName 'web-01' -Key 'app.requests' -Value 30 -Clock $collected.ToUnixTimeSeconds() -Server 'zabbix.example:10051' |
    Select-Object Processed, Failed
#endregion

#region Send over TLS with a pre-shared key
# A host whose tls_accept takes psk is sent to over TLS with its identity and key, as zabbix_sender --tls-connect psk
# does. -Psk takes a SecureString or the path of a key file; -Tls is needed only when a key and a certificate are both
# given. Without TLS parameters Send-ZbxData uses Connect-ZbxServer's when it sends to the connection's trapper.
Send-ZbxData -HostName 'web-01' -Key 'app.status' -Value 'ok' -Server 'zabbix.example:10051' -PskIdentity 'web-01' -Psk ./web-01.psk
#endregion
