# This Source Code Form is subject to the terms of the Mozilla Public
# License, v. 2.0. Copyright (c) 2026 schroeder247. SPDX-License-Identifier: MPL-2.0
#region Change an item's units and history period
# An item piped from Get-ZbxItem binds -InputObject; a parameter not given keeps its value.
Get-ZbxItem -Host 'web-01' -Name 'Requests' | Set-ZbxItem -Units 'rps' -History '7d'
#endregion

#region Disable every item of a host whose key matches
# -Search is item.get's substring search; each matching item is disabled in one item.update.
Get-ZbxItem -Host 'web-01' -Search @{ key_ = 'app.' } | Set-ZbxItem -Status Disabled -PassThru | Select-Object Key, Status
#endregion

#region Tag an item
# -Tag replaces the item's tags; give every tag it should keep.
Get-ZbxItem -Host 'web-01' -Name 'Requests' | Set-ZbxItem -Tag @{ tag = 'component'; value = 'application' }
#endregion
