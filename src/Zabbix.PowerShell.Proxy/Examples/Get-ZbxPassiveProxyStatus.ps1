# This Source Code Form is subject to the terms of the Mozilla Public
# License, v. 2.0. Copyright (c) 2026 schroeder247. SPDX-License-Identifier: MPL-2.0
Start-ZbxPassiveProxy -Name 'dmz-proxy' -AllowedPeer '127.0.0.1' -Version '7.0' -Port 30160

<#
.SYNOPSIS
Gets the running passive proxy sessions.
.DESCRIPTION
The Get-ZbxPassiveProxyStatus cmdlet writes the passive proxy sessions running in this runspace, or the one named in
-Name: each with its name, when it started, its Listener (the address and port the server connects to), and the
configuration the server last pushed. It reads only the runspace's own sessions and sends nothing.
Start-ZbxPassiveProxy starts a session and Stop-ZbxPassiveProxy ends it.
#>
#region List the running passive proxy sessions
# Every passive proxy session of this runspace.
Get-ZbxPassiveProxyStatus | Select-Object Name, Started
#endregion

#region Find the port a passive proxy listens on
# Listener.LocalEndPoint is the address and port to give the proxy object (New-ZbxProxy -Address -Port).
(Get-ZbxPassiveProxyStatus -Name 'dmz-proxy').Listener.LocalEndPoint
#endregion

Stop-ZbxPassiveProxy -Name 'dmz-proxy'
