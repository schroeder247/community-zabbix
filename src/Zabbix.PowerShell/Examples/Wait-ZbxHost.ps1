# This Source Code Form is subject to the terms of the Mozilla Public
# License, v. 2.0. Copyright (c) 2026 schroeder247. SPDX-License-Identifier: MPL-2.0
<#
.SYNOPSIS
Waits until a host exists on the server.
.DESCRIPTION
The Wait-ZbxHost cmdlet polls host.get every -Interval until a host with the technical name in -Name exists, and writes
it; after -Timeout (two minutes by default) it stops with an error. It is the companion of autoregistration: an active
agent's first autoregistration attempt fails by design and the host appears after the agent's next refresh, so a script
that starts an agent waits for its host before configuring it. It needs a connection made with Connect-ZbxServer.
#>
#region Wait for a host an agent registers
# Writes the host as soon as it exists; here web-01 exists already, so it returns at once.
Wait-ZbxHost -Name 'web-01' -Timeout '2m' | Select-Object HostName, HostId
#endregion

#region Wait for a new host, then configure it
# -Interval sets how often to look; the pipeline continues with the host once it is there.
$null = New-ZbxHost -Host 'web-02' -Group 'Web servers' -InterfaceType Agent -InterfaceIP '10.0.0.12'
Wait-ZbxHost -Name 'web-02' -Interval '5s' | Add-ZbxHostGroupMember -Group 'Linux servers'
#endregion
