# This Source Code Form is subject to the terms of the Mozilla Public
# License, v. 2.0. Copyright (c) 2026 schroeder247. SPDX-License-Identifier: MPL-2.0
<#
.SYNOPSIS
Creates a web scenario on a host.
.DESCRIPTION
The New-ZbxHttpTest cmdlet creates a web scenario on the host in -Host with httptest.create, then reads it back and
writes the ZabbixHttpTest. -Step are the requests in order: objects from New-ZbxHttpTestStep or hashtables keyed by the
API's field names; a step without a number is numbered by its position. The server checks the scenario every -Delay and
keeps its response codes, times and speeds as items. -WhatIf shows what would be created.
#>
#region Check that a web shop answers
# One step that expects HTTP 200; the scenario runs every minute (the default) on web-01.
New-ZbxHttpTest -Name 'Web checkout' -Host 'web-01' -Step (New-ZbxHttpTestStep 'Home page' 'https://shop.example.com/' -StatusCodes '200') |
    Select-Object HttpTestId, Name, Delay
#endregion

#region Check a login flow every five minutes
# Two steps in order; the second posts a form and expects the text Welcome in the answer.
$steps = @(
    New-ZbxHttpTestStep 'Login page' 'https://shop.example.com/login' -StatusCodes '200'
    New-ZbxHttpTestStep 'Log in' 'https://shop.example.com/login' -Posts @{ name = 'user'; value = 'monitor' }, @{ name = 'password'; value = '{$SHOP.PASSWORD}' } -Required 'Welcome'
)
New-ZbxHttpTest -Name 'Checkout' -Host 'web-01' -Step $steps -Delay '5m' -Tag @{ team = 'shop' } | Select-Object Name, Delay
#endregion
