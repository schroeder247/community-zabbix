# This Source Code Form is subject to the terms of the Mozilla Public
# License, v. 2.0. Copyright (c) 2026 schroeder247. SPDX-License-Identifier: MPL-2.0
# Vocabulary: Office printers
#region Create a discovery rule that pings a range
# -IpRange takes ranges, CIDR blocks and lists; -Delay is how often the server scans (1h by default). The rule is
# created disabled so nothing is scanned yet; the created rule is read back and written.
New-ZbxNetworkDiscoveryRule -Name 'Local network' -IpRange '192.0.2.1-254' -Delay '1h' -Status Disabled `
    -Check (New-ZbxNetworkDiscoveryCheck -Type Icmp) |
    Select-Object NetworkDiscoveryRuleId, Name, IpRange, Delay, Status
#endregion

Remove-ZbxNetworkDiscoveryRule -InputObject 'Local network' -Confirm:$false

#region Create a rule with several checks
# Each check is one kind of probe; -Unique on one of them decides when two devices are the same host. -ConcurrencyMax
# limits how many checks run at once (0: unlimited).
$checks = @(
    New-ZbxNetworkDiscoveryCheck -Type Icmp
    New-ZbxNetworkDiscoveryCheck -Type ZabbixAgent -Key 'system.hostname' -Port 10050 -Unique
    New-ZbxNetworkDiscoveryCheck -Type Tcp -Ports '22,443'
)
New-ZbxNetworkDiscoveryRule -Name 'Local network' -IpRange '192.0.2.0/24' -Check $checks -ConcurrencyMax 10 -Status Disabled |
    Out-Null
Get-ZbxNetworkDiscoveryRule -Name 'Local network' -SelectNetworkDiscoveryChecks |
    ForEach-Object Checks | Select-Object Type, Key, Ports, Unique
#endregion

#region Let a proxy run the discovery
# -ProxyId takes the proxy's id: the proxy scans its own network and reports what it finds to the server.
$proxy = New-ZbxProxy -Name 'proxy-01' -OperatingMode Active
New-ZbxNetworkDiscoveryRule -Name 'Office printers' -IpRange '192.0.2.100-120' -ProxyId $proxy.ProxyId -Status Disabled `
    -Check (New-ZbxNetworkDiscoveryCheck -Type SnmpV2c -SnmpOid '1.3.6.1.2.1.1.1.0' -SnmpCommunity '{$SNMP_COMMUNITY}' -Port 161) |
    Select-Object Name, IpRange, ProxyId
#endregion
