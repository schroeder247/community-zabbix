# This Source Code Form is subject to the terms of the Mozilla Public
# License, v. 2.0. Copyright (c) 2026 schroeder247. SPDX-License-Identifier: MPL-2.0
$null = Connect-ZbxServer -Uri 'https://zabbix.example/api_jsonrpc.php' -ApiToken $token -Name 'staging' -NoDefault

#region Show which server the session talks to
# Writes the current connection's API version, name, endpoint and credential kind (the secret itself is never
# shown).
Get-ZbxApiInfo
#endregion

#region Show a named connection
# -Connection takes a connection name (see Get-ZbxContext) or the object Connect-ZbxServer -PassThru wrote.
Get-ZbxApiInfo -Connection 'staging'
#endregion

#region Branch a script on the server version
# Version is a System.Version, so it compares with -ge/-lt directly: proxy groups (proxygroup.*) exist from
# Zabbix 7.0 on.
if ((Get-ZbxApiInfo).Version -ge [version]'7.0') {
    'Proxy groups are available on {0}' -f (Get-ZbxApiInfo).Connection
}
#endregion

Disconnect-ZbxServer -Name 'staging'
