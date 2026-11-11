# This Source Code Form is subject to the terms of the Mozilla Public
# License, v. 2.0. Copyright (c) 2026 schroeder247. SPDX-License-Identifier: MPL-2.0
$credential = [pscredential]::new('Admin', (ConvertTo-SecureString 'zabbix' -AsPlainText -Force))
# A pre-shared key file, as Zabbix's TLSPSKFile holds one: 64 hex digits on one line.
Set-Location (New-Item -ItemType Directory -Path (Join-Path ([IO.Path]::GetTempPath()) ([guid]::NewGuid().ToString('N'))))
-join (1..32 | ForEach-Object { '{0:x2}' -f (Get-Random -Maximum 256) }) | Set-Content -Path 'web-01.psk'

#region Connect with an API token
# $token is the API token as a SecureString (create one under Users > API tokens; read it with
# Read-Host -AsSecureString). The connection is named after the endpoint's host and port and becomes the current
# server, so every later command uses it without -Connection.
Connect-ZbxServer -Uri 'https://zabbix.example/api_jsonrpc.php' -ApiToken $token
Get-ZbxApiInfo
#endregion

#region Tell a refused credential from an unreachable server
# The credential is proven before the connection is added: a token or password the server refuses ends the command
# with an AuthenticationError naming the server, and nothing is added. An unreachable server is a ConnectionError.
try { Connect-ZbxServer -Uri 'https://zabbix.example/api_jsonrpc.php' -ApiToken (ConvertTo-SecureString 'not-a-token' -AsPlainText -Force) -Name 'refused' }
catch { $_.CategoryInfo.Category; $_.ErrorDetails.Message }
#endregion

#region Connect with a user name and password
# $credential is a PSCredential (Get-Credential). Connect-ZbxServer logs in (user.login) to prove it, and the session
# id is used from then on. -NoDefault adds the connection without making it the current server.
Connect-ZbxServer -Uri 'https://zabbix.example/api_jsonrpc.php' -Credential $credential -Name 'admin' -NoDefault
Get-ZbxApiInfo -Connection 'admin'
Disconnect-ZbxServer -Name 'admin'
#endregion

#region Work with a second server side by side
# A named connection that is not the default is reached with -Connection, by its name or by the object -PassThru
# wrote. The current server stays what it was.
$staging = Connect-ZbxServer -Uri 'https://zabbix.example/api_jsonrpc.php' -ApiToken $token -Name 'staging' -NoDefault -PassThru
Get-ZbxHost -Name 'web-01' -Connection $staging | Select-Object HostId, HostName
Get-ZbxContext | Select-Object -ExpandProperty Servers
Disconnect-ZbxServer -Name 'staging'
#endregion

#region Tell the connection where the server's trapper listens
# -Trapper is the server's trapper port as this machine reaches it, for the commands that speak the Zabbix
# protocol instead of the API (Send-ZbxData and the agent/proxy commands). Without it they ask the API for the
# active HA node's address, which may not be reachable from a client behind NAT.
$lab = Connect-ZbxServer -Uri 'https://zabbix.example/api_jsonrpc.php' -ApiToken $token -Trapper 'zabbix.example:10051' -Name 'lab' -NoDefault -PassThru
$lab | Select-Object Name, Endpoint, Trapper, ApiVersion
Disconnect-ZbxServer -Name 'lab'
#endregion

#region Encrypt the trapper connection
# The TLS parameters are the trapper's, named after Zabbix's configuration (TLSPSKIdentity, TLSPSKFile, TLSCertFile, …):
# Send-ZbxData, Get-ZbxQueue, Test-ZbxItem and the other server-wire commands use them unless they are given their own.
# -TrapperPsk takes a SecureString or the path of a key file; the API's HTTPS is not affected.
Connect-ZbxServer -Uri 'https://zabbix.example/api_jsonrpc.php' -ApiToken $token -Trapper 'zabbix.example:10051' -TrapperPskIdentity 'web-01' -TrapperPsk ./web-01.psk -Name 'tls' -NoDefault
Disconnect-ZbxServer -Name 'tls'
#endregion
