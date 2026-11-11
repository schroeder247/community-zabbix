# This Source Code Form is subject to the terms of the Mozilla Public
# License, v. 2.0. Copyright (c) 2026 schroeder247. SPDX-License-Identifier: MPL-2.0
<#
.SYNOPSIS
Calls any Zabbix API method with the parameters given.
#>
#region Call a read method with a hashtable body
# -Body is the method's params exactly as the API documents them; the result comes back as ConvertFrom-Json would
# shape it (wire field names, not the typed records' property names). *.get methods skip -WhatIf/-Confirm.
Invoke-ZbxApi -Method 'host.get' -Body @{ output = @('hostid', 'host', 'status'); filter = @{ host = 'web-01' } }
#endregion

#region Pass the body as JSON text
# A JSON string works as well as a hashtable, e.g. a request copied from the API documentation.
Invoke-ZbxApi -Method 'hostgroup.get' -Body '{"output":["groupid","name"],"filter":{"name":["Linux servers","Web servers"]}}'
#endregion

#region Call a method that changes something
# Every method that is not a *.get can change the server, so it is gated like any other change (ConfirmImpact
# Medium): -WhatIf shows what would be called. The result is the method's own, here host.update's hostids.
$hostId = (Get-ZbxHost -Name 'web-01').HostId
Invoke-ZbxApi -Method 'host.update' -Body @{ hostid = $hostId; description = 'Front end, rack 4' } -WhatIf
Invoke-ZbxApi -Method 'host.update' -Body @{ hostid = $hostId; description = 'Front end, rack 4' }
#endregion

#region Call a method that takes no parameters
# -Body defaults to {}; apiinfo.version needs no authentication and answers the server's version string.
Invoke-ZbxApi apiinfo.version
#endregion
