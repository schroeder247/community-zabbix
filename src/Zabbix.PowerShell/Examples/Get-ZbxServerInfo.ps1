# This Source Code Form is subject to the terms of the Mozilla Public
# License, v. 2.0. Copyright (c) 2026 schroeder247. SPDX-License-Identifier: MPL-2.0
<#
.SYNOPSIS
Gets what a connection is connected to.
.DESCRIPTION
The Get-ZbxServerInfo cmdlet writes one object for the current connection, or for -Connection: its Name, the API
Endpoint, the ApiVersion the server reported when Connect-ZbxServer connected, and the Trapper the sender and
server-wire commands use. It reads what the connection holds and sends nothing to the server. Get-ZbxApiVersion asks the
server again instead.
#>
#region Show the current connection's server
# Trapper is empty when Connect-ZbxServer was given no -Trapper; the protocol commands then ask hanode.get for it.
Get-ZbxServerInfo
#endregion

#region Show the server of every connection
# Get-ZbxContext's Servers holds the session's connections by name, and -Connection picks one for the command.
(Get-ZbxContext).Servers.Values | ForEach-Object { Get-ZbxServerInfo -Connection $_ } | Select-Object Name, ApiVersion
#endregion
