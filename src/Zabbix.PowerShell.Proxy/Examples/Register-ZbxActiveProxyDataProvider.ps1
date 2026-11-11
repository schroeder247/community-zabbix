# This Source Code Form is subject to the terms of the Mozilla Public
# License, v. 2.0. Copyright (c) 2026 schroeder247. SPDX-License-Identifier: MPL-2.0
<#
.SYNOPSIS
Registers a scriptblock that supplies values for each upload of a running active proxy session.
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
Start-ZbxActiveProxy -Name 'uplink' -ProxyName 'proxy-01' -Server 'zabbix.example:10051' -RefreshInterval '2s'

#region Push a value on every refresh
# Before each refresh's upload the session runs the scriptblock on its own runspace (module commands are not there,
# so it builds SDK records directly), passing the configuration last pulled; a ZabbixProxyDataPage it returns is sent.
Register-ZbxActiveProxyDataProvider -Name 'uplink' -ScriptBlock {
    param($config)
    $item = $config.Items.Values | Where-Object Key -eq 'app.requests'
    $now = [DateTimeOffset]::UtcNow
    $value = [Community.Zabbix.Proxy.ZabbixProxyHistoryItem]::new($now.ToUnixTimeMilliseconds(), $item.ItemId, $now, '42')
    [Community.Zabbix.Proxy.ZabbixProxyDataPage]::new([Community.Zabbix.Proxy.ZabbixProxyHistoryItem[]]@($value), $null, $null, $null, $null)
}
Start-Sleep -Seconds 5
Get-ZbxItem -Host 'web-01' | Where-Object Key -eq 'app.requests' | Select-Object Key, LastValue
#endregion

#region Push only when there is something to send
# Returning nothing skips the tick. Several providers may be registered: each is asked every tick and their pages
# are merged into one upload.
Register-ZbxActiveProxyDataProvider -Name 'uplink' -ScriptBlock {
    param($config)
    $item = $config.Items.Values | Where-Object Key -eq 'app.status'
    if (-not $item) { return }
    $now = [DateTimeOffset]::UtcNow
    $value = [Community.Zabbix.Proxy.ZabbixProxyHistoryItem]::new($now.ToUnixTimeMilliseconds() + 1, $item.ItemId, $now, 'ok')
    [Community.Zabbix.Proxy.ZabbixProxyDataPage]::new([Community.Zabbix.Proxy.ZabbixProxyHistoryItem[]]@($value), $null, $null, $null, $null)
}
Start-Sleep -Seconds 5
Get-ZbxItem -Host 'web-01' | Where-Object Key -eq 'app.status' | Select-Object Key, LastValue
#endregion

Stop-ZbxActiveProxy -Name 'uplink' -WarningAction SilentlyContinue
