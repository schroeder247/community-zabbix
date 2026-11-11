# This Source Code Form is subject to the terms of the Mozilla Public
# License, v. 2.0. Copyright (c) 2026 schroeder247. SPDX-License-Identifier: MPL-2.0
<#
.SYNOPSIS
Stops a passive proxy session.
.DESCRIPTION
The Stop-ZbxPassiveProxy cmdlet stops the passive proxy session named in -Name, or each session piped from
Get-ZbxPassiveProxyStatus: it closes the listener, frees the port and drops the configuration and values the session
held. -WhatIf shows which session it would stop. It writes nothing.
#>
#region Stop a passive proxy
# -Name (position 0) is the name Start-ZbxPassiveProxy gave the session.
Start-ZbxPassiveProxy -Name 'dmz-proxy' -AllowedPeer '127.0.0.1' -Version '7.0' -Port 30160
Stop-ZbxPassiveProxy -Name 'dmz-proxy'
#endregion

#region Stop every passive proxy session of this runspace
# Each session piped from Get-ZbxPassiveProxyStatus binds -Name.
Start-ZbxPassiveProxy -Name 'dmz-proxy' -AllowedPeer '127.0.0.1' -Version '7.0' -Port 30160
Get-ZbxPassiveProxyStatus | Stop-ZbxPassiveProxy
#endregion
