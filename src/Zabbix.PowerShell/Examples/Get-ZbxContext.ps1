# This Source Code Form is subject to the terms of the Mozilla Public
# License, v. 2.0. Copyright (c) 2026 schroeder247. SPDX-License-Identifier: MPL-2.0
<#
.SYNOPSIS
Gets the session's connections and the current one.
.DESCRIPTION
The Get-ZbxContext cmdlet writes the runspace's Zabbix context: every connection Connect-ZbxServer added, by name, and
the current one, which commands use when no -Connection is given. Each runspace has its own context, so a job or a
ForEach-Object -Parallel branch starts with no connection. The command sends nothing to a server.
#>
#region Show the current connection
# Current is the connection commands use by default; Connect-ZbxServer -NoDefault adds one without making it current.
(Get-ZbxContext).Current | Select-Object Name, Endpoint, ApiVersion
#endregion

#region List every connection of the session
# Servers holds every connection by its name; Disconnect-ZbxServer removes one.
(Get-ZbxContext).Servers.Values | Select-Object Name, Endpoint
#endregion
