# This Source Code Form is subject to the terms of the Mozilla Public
# License, v. 2.0. Copyright (c) 2026 schroeder247. SPDX-License-Identifier: MPL-2.0
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

#region Pull a proxy's configuration once
# Asks the server's trapper for proxy-01's configuration, as the proxy itself would ('proxy config'), with no
# session left running. The proxy must exist on the server in active mode (New-ZbxProxy -OperatingMode Active).
Receive-ZbxProxyConfig -ProxyName 'proxy-01' -Server 'zabbix.example:10051' |
    Select-Object ConfigRevision, @{ n = 'Hosts'; e = { $_.Hosts.Count } }, @{ n = 'Items'; e = { $_.Items.Count } }
#endregion

#region List the items the proxy is to collect
# Hosts, Items and their ...ByName/...ByKey indexes are read-only dictionaries; .Values are the records.
$config = Receive-ZbxProxyConfig -ProxyName 'proxy-01' -Server 'zabbix.example:10051'
$config.Items.Values | Select-Object ItemId, HostId, Key | Sort-Object Key
#endregion

#region Pull only what changed since the last pull
# -Held (or the pipeline) takes the configuration already held: the server sends the difference since its
# ConfigRevision and the result is the merged configuration, or the same one when nothing changed.
$config = Receive-ZbxProxyConfig -ProxyName 'proxy-01' -Server 'zabbix.example:10051'
$config | Receive-ZbxProxyConfig -ProxyName 'proxy-01' -Server 'zabbix.example:10051' | Select-Object ConfigRevision
#endregion
