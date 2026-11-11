# This Source Code Form is subject to the terms of the Mozilla Public
# License, v. 2.0. Copyright (c) 2026 schroeder247. SPDX-License-Identifier: MPL-2.0
#region Build a link trigger
# -TriggerId is positional; while the trigger is in the problem state the link is drawn in -Color (hexadecimal or a
# color name) and -DrawType.
$trigger = Get-ZbxTrigger -Host 'web-01' -Name 'Too many requests on web-01'
New-ZbxMapLinkTrigger $trigger.TriggerId -Color Red -DrawType BoldLine
#endregion

#region Color a map link while a trigger is in the problem state
# New-ZbxMapLink takes the link triggers in -LinkTrigger; the link joins two elements of the map by their selementid.
$trigger = Get-ZbxTrigger -Host 'web-01' -Name 'Too many requests on web-01'
New-ZbxMapLink -Element1Id 1 -Element2Id 2 -LinkTrigger (New-ZbxMapLinkTrigger $trigger.TriggerId -Color Red)
#endregion
