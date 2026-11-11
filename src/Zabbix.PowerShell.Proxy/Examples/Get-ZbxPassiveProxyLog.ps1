# This Source Code Form is subject to the terms of the Mozilla Public
# License, v. 2.0. Copyright (c) 2026 schroeder247. SPDX-License-Identifier: MPL-2.0
Start-ZbxPassiveProxy -Name 'dmz-proxy' -AllowedPeer '127.0.0.1' -Version '7.0' -Port 30160

<#
.SYNOPSIS
Gets the recent log entries of a running passive proxy session.
.DESCRIPTION
The Get-ZbxPassiveProxyLog cmdlet writes the log entries the passive proxy session named in -Name keeps in its own
bounded buffer: the connections it accepted or refused, the configurations the server pushed and the data it pulled,
with a timestamp, a level and a category. The listener runs in the background, so its log is the place to see why the
server does not reach it. -Tail keeps the last entries and -Level the entries at or above a level.
#>
#region Read the last log entries of a passive proxy
# Each entry has a Timestamp, a Level, a Category and the Message.
Get-ZbxPassiveProxyLog -Name 'dmz-proxy' -Tail 10 | Select-Object Timestamp, Level, Message
#endregion

#region Find refused connections
# A peer that -AllowedPeer does not list is refused, and logged as a warning.
Get-ZbxPassiveProxyLog -Name 'dmz-proxy' -Level Warning
#endregion

Stop-ZbxPassiveProxy -Name 'dmz-proxy'
