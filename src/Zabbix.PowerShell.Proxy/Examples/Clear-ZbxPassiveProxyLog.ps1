# This Source Code Form is subject to the terms of the Mozilla Public
# License, v. 2.0. Copyright (c) 2026 schroeder247. SPDX-License-Identifier: MPL-2.0
Start-ZbxPassiveProxy -Name 'dmz-proxy' -AllowedPeer '127.0.0.1' -Version '7.0' -Port 30160

<#
.SYNOPSIS
Empties the log buffer of a running passive proxy session.
.DESCRIPTION
The Clear-ZbxPassiveProxyLog cmdlet deletes every entry the passive proxy session named in -Name keeps in its log
buffer, so the next Get-ZbxPassiveProxyLog shows only what happens from now on. The listener keeps running and logging.
-WhatIf shows which session it would clear. It writes nothing.
#>
#region Start the log afresh
# After the clear, the log holds only what the listener does next.
Clear-ZbxPassiveProxyLog -Name 'dmz-proxy'
Get-ZbxPassiveProxyLog -Name 'dmz-proxy' | Select-Object Timestamp, Message
#endregion

Stop-ZbxPassiveProxy -Name 'dmz-proxy'
