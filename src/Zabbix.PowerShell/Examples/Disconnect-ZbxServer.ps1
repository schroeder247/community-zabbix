# This Source Code Form is subject to the terms of the Mozilla Public
# License, v. 2.0. Copyright (c) 2026 schroeder247. SPDX-License-Identifier: MPL-2.0
<#
.DESCRIPTION
The Disconnect-ZbxServer cmdlet closes the connection named in -Name, or the current one, and removes it from the
session's context; a call still in flight on it finishes first. When it closes the current connection, no connection
is current until Connect-ZbxServer makes one. It sends no logout, so an API token stays valid. It writes nothing.
#>
$null = Connect-ZbxServer -Uri 'https://zabbix.example/api_jsonrpc.php' -ApiToken $token -Name 'staging' -NoDefault

#region Close a named connection
# -Name is the name the connection was given by Connect-ZbxServer -Name (or its endpoint's host:port). A call
# still in flight on it finishes first. Without -Name, the current server is closed.
Disconnect-ZbxServer -Name 'staging'
(Get-ZbxContext).Servers.Keys
#endregion

#region Close a connection a script opened, even when the script fails
# -Name binds from the Name property of a piped connection object, so the object -PassThru wrote can be piped
# straight back in a finally block.
$staging = Connect-ZbxServer -Uri 'https://zabbix.example/api_jsonrpc.php' -ApiToken $token -Name 'staging' -NoDefault -PassThru
try {
    Get-ZbxHost -Group 'Linux servers' -Connection $staging -CountOutput
}
finally {
    $staging | Disconnect-ZbxServer
}
#endregion
