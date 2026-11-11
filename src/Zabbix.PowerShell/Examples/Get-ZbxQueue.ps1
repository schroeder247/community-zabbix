# This Source Code Form is subject to the terms of the Mozilla Public
# License, v. 2.0. Copyright (c) 2026 schroeder247. SPDX-License-Identifier: MPL-2.0
<#
.SYNOPSIS
Gets how many items are overdue on the server, and by how much.
.DESCRIPTION
The Get-ZbxQueue cmdlet asks the server's trapper for its queue (queue.get), the items whose values are late: by default
an overview of how many items of each item type are 5, 10, 30 seconds and longer overdue; with -ByProxy the same counts
per proxy; with -Details the overdue items themselves, most overdue first. It needs a Super admin's connection, whose
API token the trapper accepts as the session, and the trapper in -Server or the connection's.
#>
#region Show the queue by item type
# Each row is one item type; the columns count the items overdue by more than 5, 10, 30, 60 and 300 seconds and 10 minutes.
Get-ZbxQueue -Server 'zabbix.example:10051'
#endregion

#region List the most overdue items
# -Details lists the items themselves; -Limit caps the list (100 by default).
(Get-ZbxQueue -Server 'zabbix.example:10051' -Details -Limit 10).Items
#endregion
