# This Source Code Form is subject to the terms of the Mozilla Public
# License, v. 2.0. Copyright (c) 2026 schroeder247. SPDX-License-Identifier: MPL-2.0
<#
.SYNOPSIS
Gets the hosts that Zabbix monitors.
.DESCRIPTION
A host is a monitored device, server or application: a name, the groups it is in, the interfaces it is polled on, and
the items, triggers and templates that monitor it. The Get-ZbxHost cmdlet reads hosts with host.get: all of them up to
-Limit, by technical name with -Name (case-insensitive; * and ? are wildcards), by id with -Id or from the pipeline,
or with the whole host.get selection in -Filter. -Group narrows any of these to the hosts of the given host groups,
and the -Select switches add related objects to each host. It writes ZabbixHost objects and needs a connection made
with Connect-ZbxServer.
#>
#region Get a host by its technical name
# -Name matches the host's technical name (the "host" field) exactly; the result is a ZabbixHost record, whose
# HostName and VisibleName properties carry the API's host and name fields.
Get-ZbxHost -Name 'web-01'
#endregion

#region Find hosts with a wildcard
# A * in -Name turns the exact match into a search, so this returns web-01, web-02 and so on.
Get-ZbxHost -Name 'web-*' | Select-Object HostId, HostName, Status
#endregion

#region Get the hosts of a group, with their interfaces and tags
# -Group takes a host group's name, id or object; the -Select* switches add related objects to each host.
Get-ZbxHost -Group 'Linux servers' -SelectInterfaces -SelectTags |
    ForEach-Object { '{0}: {1}' -f $_.HostName, ($_.Interfaces.IP -join ', ') }
#endregion

#region Count the hosts of a group without reading them
# -CountOutput asks the server for the number only (host.get's countOutput).
Get-ZbxHost -Group 'Linux servers' -CountOutput
#endregion
