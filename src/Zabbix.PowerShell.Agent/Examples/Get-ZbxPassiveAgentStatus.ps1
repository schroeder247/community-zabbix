# This Source Code Form is subject to the terms of the Mozilla Public
# License, v. 2.0. Copyright (c) 2026 schroeder247. SPDX-License-Identifier: MPL-2.0
Start-ZbxPassiveAgent -Name 'web-agent' -Hostname 'web-01' -Port 30150

<#
.SYNOPSIS
Gets the running passive agent sessions.
.DESCRIPTION
The Get-ZbxPassiveAgentStatus cmdlet writes the passive agent sessions running in this runspace, or the one named in
-Name: each with its name, the host it answers as, when it started, and its Listener, the address and port it listens
on. It reads only the runspace's own sessions and sends nothing. Start-ZbxPassiveAgent starts a session and
Stop-ZbxPassiveAgent ends it.
#>
#region List the running passive agents
# Every passive agent session of this runspace.
Get-ZbxPassiveAgentStatus | Select-Object Name, Kind, Started
#endregion

#region Find the port an agent listens on
# Listener.LocalEndPoint is the address and port the server polls.
(Get-ZbxPassiveAgentStatus -Name 'web-agent').Listener.LocalEndPoint
#endregion

Stop-ZbxPassiveAgent -Name 'web-agent'
