# This Source Code Form is subject to the terms of the Mozilla Public
# License, v. 2.0. Copyright (c) 2026 schroeder247. SPDX-License-Identifier: MPL-2.0
$hostId = (Get-ZbxHost -Name 'web-01').HostId
$null = New-ZbxItem -HostId $hostId -Name 'Queue length' -Key 'app.queue' -Type Trapper -ValueType Unsigned

#region Delete an item
# -InputObject takes an id, an item object or a name (a name must be unique on the server, so pass the object when
# several hosts have an item of that name). ConfirmImpact is High: -Confirm:$false skips the prompt.
$item = Get-ZbxItem -Host 'web-01' -Name 'Queue length'
Remove-ZbxItem -InputObject $item -Confirm:$false
#endregion

$null = New-ZbxItem -HostId $hostId -Name 'Queue length' -Key 'app.queue' -Type Trapper -ValueType Unsigned
$null = New-ZbxItem -HostId $hostId -Name 'Queue age' -Key 'app.queue.age' -Type Trapper -ValueType Unsigned

#region Delete the items a search finds, through the pipeline
# Get-ZbxItem writes item objects; Remove-ZbxItem binds each one. -WhatIf lists what would be deleted.
Get-ZbxItem -Host 'web-01' -Search @{ key_ = 'app.queue' } | Remove-ZbxItem -WhatIf
Get-ZbxItem -Host 'web-01' -Search @{ key_ = 'app.queue' } | Remove-ZbxItem -Confirm:$false
Get-ZbxItem -Host 'web-01' | Select-Object Name, Key
#endregion
