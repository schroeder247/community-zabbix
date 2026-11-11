# This Source Code Form is subject to the terms of the Mozilla Public
# License, v. 2.0. Copyright (c) 2026 schroeder247. SPDX-License-Identifier: MPL-2.0
Start-ZbxPassiveProxy -Name 'dmz-proxy' -AllowedPeer '127.0.0.1' -Version '7.0' -Port 30160

<#
.SYNOPSIS
Registers a scriptblock that answers zabbix.stats requests to a passive proxy session.
.DESCRIPTION
The Register-ZbxPassiveProxyStatsProvider cmdlet sets how the passive proxy session named in -Name answers a
zabbix.stats request, the internal statistics a server's zabbix[stats,...] item asks a proxy for. The session keeps no
counters of its own, so the scriptblock runs on the session's runspace with the request's JSON text and returns a
hashtable that becomes the answer's data. It writes the session only with -PassThru.
#>
#region Answer with a few statistics
# The hashtable's keys and values are the answer's data, as the requesting item reads them.
Register-ZbxPassiveProxyStatsProvider -Name 'dmz-proxy' -ScriptBlock {
    param($request)
    @{ version = '7.0.0'; started = [DateTimeOffset]::UtcNow.ToUnixTimeSeconds() }
}
#endregion

Stop-ZbxPassiveProxy -Name 'dmz-proxy'
