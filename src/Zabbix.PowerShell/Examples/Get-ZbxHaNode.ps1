# This Source Code Form is subject to the terms of the Mozilla Public
# License, v. 2.0. Copyright (c) 2026 schroeder247. SPDX-License-Identifier: MPL-2.0
<#
.SYNOPSIS
Gets the nodes of the server's high-availability cluster.
.DESCRIPTION
The Get-ZbxHaNode cmdlet reads the server's high-availability nodes with hanode.get: each node's name, address, port,
status (Active, Standby, Stopped or Unavailable) and when it was last seen. It writes all nodes, the nodes with the ids
in -Id, or the nodes whose -Name, -Address and -Status match exactly; hanode.get has no search, so there are no
wildcards. A server that does not run in a cluster reports one node with an empty name.
#>
#region List the cluster's nodes
# LastAccess is the Unix time the node last updated its status.
Get-ZbxHaNode | Select-Object Name, Address, Port, Status
#endregion

#region Find the active node
# -Status matches exactly; the active node is the one serving the frontend and the trappers.
Get-ZbxHaNode -Status Active | Select-Object Name, Address, Port
#endregion
