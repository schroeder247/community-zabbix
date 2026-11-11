# This Source Code Form is subject to the terms of the Mozilla Public
# License, v. 2.0. Copyright (c) 2026 schroeder247. SPDX-License-Identifier: MPL-2.0
# Vocabulary: Inventory script; Inventory reader

#region Create an API token
# Writes the new token (token.create, read back with token.get). It belongs to the calling user unless -UserId names
# another, and it has no secret yet: the server generates one only on request.
New-ZbxToken -Name 'Inventory script' -Description 'Read-only access for the inventory script'
#endregion

#region Create a token and connect with it
# -Generate also asks the server for the token's secret (token.generate) and adds it to the written token as the
# SecureString property Secret, which Connect-ZbxServer -ApiToken takes as it is. The server shows a secret only
# this once: keep it (a secret vault, or Export-Clixml, which encrypts it for this user on Windows).
$created = New-ZbxToken -Name 'Inventory reader' -Generate
Connect-ZbxServer -Uri 'https://zabbix.example/api_jsonrpc.php' -ApiToken $created.Secret -Name 'inventory' -NoDefault
Get-ZbxApiVersion -Connection 'inventory'
Disconnect-ZbxServer -Name 'inventory'
#endregion

Remove-ZbxToken -InputObject 'Inventory reader', 'Inventory script' -Confirm:$false
