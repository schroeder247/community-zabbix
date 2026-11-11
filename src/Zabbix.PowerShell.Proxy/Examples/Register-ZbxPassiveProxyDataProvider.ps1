# This Source Code Form is subject to the terms of the Mozilla Public
# License, v. 2.0. Copyright (c) 2026 schroeder247. SPDX-License-Identifier: MPL-2.0
Start-ZbxPassiveProxy -Name 'dmz-proxy' -AllowedPeer '127.0.0.1' -Version '7.0' -Port 30160

<#
.SYNOPSIS
Registers a scriptblock that supplies values each time the server pulls a passive proxy session's data.
.DESCRIPTION
The Register-ZbxPassiveProxyDataProvider cmdlet adds a data provider to the passive proxy session named in -Name:
before each proxy data pull, the session runs the scriptblock on its own runspace with the configuration it holds, and a
ZabbixProxyDataPage the scriptblock returns joins the values the server takes. Returning nothing adds nothing. Several
providers may be registered, and their pages are merged. It writes the session only with -PassThru.
#>
#region Supply a value with every pull
# The provider finds the item in the configuration and returns one value for it.
Register-ZbxPassiveProxyDataProvider -Name 'dmz-proxy' -ScriptBlock {
    param($config)
    $item = $config.Items.Values | Where-Object Key -eq 'app.requests'
    if (-not $item) { return }
    $now = [DateTimeOffset]::UtcNow
    $value = [Community.Zabbix.Proxy.ZabbixProxyHistoryItem]::new($now.ToUnixTimeMilliseconds(), $item.ItemId, $now, '42')
    [Community.Zabbix.Proxy.ZabbixProxyDataPage]::new([Community.Zabbix.Proxy.ZabbixProxyHistoryItem[]]@($value), $null, $null, $null, $null)
}
#endregion

Stop-ZbxPassiveProxy -Name 'dmz-proxy'
