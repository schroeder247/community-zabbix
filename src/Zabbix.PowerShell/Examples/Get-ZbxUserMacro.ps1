# This Source Code Form is subject to the terms of the Mozilla Public
# License, v. 2.0. Copyright (c) 2026 schroeder247. SPDX-License-Identifier: MPL-2.0
$null = Get-ZbxHost -Name 'db-01' | New-ZbxUserMacro -Macro '{$APP.PORT}' -Value '5432'

#region List a host's macros
# -Host takes a host name, id or object; the result is the host's own macros (not inherited or global ones).
Get-ZbxUserMacro -Host 'web-01' | Select-Object HostMacroId, Macro, Value, Type
#endregion

#region Find one macro on every host of a group, through the pipeline
# A piped host binds -Host by its HostId. Macro names are matched here with Where-Object on the Macro property.
Get-ZbxHost -Group 'Linux servers' | Get-ZbxUserMacro |
    Where-Object Macro -EQ '{$APP.PORT}' |
    Select-Object HostId, Macro, Value
#endregion

#region Search macros by part of their name
# -Search is usermacro.get's substring search, keyed by the API field name (macro).
Get-ZbxUserMacro -Host 'web-01', 'db-01' -Search @{ macro = 'APP.' } -SelectHosts |
    Select-Object @{ n = 'Host'; e = { $_.Hosts.TechnicalName } }, Macro, Value
#endregion

#region List the global macros
# -Global reads the server-wide macros (Administration > Macros) instead of host and template macros.
Get-ZbxUserMacro -Global | Select-Object Macro, Value, Description
#endregion
