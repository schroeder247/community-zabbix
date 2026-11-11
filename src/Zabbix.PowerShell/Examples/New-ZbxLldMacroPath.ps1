# This Source Code Form is subject to the terms of the Mozilla Public
# License, v. 2.0. Copyright (c) 2026 schroeder247. SPDX-License-Identifier: MPL-2.0
# Vocabulary: Mounted filesystems
#region Build a macro path
# -LldMacro and -Path are both required: the macro the prototypes use, and the JSONPath that reads it from each entry.
New-ZbxLldMacroPath -LldMacro FSNAME -Path '$.fsname'
#endregion

#region Read discovery data with your own field names
# The rule's data has fsname and fstype fields; the macro paths make them {#FSNAME} and {#FSTYPE}.
$paths = @(
    New-ZbxLldMacroPath -LldMacro FSNAME -Path '$.fsname'
    New-ZbxLldMacroPath -LldMacro FSTYPE -Path '$.fstype'
)
Get-ZbxHost -Name 'web-01' |
    New-ZbxDiscoveryRule -Name 'Mounted filesystems' -Key 'vfs.fs.get' -Type ZabbixActive -Delay '1h' -LldMacroPath $paths |
    Select-Object Name, Key
#endregion
