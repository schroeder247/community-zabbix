# This Source Code Form is subject to the terms of the Mozilla Public
# License, v. 2.0. Copyright (c) 2026 schroeder247. SPDX-License-Identifier: MPL-2.0
# Vocabulary: Nginx by HTTP
$null = New-ZbxTemplate -Host 'Nginx by HTTP' -Group 'Templates/Web'

#region Delete a template by name
# -InputObject takes a name, an id or a template object. ConfirmImpact is High, so PowerShell asks first unless
# -Confirm:$false is given. Hosts linked to the template lose everything it brought (items, triggers...).
Remove-ZbxTemplate -InputObject 'Nginx by HTTP' -Confirm:$false
#endregion

$null = New-ZbxTemplate -Host 'Nginx by HTTP' -Group 'Templates/Web'

#region Delete every template of a template group, through the pipeline
# Get-ZbxTemplate writes template objects; Remove-ZbxTemplate binds each one. -WhatIf lists what would go.
Get-ZbxTemplate -Group 'Templates/Web' | Remove-ZbxTemplate -WhatIf
Get-ZbxTemplate -Group 'Templates/Web' | Remove-ZbxTemplate -Confirm:$false
Get-ZbxTemplate -Group 'Templates/Web' -CountOutput
#endregion
