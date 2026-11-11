# This Source Code Form is subject to the terms of the Mozilla Public
# License, v. 2.0. Copyright (c) 2026 schroeder247. SPDX-License-Identifier: MPL-2.0
<#
.SYNOPSIS
Builds a check for a network discovery rule.
.DESCRIPTION
A network discovery check is one probe a network discovery rule runs on each address: ICMP ping, a service port such
as SSH or HTTP, a Zabbix agent key or an SNMP OID. The New-ZbxNetworkDiscoveryCheck cmdlet writes it as a typed
ZabbixNetworkDiscoveryCheckRequest, which the -Check of New-ZbxNetworkDiscoveryRule and Set-ZbxNetworkDiscoveryRule
take as it is, alone or in an array. The SNMPv3 passphrases are kept but not shown by the default view. Nothing is
sent to the server.
#>
#region Build an ICMP ping check
# A check is a hashtable of the dcheck's wire fields (type, key_, ports, uniq...); nothing is sent until a discovery
# rule is created or changed with it.
New-ZbxNetworkDiscoveryCheck -Type Icmp
#endregion

#region Build an agent check that identifies the device
# -Key is the item key the agent answers; -Unique makes this check's value decide whether two discovered devices
# are the same host (uniq). -Port is the agent's port; -Ports takes a list or range instead.
New-ZbxNetworkDiscoveryCheck -Type ZabbixAgent -Key 'system.hostname' -Port 10050 -Unique
#endregion

#region Build an SNMPv2c check and a TCP port check
# An SNMP check takes -SnmpOid and -SnmpCommunity (a macro works as well as a literal); -Ports on a TCP check lists
# the ports to probe.
New-ZbxNetworkDiscoveryCheck -Type SnmpV2c -SnmpOid '1.3.6.1.2.1.1.5.0' -SnmpCommunity '{$SNMP_COMMUNITY}' -Port 161
New-ZbxNetworkDiscoveryCheck -Type Tcp -Ports '22,80,443'
#endregion

#region Create a discovery rule from the checks
# New-ZbxNetworkDiscoveryRule -Check binds the hashtables directly, alone or in an array. The rule is created
# disabled here, so the server does not scan the range yet.
$checks = @(
    New-ZbxNetworkDiscoveryCheck -Type Icmp
    New-ZbxNetworkDiscoveryCheck -Type ZabbixAgent -Key 'system.hostname' -Port 10050 -Unique
)
New-ZbxNetworkDiscoveryRule -Name 'Local network' -IPRange '192.0.2.1-254' -Check $checks -Status Disabled | Out-Null
Get-ZbxNetworkDiscoveryRule -Name 'Local network' -SelectNetworkDiscoveryChecks |
    ForEach-Object Checks | Select-Object Type, Key, Ports, Unique
#endregion
