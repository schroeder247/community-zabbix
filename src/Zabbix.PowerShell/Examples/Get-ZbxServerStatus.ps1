# This Source Code Form is subject to the terms of the Mozilla Public
# License, v. 2.0. Copyright (c) 2026 schroeder247. SPDX-License-Identifier: MPL-2.0
<#
.SYNOPSIS
Gets the server's system information statistics.
.DESCRIPTION
The Get-ZbxServerStatus cmdlet asks the server's trapper for the statistics the frontend's System information page shows
(status.get): how many hosts, items and triggers are enabled and disabled, how many users are online, and the required
values per second. It needs a Super admin's connection, whose API token the trapper accepts as the session, and the
trapper in -Server or the connection's. It writes a ZabbixServerStatus: the whole installation's counts, and each
proxy's in its Proxies.
#>
#region Read the server's statistics
# The object has the counts of Reports > System information.
Get-ZbxServerStatus -Server 'zabbix.example:10051'
#endregion

#region Show what each proxy monitors
# Proxies lists each proxy's hosts, items and required values per second.
(Get-ZbxServerStatus -Server 'zabbix.example:10051').Proxies | Format-Table
#endregion
