# This Source Code Form is subject to the terms of the Mozilla Public
# License, v. 2.0. Copyright (c) 2026 schroeder247. SPDX-License-Identifier: MPL-2.0
<#
.SYNOPSIS
Adds hosts to a host group.
.DESCRIPTION
The Add-ZbxHostGroupMember cmdlet adds the hosts given to -InputObject (names, ids or piped hosts) to the host group in
-Group with host.massadd; each host keeps the groups it is already in. Every host the pipeline brings is added in one
call when the pipeline ends. The command writes nothing unless -PassThru is given, which reads the hosts back and
writes them. It needs a connection made with Connect-ZbxServer.
#>
#region Add a host to a group
# -InputObject (position 0) takes a host name, id or object, and -Group (position 1) the group's name, id or object.
Add-ZbxHostGroupMember -InputObject 'db-01' -Group 'Web servers'
#endregion

#region Add every host a search finds, through the pipeline
# Get-ZbxHost writes host objects, and the command adds them all in one host.massadd. -PassThru writes the hosts back.
Get-ZbxHost -Name 'web-*' | Add-ZbxHostGroupMember -Group 'Linux servers' -PassThru | Select-Object HostName, HostId
#endregion
