# This Source Code Form is subject to the terms of the Mozilla Public
# License, v. 2.0. Copyright (c) 2026 schroeder247. SPDX-License-Identifier: MPL-2.0
#region Get host groups by name
# -Name matches the group name exactly and takes several names.
Get-ZbxHostGroup -Name 'Linux servers', 'Web servers' | Select-Object GroupId, Name
#endregion

#region List a group's hosts
# -SelectHosts adds each group's hosts (hostgroup.get's selectHosts) to the Hosts property.
Get-ZbxHostGroup -Name 'Linux servers' -SelectHosts |
    ForEach-Object { '{0}: {1}' -f $_.Name, ($_.Hosts.TechnicalName -join ', ') }
#endregion

#region Get the groups a host is in, through the pipeline
# A piped host binds -Host by its HostId property.
Get-ZbxHost -Name 'web-01' | Get-ZbxHostGroup | Select-Object GroupId, Name
#endregion

#region Find the groups several hosts share
# -Host takes host names, ids or objects; the result is every group any of them is in, each once.
Get-ZbxHostGroup -Host 'web-01', 'db-01' | Select-Object -ExpandProperty Name
#endregion
