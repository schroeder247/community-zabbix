# This Source Code Form is subject to the terms of the Mozilla Public
# License, v. 2.0. Copyright (c) 2026 schroeder247. SPDX-License-Identifier: MPL-2.0
#region Add a second agent interface to a host
# -HostId takes the host's id; -Main 0 makes it an additional interface of its type (each type has one main
# interface). The API requires -Dns even when the interface connects by IP, so pass '' for an IP-only interface.
$hostId = (Get-ZbxHost -Name 'web-01').HostId
New-ZbxHostInterface -HostId $hostId -Type Agent -IP '10.0.0.21' -Dns '' -Port '10050' -UseIP IP -Main 0 |
    Select-Object InterfaceId, Type, IP, Port, Main
#endregion

#region Add a JMX interface reached by DNS name, through the pipeline
# A piped host binds -HostId by its HostId property. -UseIP Dns connects by -Dns, so -IP may be empty.
Get-ZbxHost -Name 'db-01' | New-ZbxHostInterface -Type Jmx -Dns 'db-01.example.com' -IP '' -Port '12345' -UseIP Dns -Main 1 |
    Select-Object InterfaceId, Type, Dns, Port
#endregion

#region Add an SNMPv2 interface
# An SNMP interface also needs its SNMP details (version, community); there is no parameter per detail, so
# -AdditionalProperty passes the API's details object as it is documented.
$hostId = (Get-ZbxHost -Name 'db-01').HostId
New-ZbxHostInterface -HostId $hostId -Type Snmp -IP '127.0.0.2' -Dns '' -Port '161' -UseIP IP -Main 1 `
    -AdditionalProperty @{ details = @{ version = 2; community = '{$SNMP_COMMUNITY}' } } |
    Select-Object InterfaceId, Type, IP, Port, @{ n = 'Community'; e = { $_.Details.Community } }
#endregion
