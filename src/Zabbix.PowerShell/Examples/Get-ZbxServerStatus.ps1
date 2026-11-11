# This Source Code Form is subject to the terms of the Mozilla Public
# License, v. 2.0. Copyright (c) 2026 schroeder247. SPDX-License-Identifier: MPL-2.0
<#
.SYNOPSIS
Gets the server's system information statistics.
.DESCRIPTION
The Get-ZbxServerStatus cmdlet asks the server's trapper for the statistics the frontend's System information page shows
(status.get): how many hosts, items and triggers are enabled and disabled, how many users are online, and the required
values per second. It needs a Super admin's connection, whose API token the trapper accepts as the session, and the
trapper in -Server or the connection's. It writes one object as the server sent it.
#>
#region Read the server's statistics
# The object has the same sections as Reports > System information.
Get-ZbxServerStatus -Server 'zabbix.example:10051'
#endregion

#region Show every section of the statistics
# Format-List shows each section the server sent on its own line.
Get-ZbxServerStatus -Server 'zabbix.example:10051' | Format-List
#endregion
