# This Source Code Form is subject to the terms of the Mozilla Public
# License, v. 2.0. Copyright (c) 2026 schroeder247. SPDX-License-Identifier: MPL-2.0
<#
.SYNOPSIS
Stops a passive agent session.
.DESCRIPTION
The Stop-ZbxPassiveAgent cmdlet stops the passive agent session named in -Name, or each session piped from
Get-ZbxPassiveAgentStatus: it closes the listener, frees the port, and removes the session from the runspace. A
session also stops when the runspace closes. -WhatIf shows which session it would stop, and it writes nothing.
#>
#region Stop a passive agent by name
# Closes the session's listener (the port is free again) and disposes the session.
Start-ZbxPassiveAgent -Name 'web-agent' -Hostname 'web-01' -Port 30150
Stop-ZbxPassiveAgent -Name 'web-agent'
@(Get-ZbxPassiveAgentStatus).Count
#endregion

#region Stop every passive agent of this runspace
# Get-ZbxPassiveAgentStatus writes each running session; Stop-ZbxPassiveAgent binds its Name from the pipeline.
Start-ZbxPassiveAgent -Name 'web-agent' -Hostname 'web-01' -Port 30150
Start-ZbxPassiveAgent -Name 'db-agent' -Hostname 'db-01' -Port 30151
Get-ZbxPassiveAgentStatus | Stop-ZbxPassiveAgent
@(Get-ZbxPassiveAgentStatus).Count
#endregion

#region Preview a stop with -WhatIf
# -WhatIf names the session it would stop and leaves it listening. ConfirmImpact is Medium: -Confirm asks first.
Start-ZbxPassiveAgent -Name 'web-agent' -Hostname 'web-01' -Port 30150
Stop-ZbxPassiveAgent -Name 'web-agent' -WhatIf
Stop-ZbxPassiveAgent -Name 'web-agent'
#endregion
