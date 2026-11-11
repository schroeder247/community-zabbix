# This Source Code Form is subject to the terms of the Mozilla Public
# License, v. 2.0. Copyright (c) 2026 schroeder247. SPDX-License-Identifier: MPL-2.0
$null = New-ZbxHost -Host 'web-02' -Group 'Web servers' -InterfaceType Agent -InterfaceIp '10.0.0.12'

#region Disable a host
# -InputObject (position 0) takes a host name, id or object. Set-ZbxHost writes nothing by itself; -PassThru reads the
# changed host back and writes it.
Set-ZbxHost 'web-02' -Status Disabled -PassThru | Select-Object TechnicalName, Status
#endregion

#region Change the hosts a search finds, through the pipeline
# Get-ZbxHost writes host objects and Set-ZbxHost binds each one; every host gets the same change (host.update).
Get-ZbxHost -Group 'Web servers' | Set-ZbxHost -Description 'Front end pool' -PassThru |
    Select-Object TechnicalName, Description
#endregion

#region Link a template and set the host's groups
# -Template and -Group REPLACE the host's current lists (host.update semantics): list every group the host
# should stay in. -TemplatesClear unlinks a template and deletes what it brought.
Set-ZbxHost 'web-02' -Group 'Web servers', 'Linux servers' -Template 'Web App'
Get-ZbxHost -Name 'web-02' -SelectHostGroups -SelectParentTemplates |
    Select-Object TechnicalName, @{ n = 'Groups'; e = { $_.HostGroups.Name -join ', ' } },
        @{ n = 'Templates'; e = { $_.ParentTemplates.TechnicalName -join ', ' } }
#endregion

#region Replace a host's tags
# -Tag replaces every tag the host has; use Add-ZbxTag/Remove-ZbxTag to change one tag and keep the rest.
Set-ZbxHost 'web-02' -Tag @{ tag = 'env'; value = 'staging' }, @{ tag = 'team'; value = 'shop' }
(Get-ZbxHost -Name 'web-02' -SelectTags).Tags | Select-Object Tag, Value
#endregion
