# This Source Code Form is subject to the terms of the Mozilla Public
# License, v. 2.0. Copyright (c) 2026 schroeder247. SPDX-License-Identifier: MPL-2.0
# proxy-01 (active) monitors web-01. The server answers a proxy only once its configuration cache has loaded it (up to
# CacheUpdateFrequency, 60 s by default), and a cache that still holds a deleted proxy-01 or web-01 of the same name
# answers with their stale ids: wait until a pull returns this web-01's own id.
$null = New-ZbxProxy -Name 'proxy-01' -OperatingMode Active -Host 'web-01'
$web = Get-ZbxHost -Name 'web-01'
Start-ZbxActiveProxy -Name 'cache-wait' -ProxyName 'proxy-01' -Server 'zabbix.example:10051' -RefreshInterval '1s'
$deadline = [DateTime]::UtcNow.AddSeconds(120)
while ($true) {
    if ((Get-ZbxActiveProxyStatus -Name 'cache-wait' -WarningAction SilentlyContinue).LastPulledAt) {
        $config = Receive-ZbxProxyConfig -ProxyName 'proxy-01' -Server 'zabbix.example:10051'
        if ($config.HostsByName.ContainsKey('web-01') -and "$($config.HostsByName['web-01'].HostId)" -eq "$($web.HostId)") { break }
    }
    if ([DateTime]::UtcNow -gt $deadline) { throw 'the server did not load proxy-01 in time' }
    Start-Sleep -Seconds 1
}
Stop-ZbxActiveProxy -Name 'cache-wait' -WarningAction SilentlyContinue

#region Start an active proxy's uplink
# The session pulls proxy-01's configuration from the server's trapper now and every -RefreshInterval (60 s by
# default) until Stop-ZbxActiveProxy. Version is the protocol it speaks: the server's own, adopted from its first
# refusal. ConfigRevision is 0 before the first pull; Fault holds the last failed pull's error.
Start-ZbxActiveProxy -Name 'uplink' -ProxyName 'proxy-01' -Server 'zabbix.example:10051'
Start-Sleep -Seconds 2
Get-ZbxActiveProxyStatus -Name 'uplink' | Select-Object Name, ProxyName, Version, ConfigRevision, LastPulledAt, Fault
Stop-ZbxActiveProxy -Name 'uplink'
#endregion

#region Start with a shorter refresh
# -RefreshInterval is a duration ('10s', seconds or a time span; Set-ZbxActiveProxy changes it on a running session);
# -PassThru writes the session.
# Several -Server entries are HA nodes, tried in turn.
Start-ZbxActiveProxy -Name 'uplink' -ProxyName 'proxy-01' -Server 'zabbix.example:10051' -RefreshInterval '10s' -PassThru |
    Select-Object Name, ProxyName, RefreshInterval
Stop-ZbxActiveProxy -Name 'uplink'
#endregion

#region Push values on every refresh
# The session pushes nothing by itself: a data provider (Register-ZbxActiveProxyDataProvider) returns a page of
# values before each refresh's upload, and the session sends it as proxy-01 ('proxy data').
Start-ZbxActiveProxy -Name 'uplink' -ProxyName 'proxy-01' -Server 'zabbix.example:10051' -RefreshInterval '2s'
Register-ZbxActiveProxyDataProvider -Name 'uplink' -ScriptBlock {
    param($config)
    $item = $config.Items.Values | Where-Object Key -eq 'app.requests'
    $now = [DateTimeOffset]::UtcNow
    $value = [Community.Zabbix.Proxy.ZabbixProxyHistoryItem]::new($now.ToUnixTimeMilliseconds(), $item.ItemId, $now, '42')
    [Community.Zabbix.Proxy.ZabbixProxyDataPage]::new([Community.Zabbix.Proxy.ZabbixProxyHistoryItem[]]@($value), $null, $null, $null, $null)
}
Start-Sleep -Seconds 5
Stop-ZbxActiveProxy -Name 'uplink' -WarningAction SilentlyContinue
Get-ZbxItem -Host 'web-01' | Where-Object Key -eq 'app.requests' | Select-Object Key, LastValue
#endregion
