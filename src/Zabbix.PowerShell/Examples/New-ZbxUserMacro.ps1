# This Source Code Form is subject to the terms of the Mozilla Public
# License, v. 2.0. Copyright (c) 2026 schroeder247. SPDX-License-Identifier: MPL-2.0
#region Add a macro to a host
# -HostId is the host's (or template's) id. The created macro is read back and written.
$hostId = (Get-ZbxHost -Name 'web-01').HostId
New-ZbxUserMacro -HostId $hostId -Macro '{$APP.NAME}' -Value 'shop' -Description 'Application name' |
    Select-Object HostMacroId, Macro, Value, Description
#endregion

#region Add the same macro to every host of a group, through the pipeline
# A piped host binds -HostId by its HostId property.
Get-ZbxHost -Group 'Linux servers' | New-ZbxUserMacro -Macro '{$APP.TIMEOUT}' -Value '30s' |
    Select-Object HostId, Macro, Value
#endregion

#region Store a secret in a macro
# -Type Secret stores the value write-only: the API never returns it, so Value reads back empty.
$hostId = (Get-ZbxHost -Name 'db-01').HostId
New-ZbxUserMacro -HostId $hostId -Macro '{$DB.PASSWORD}' -Value 'S3cret!' -Type Secret |
    Select-Object Macro, Type, Value
#endregion

#region Add a macro to a template
# A piped template binds -HostId through its TemplateId; hosts linked to the template inherit the macro.
Get-ZbxTemplate -Name 'Web App' | New-ZbxUserMacro -Macro '{$APP.PORT}' -Value '80' |
    Select-Object HostId, Macro, Value
#endregion
