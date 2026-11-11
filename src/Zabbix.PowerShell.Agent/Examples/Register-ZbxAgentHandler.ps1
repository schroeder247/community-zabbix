# This Source Code Form is subject to the terms of the Mozilla Public
# License, v. 2.0. Copyright (c) 2026 schroeder247. SPDX-License-Identifier: MPL-2.0
<#
.SYNOPSIS
Registers a scriptblock that answers an item key on a running agent session.
.DESCRIPTION
The Register-ZbxAgentHandler cmdlet adds a handler to the active or passive agent session named in -Name: a scriptblock
that answers -Key, run on the session's own runspace with the parsed key as its argument; its last output, as text, is
the value. It works on either kind of session; Register-ZbxActiveAgentHandler and Register-ZbxPassiveAgentHandler do the
same for one kind and refuse the other. A binding from New-ZbxAgentHandler can be piped in instead of -Key and
-ScriptBlock.
#>
#region Answer a key on a passive agent
# The handler's argument is the key; Parameters holds its parameters, here none.
Start-ZbxPassiveAgent -Name 'web-agent' -Hostname 'web-01' -Port 30150
Register-ZbxAgentHandler -Name 'web-agent' -Key 'app.health' -ScriptBlock { 'ok' }
Get-ZbxAgentItem -Agent '127.0.0.1:30150' -Key 'app.health' | Select-Object Key, Value
Stop-ZbxPassiveAgent -Name 'web-agent'
#endregion

#region Answer a key with parameters
# app.queue[orders] reaches the handler as a key whose Parameters[0] is orders.
Start-ZbxPassiveAgent -Name 'web-agent' -Hostname 'web-01' -Port 30150
Register-ZbxAgentHandler -Name 'web-agent' -Key 'app.queue' -ScriptBlock {
    param($key)
    @{ orders = 12; mail = 0 }[$key.Parameters[0]]
}
Get-ZbxAgentItem -Agent '127.0.0.1:30150' -Key 'app.queue[orders]' | Select-Object Key, Value
Stop-ZbxPassiveAgent -Name 'web-agent'
#endregion
