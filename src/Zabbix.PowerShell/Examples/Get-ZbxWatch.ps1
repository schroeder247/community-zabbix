# This Source Code Form is subject to the terms of the Mozilla Public
# License, v. 2.0. Copyright (c) 2026 schroeder247. SPDX-License-Identifier: MPL-2.0
Watch-ZbxItem -Name 'requests' -HostName 'web-01' -Key 'app.requests'
Watch-ZbxItem -Name 'linux' -Group 'Linux servers'

<#
.SYNOPSIS
Gets the running watches of the session.
.DESCRIPTION
The Get-ZbxWatch cmdlet writes the watches Watch-ZbxItem started in this runspace, or the one named in -Name: each
with its name, when it started, how many reads succeeded and failed, and how many values its readers dropped. It reads
only the runspace's own watches and sends nothing to the server.
#>
#region List the running watches
# SucceededPolls and FailedPolls count the watch's reads of the server.
Get-ZbxWatch | Select-Object Name, Started, SucceededPolls, FailedPolls
#endregion

#region Check whether a reader lost values
# Dropped counts the values a slow Receive-ZbxWatch reader lost to its full buffer.
(Get-ZbxWatch -Name 'requests').Dropped
#endregion

Get-ZbxWatch | Stop-ZbxWatch
