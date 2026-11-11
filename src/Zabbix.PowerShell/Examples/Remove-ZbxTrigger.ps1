# This Source Code Form is subject to the terms of the Mozilla Public
# License, v. 2.0. Copyright (c) 2026 schroeder247. SPDX-License-Identifier: MPL-2.0
$null = New-ZbxTrigger -Name 'Request backlog on web-01' -Expression 'last(/web-01/app.requests)>500' -Priority Average

#region Delete a trigger by name
# ConfirmImpact is High, so PowerShell asks first; -Confirm:$false answers for a script that has already decided.
Remove-ZbxTrigger -InputObject 'Request backlog on web-01' -Confirm:$false
#endregion

#region Preview deleting the triggers of a host
# -WhatIf names each trigger the command would delete, and deletes nothing.
Get-ZbxTrigger -Host 'web-01' | Remove-ZbxTrigger -WhatIf
#endregion
