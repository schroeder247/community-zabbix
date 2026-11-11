# This Source Code Form is subject to the terms of the Mozilla Public
# License, v. 2.0. Copyright (c) 2026 schroeder247. SPDX-License-Identifier: MPL-2.0
<#
.SYNOPSIS
Collects an item's value on the server without saving it.
.DESCRIPTION
The Test-ZbxItem cmdlet asks the server's trapper to run an item's collection and preprocessing (item.test), as the
Test button of the frontend's item form does, and writes the result: the value, or the error that stopped it. -Data is
what the frontend sends: the item, its host and the test options, as a hashtable keyed by the API's field names. It
saves nothing, so an item can be tried before it is created. It needs the trapper in -Server or the connection's.
#>
#region Test a calculated item's formula
# Type 15 is a calculated item and value_type 0 a float; the server evaluates params and writes the result.
Test-ZbxItem -Server 'zabbix.example:10051' -Data @{
    item    = @{ type = 15; key = 'app.total'; value_type = 0; params = '1+1' }
    options = @{ single = $false; state = 0 }
}
#endregion
