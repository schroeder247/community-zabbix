# This Source Code Form is subject to the terms of the Mozilla Public
# License, v. 2.0. Copyright (c) 2026 schroeder247. SPDX-License-Identifier: MPL-2.0
Start-ZbxPassiveProxy -Name 'dmz-proxy' -AllowedPeer '127.0.0.1' -Version '7.0' -Port 30160

<#
.SYNOPSIS
Queues a value in a running passive proxy session, for the server to pull.
.DESCRIPTION
The Send-ZbxPassiveProxyHistory cmdlet adds a value for the item -Key of the host -HostName to the store of the passive
proxy session named in -Name; the server takes it with the next proxy data it pulls. The host must be one the server's
configuration gives the proxy. Objects with HostName (or Host), Key, Value and, optionally, Clock bind by property name,
so a batch can be piped in. -WhatIf shows what would be queued.
#>
#region Queue one value
# The value waits in the session's store until the server pulls it.
Send-ZbxPassiveProxyHistory -Name 'dmz-proxy' -HostName 'web-01' -Key 'app.requests' -Value 42
#endregion

#region Queue a series with past clocks
# Each object binds by property name; -Clock takes a date, Unix seconds or a clock.
$start = [DateTimeOffset]::UtcNow.AddMinutes(-3).ToUnixTimeSeconds()
0..2 | ForEach-Object { [pscustomobject]@{ Host = 'web-01'; Key = 'app.requests'; Value = 10 * $_; Clock = $start + 60 * $_ } } |
    Send-ZbxPassiveProxyHistory -Name 'dmz-proxy'
#endregion

Stop-ZbxPassiveProxy -Name 'dmz-proxy'
