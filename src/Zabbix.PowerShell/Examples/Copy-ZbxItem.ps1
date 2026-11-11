# This Source Code Form is subject to the terms of the Mozilla Public
# License, v. 2.0. Copyright (c) 2026 schroeder247. SPDX-License-Identifier: MPL-2.0
$null = New-ZbxHost -Host 'web-02' -Group 'Web servers' -InterfaceType Agent -InterfaceIp '10.0.0.12'

#region Copy every item of one host onto another
# The source host's items are exported (configuration.export) and imported onto each target (createMissing), so
# items the target already has are left alone. Triggers are not copied.
Copy-ZbxItem -SourceHost 'web-01' -TargetHost 'web-02'
Get-ZbxItem -Host 'web-02' | Select-Object Name, Key
#endregion

#region Copy onto the hosts a search finds, through the pipeline
# Piped hosts bind -TargetHost; they are collected and copied to one at a time after the pipeline ends.
Get-ZbxHost -Name 'db-*' | Copy-ZbxItem -SourceHost 'web-01'
Get-ZbxItem -Host 'db-01' | Select-Object Name, Key
#endregion

#region Overwrite items the target already has
# -Force also updates same-keyed items on the target (updateExisting) instead of skipping them, so a change made
# on the source reaches the copies.
Get-ZbxItem -Host 'web-01' -Name 'Requests' | Set-ZbxItem -Units 'req' | Out-Null
Copy-ZbxItem -SourceHost 'web-01' -TargetHost 'web-02' -Force
Get-ZbxItem -Host 'web-02' -Name 'Requests' | Select-Object Name, Key, Units
#endregion
