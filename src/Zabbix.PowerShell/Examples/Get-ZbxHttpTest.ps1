# This Source Code Form is subject to the terms of the Mozilla Public
# License, v. 2.0. Copyright (c) 2026 schroeder247. SPDX-License-Identifier: MPL-2.0
$null = New-ZbxHttpTest -Name 'Web checkout' -Host 'web-01' -Step @{ name = 'Home page'; url = 'https://shop.example.com/'; status_codes = '200' }

<#
.SYNOPSIS
Gets web scenarios.
.DESCRIPTION
The Get-ZbxHttpTest cmdlet reads web scenarios with httptest.get: all of them, the scenarios on the hosts in -Host,
the scenarios with the ids in -Id, or by name with -Name (* and ? are wildcards). Each ZabbixHttpTest carries its steps,
interval, authentication and tags. It needs a connection made with Connect-ZbxServer.
#>
#region Get the web scenarios of a host
# -HostReference (alias -Host) takes a host's name, id or object; a host piped from Get-ZbxHost binds by its HostId.
Get-ZbxHttpTest -Host 'web-01' | Select-Object HttpTestId, Name, Delay, Status
#endregion

#region Get a scenario by name and list its steps
# Each step is one request: its order, name, URL and the status codes it expects.
(Get-ZbxHttpTest -Name 'Web checkout').Steps | Select-Object No, Name, Url, StatusCodes
#endregion
