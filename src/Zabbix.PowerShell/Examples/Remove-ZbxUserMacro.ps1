# This Source Code Form is subject to the terms of the Mozilla Public
# License, v. 2.0. Copyright (c) 2026 schroeder247. SPDX-License-Identifier: MPL-2.0
$null = Get-ZbxHost -Group 'Linux servers' | New-ZbxUserMacro -Macro '{$APP.TIMEOUT}' -Value '30s'

#region Delete one macro of a host
# Read the macro first and pipe it in: Get-ZbxUserMacro writes macro objects that Remove-ZbxUserMacro binds.
# ConfirmImpact is High, so -Confirm:$false skips the prompt.
Get-ZbxUserMacro -Host 'web-01' | Where-Object Macro -EQ '{$APP.TIMEOUT}' | Remove-ZbxUserMacro -Confirm:$false
Get-ZbxUserMacro -Host 'web-01' | Select-Object Macro, Value
#endregion

#region Delete a macro by its id
# -InputObject takes the macro's id (HostMacroId) as well as the object.
$macro = Get-ZbxUserMacro -Host 'db-01' | Where-Object Macro -EQ '{$APP.TIMEOUT}'
Remove-ZbxUserMacro -InputObject $macro.HostMacroId -Confirm:$false
#endregion

$null = Get-ZbxHost -Group 'Linux servers' | New-ZbxUserMacro -Macro '{$APP.TIMEOUT}' -Value '30s'

#region Delete a macro from every host of a group
# -WhatIf lists the macros that would go; run it again with -Confirm:$false to delete them.
Get-ZbxHost -Group 'Linux servers' | Get-ZbxUserMacro | Where-Object Macro -EQ '{$APP.TIMEOUT}' | Remove-ZbxUserMacro -WhatIf
Get-ZbxHost -Group 'Linux servers' | Get-ZbxUserMacro | Where-Object Macro -EQ '{$APP.TIMEOUT}' | Remove-ZbxUserMacro -Confirm:$false
#endregion
