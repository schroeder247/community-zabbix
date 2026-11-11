# This Source Code Form is subject to the terms of the Mozilla Public
# License, v. 2.0. Copyright (c) 2026 schroeder247. SPDX-License-Identifier: MPL-2.0
<#
.SYNOPSIS
Builds a proxy data request without sending it.
.DESCRIPTION
The New-ZbxProxyData cmdlet builds the proxy data request an active proxy sends the server: the proxy's name, a
session token, and the history values in -History, from New-ZbxHistoryValue or New-ZbxHistoryDataEntry. It writes the
request for inspection, or for a send a script makes itself; Send-ZbxProxyData builds and sends one in a single step.
Nothing is sent to the server.
#>
#region Build a proxy data request
# The 'proxy data' request an active proxy sends, built locally for inspection; nothing is sent. -Session is the
# 32-hex-digit token the server de-duplicates history on (a new GUID without dashes is one).
New-ZbxProxyData -ProxyName 'proxy-01' -Session ([guid]::NewGuid().ToString('N')) | Select-Object Request, Host, Session, Version, Clock
#endregion

#region Wrap history values in the request
# -History takes entries from New-ZbxHistoryDataEntry; the request carries them as its 'history data' array.
$item = Get-ZbxItem -Host 'web-01' | Where-Object Key -eq 'app.requests'
$history = 1..3 | ForEach-Object { New-ZbxHistoryDataEntry -ItemId $item.ItemId -Value (10 * $_) }
(New-ZbxProxyData -ProxyName 'proxy-01' -Session ([guid]::NewGuid().ToString('N')) -History $history).History |
    Select-Object Id, ItemId, Value
#endregion

#region Describe a backlog and the protocol version
# -More (another page follows at once) and -ProxyDelay (age of the oldest value still held, in seconds) are how a
# proxy that fell behind reports; -Version is the protocol version announced (7.4.0 by default).
New-ZbxProxyData -ProxyName 'proxy-01' -Session ([guid]::NewGuid().ToString('N')) -More -ProxyDelay 120 -Version '7.0.0' |
    Select-Object Host, More, ProxyDelay, Version
#endregion
