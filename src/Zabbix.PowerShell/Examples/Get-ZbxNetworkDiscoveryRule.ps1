# This Source Code Form is subject to the terms of the Mozilla Public
# License, v. 2.0. Copyright (c) 2026 schroeder247. SPDX-License-Identifier: MPL-2.0
# Vocabulary: Office printers
$null = New-ZbxNetworkDiscoveryRule -Name 'Local network' -IPRange '192.0.2.1-254' -Status Disabled `
    -Check (New-ZbxNetworkDiscoveryCheck -Type Icmp), (New-ZbxNetworkDiscoveryCheck -Type ZabbixAgent -Key 'system.hostname' -Unique)
$null = New-ZbxNetworkDiscoveryRule -Name 'Office printers' -IPRange '192.0.2.100-120' -Status Disabled `
    -Check (New-ZbxNetworkDiscoveryCheck -Type SnmpV2c -SnmpOid '1.3.6.1.2.1.1.1.0' -SnmpCommunity 'public' -Port 161)

#region Get a discovery rule by name
# -Name matches the rule's name exactly; the result is a ZabbixNetworkDiscoveryRule record.
Get-ZbxNetworkDiscoveryRule -Name 'Local network' | Select-Object NetworkDiscoveryRuleId, Name, IPRange, Delay, Status
#endregion

#region Get a rule with its checks
# -SelectNetworkDiscoveryChecks adds the rule's checks (dchecks) as Checks.
Get-ZbxNetworkDiscoveryRule -Name 'Local network' -SelectNetworkDiscoveryChecks |
    ForEach-Object Checks | Select-Object NetworkDiscoveryCheckId, Type, Key, Unique
#endregion

#region Find rules by part of their range
# -Search matches substrings of wire fields (here iprange); several rules come back in one call.
Get-ZbxNetworkDiscoveryRule -Search @{ iprange = '192.0.2.' } | Where-Object Name -in 'Local network', 'Office printers' |
    Select-Object Name, IPRange
#endregion

#region List the devices a rule has discovered
# -SelectDHosts adds the discovered hosts (dhosts); a rule that has not run yet, as here, has none.
Get-ZbxNetworkDiscoveryRule -Name 'Local network' -SelectDHosts |
    Select-Object Name, @{ n = 'Discovered'; e = { @($_.DiscoveredHosts).Count } }
#endregion
