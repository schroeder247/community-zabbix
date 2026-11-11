# This Source Code Form is subject to the terms of the Mozilla Public
# License, v. 2.0. Copyright (c) 2026 schroeder247. SPDX-License-Identifier: MPL-2.0
<#
.SYNOPSIS
Sends one page of history values as an active proxy.
.DESCRIPTION
The Send-ZbxProxyData cmdlet sends the history values in -History to the server's trapper once, as the active proxy
named in -ProxyName, with no session running, and writes the server's answer. The server takes values only for hosts
that the proxy monitors, and only after its configuration cache has loaded the proxy. Start-ZbxActiveProxy keeps a
proxy session running instead. -WhatIf shows what would be sent.
#>
# proxy-01 (active) monitors web-01. The server answers a proxy only once its configuration cache has loaded it (up to
# CacheUpdateFrequency, 60 s by default), and a cache that still holds a deleted proxy-01 or web-01 of the same name
# answers with their stale ids: wait until a pull returns this web-01's own id. web-01 moves onto proxy-01 only once
# the server has answered a pull for it: a host assigned in the call that creates the proxy can reach the cache before
# the proxy does, and then stays out of the proxy's configuration.
$null = New-ZbxProxy -Name 'proxy-01' -OperatingMode Active
$web = Get-ZbxHost -Name 'web-01'
Start-ZbxActiveProxy -Name 'cache-wait' -ProxyName 'proxy-01' -Server 'zabbix.example:10051' -RefreshInterval '1s'
$deadline = [DateTime]::UtcNow.AddSeconds(120)
$assigned = $false
$config = $null
while ($true) {
    if ((Get-ZbxActiveProxyStatus -Name 'cache-wait' -WarningAction SilentlyContinue).LastPulledAt) {
        if (-not $assigned) { Set-ZbxProxy 'proxy-01' -Host 'web-01'; $assigned = $true }
        $config = Receive-ZbxProxyConfig -ProxyName 'proxy-01' -Server 'zabbix.example:10051'
        if ($config.HostsByName.ContainsKey('web-01') -and "$($config.HostsByName['web-01'].HostId)" -eq "$($web.HostId)") { break }
    }
    if ([DateTime]::UtcNow -gt $deadline) {
        $last = if ($config) { "$($config.Hosts.Count) host(s) in the last pull" } else { 'no pull answered' }
        throw "the server did not load proxy-01 in time ($last)"
    }
    Start-Sleep -Seconds 1
}
Stop-ZbxActiveProxy -Name 'cache-wait' -WarningAction SilentlyContinue

#region Push one value as an active proxy
# One 'proxy data' request on its own connection, no session. The server takes values only for items of hosts the
# proxy monitors. Zabbix 7.0 answers 'failed' (current proxy version is not supported): this cmdlet announces 7.4.0.
$item = Get-ZbxItem -Host 'web-01' | Where-Object Key -eq 'app.requests'
$entry = New-ZbxHistoryDataEntry -ItemId $item.ItemId -Value 42
Send-ZbxProxyData -ProxyName 'proxy-01' -Server 'zabbix.example:10051' -History $entry | Select-Object Response, Info, Upload
#endregion

#region Push several values through the pipeline
# The piped entries are gathered and sent as one page in one request. -Clock stamps a value in the past (Unix
# seconds); Get-ZbxHistory reads the values back once the server has written them.
$item = Get-ZbxItem -Host 'web-01' | Where-Object Key -eq 'app.requests'
$start = [DateTimeOffset]::UtcNow.AddMinutes(-3).ToUnixTimeSeconds()
0..2 | ForEach-Object { New-ZbxHistoryDataEntry -ItemId $item.ItemId -Value (10 * $_) -Clock ($start + 60 * $_) } |
    Send-ZbxProxyData -ProxyName 'proxy-01' -Server 'zabbix.example:10051' | Out-Null
Start-Sleep -Seconds 5
Get-ZbxHistory -Id $item.ItemId -SortField clock | Select-Object Clock, Value
#endregion

#region Say that more is held behind this page
# -More tells the server another page follows at once; -ProxyDelay is how old, in seconds, the oldest value still
# held is. Both are what a proxy that fell behind reports; the response's Upload says whether the server wants more.
$item = Get-ZbxItem -Host 'web-01' | Where-Object Key -eq 'app.requests'
$page = 1..3 | ForEach-Object { New-ZbxHistoryDataEntry -ItemId $item.ItemId -Value $_ }
Send-ZbxProxyData -ProxyName 'proxy-01' -Server 'zabbix.example:10051' -History $page -More -ProxyDelay 120 |
    Select-Object Response, Upload
#endregion
