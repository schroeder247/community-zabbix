# This Source Code Form is subject to the terms of the Mozilla Public
# License, v. 2.0. Copyright (c) 2026 schroeder247. SPDX-License-Identifier: MPL-2.0
# Vocabulary: Mounted filesystems
#region Build a condition that keeps the usual file systems
# -Macro and -Value are positional: the LLD macro, written with or without {# }, and the regular expression it must match.
New-ZbxLldFilterCondition FSTYPE '^(ext4|xfs|btrfs)$'
#endregion

#region Filter what a discovery rule creates
# The rule creates items only for the file systems whose {#FSTYPE} matches and whose {#FSNAME} does not.
$conditions = @(
    New-ZbxLldFilterCondition FSTYPE '^(ext4|xfs|btrfs)$'
    New-ZbxLldFilterCondition FSNAME '^/(snap|run)/' -Operator NotRegexp
)
Get-ZbxHost -Name 'web-01' |
    New-ZbxDiscoveryRule -Name 'Mounted filesystems' -Key 'vfs.fs.discovery' -Type ZabbixActive -Delay '1h' -Filter @{ evaltype = 'And'; conditions = $conditions } |
    Select-Object Name, Key
#endregion
