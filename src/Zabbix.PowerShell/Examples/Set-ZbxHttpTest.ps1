# This Source Code Form is subject to the terms of the Mozilla Public
# License, v. 2.0. Copyright (c) 2026 schroeder247. SPDX-License-Identifier: MPL-2.0
$null = New-ZbxHttpTest -Name 'Web checkout' -Host 'web-01' -Step @{ name = 'Home page'; url = 'https://shop.example.com/'; status_codes = '200' }

<#
.SYNOPSIS
Changes a web scenario.
.DESCRIPTION
The Set-ZbxHttpTest cmdlet changes the web scenario in -Id, or a scenario piped from Get-ZbxHttpTest, with
httptest.update; a setting whose parameter is not given keeps its value. -Step replaces every step the scenario has.
With -PassThru the command reads the scenario back and writes it; without it, it writes nothing. -WhatIf shows what would
change.
#>
#region Check a scenario every five minutes
# A scenario piped from Get-ZbxHttpTest binds -Id by its HttpTestId.
Get-ZbxHttpTest -Name 'Web checkout' | Set-ZbxHttpTest -Delay '5m' -PassThru | Select-Object Name, Delay
#endregion

#region Replace a scenario's steps
# -Step replaces the whole list, so give every step the scenario should keep.
Get-ZbxHttpTest -Name 'Web checkout' | Set-ZbxHttpTest -Step @(
    New-ZbxHttpTestStep 'Home page' 'https://shop.example.com/' -StatusCodes '200'
    New-ZbxHttpTestStep 'Cart' 'https://shop.example.com/cart' -StatusCodes '200'
)
#endregion
