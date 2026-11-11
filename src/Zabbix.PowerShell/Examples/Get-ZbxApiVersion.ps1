# This Source Code Form is subject to the terms of the Mozilla Public
# License, v. 2.0. Copyright (c) 2026 schroeder247. SPDX-License-Identifier: MPL-2.0
<#
.SYNOPSIS
Gets the version of the Zabbix API a server runs.
.DESCRIPTION
The Get-ZbxApiVersion cmdlet calls apiinfo.version on the current connection, or on -Connection, and writes the
version as a System.Version, so a script can compare it with another version. apiinfo.version needs no login, but the
command still needs a connection made with Connect-ZbxServer. Get-ZbxApiInfo writes the version together with the
connection's name, endpoint and credential.
#>
#region Get the API version
# The result is a [version]: Major, Minor and Build are numbers.
Get-ZbxApiVersion
#endregion

#region Branch on the server's version
# A [version] compares as a version, so 7.4 is newer than 7.0 and 7.10 newer than 7.4.
if ((Get-ZbxApiVersion) -ge [version]'7.4') { 'Zabbix 7.4 or later' } else { 'Zabbix 7.0' }
#endregion
