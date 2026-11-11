# This Source Code Form is subject to the terms of the Mozilla Public
# License, v. 2.0. Copyright (c) 2026 schroeder247. SPDX-License-Identifier: MPL-2.0
$null = New-ZbxHttpTest -Name 'Web checkout' -Host 'web-01' -Step @{ name = 'Home page'; url = 'https://shop.example.com/'; status_codes = '200' }
$null = New-ZbxHttpTest -Name 'Checkout' -Host 'web-01' -Step @{ name = 'Home page'; url = 'https://shop.example.com/'; status_codes = '200' }

<#
.SYNOPSIS
Deletes web scenarios.
.DESCRIPTION
The Remove-ZbxHttpTest cmdlet deletes the web scenarios with the ids in -Id, or piped from Get-ZbxHttpTest, with
httptest.delete, in one call when the pipeline ends; the items the scenarios made go with them. It asks for confirmation
for each scenario unless -Confirm:$false or -Force is given, and -WhatIf lists what would be deleted. It writes nothing.
#>
#region Delete a scenario by name
# -Id takes ids, not names: Get-ZbxHttpTest finds the scenario and the pipeline passes its HttpTestId.
Get-ZbxHttpTest -Name 'Checkout' | Remove-ZbxHttpTest -Confirm:$false
#endregion

#region Preview deleting every scenario of a host
# -WhatIf names each scenario the command would delete, and deletes nothing.
Get-ZbxHttpTest -Host 'web-01' | Remove-ZbxHttpTest -WhatIf
#endregion
