# This Source Code Form is subject to the terms of the Mozilla Public
# License, v. 2.0. Copyright (c) 2026 schroeder247. SPDX-License-Identifier: MPL-2.0
#region Raise a trigger's severity
# -InputObject (position 0) takes the trigger's name, id or object.
Set-ZbxTrigger -InputObject 'Too many requests on web-01' -Priority Disaster
#endregion

#region Change the triggers of a host through the pipeline
# Get-ZbxTrigger writes the triggers and the command changes them in one call; -PassThru writes them back.
Get-ZbxTrigger -Host 'web-01' | Set-ZbxTrigger -Description 'Runbook: https://wiki.example.com/web' -PassThru |
    Select-Object Name, Description
#endregion

#region Disable a trigger without deleting it
# A disabled trigger raises no problems until it is enabled again with -Status Enabled.
Set-ZbxTrigger -InputObject 'Too many requests on web-01' -Status Disabled -PassThru | Select-Object Description, Status
#endregion
