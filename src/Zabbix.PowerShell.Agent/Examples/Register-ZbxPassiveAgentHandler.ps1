# This Source Code Form is subject to the terms of the Mozilla Public
# License, v. 2.0. Copyright (c) 2026 schroeder247. SPDX-License-Identifier: MPL-2.0
<#
.SYNOPSIS
Registers a scriptblock that answers an item key on a running passive agent session.
.DESCRIPTION
The Register-ZbxPassiveAgentHandler cmdlet adds a handler to the passive agent session named in -Name: a scriptblock
that answers -Key, run on the session's own runspace with the parsed key as its argument; its last output, as text, is
the value the agent answers when the server or Get-ZbxAgentItem asks for that key. A binding from New-ZbxAgentHandler
can be piped in instead. It refuses an active session; Register-ZbxAgentHandler takes either kind. It writes the
session only with -PassThru.
#>
Start-ZbxPassiveAgent -Name 'web-agent' -Hostname 'web-01' -Port 30150

#region Answer a custom item key
# The scriptblock runs on the session's own runspace (not this one: module commands and your variables are not
# there); its last output, converted to text, is the answer. The registration applies to the very next check.
Register-ZbxPassiveAgentHandler -Name 'web-agent' -Key 'app.health' -ScriptBlock { 'ok' }
Get-ZbxAgentItem -Agent '127.0.0.1:30150' -Key 'app.health' | Select-Object Key, Value, IsSupported
#endregion

#region Read the key's parameters
# The scriptblock's first argument is the parsed key: app.queue[orders] arrives with Parameters = orders.
Register-ZbxPassiveAgentHandler -Name 'web-agent' -Key 'app.queue' -ScriptBlock {
    param($key)
    @{ orders = 12; mail = 0 }[$key.Parameters[0]]
}
Get-ZbxAgentItem -Agent '127.0.0.1:30150' -Key 'app.queue[orders]', 'app.queue[mail]' | Select-Object Key, Value
#endregion

#region Register a catalogue of handlers through the pipeline
# New-ZbxAgentHandler builds key/scriptblock bindings once; piped in, each one binds to -Binding.
$handlers = @(
    New-ZbxAgentHandler -Key 'app.version' -ScriptBlock { '2.4.1' }
    New-ZbxAgentHandler -Key 'app.uptime' -ScriptBlock { 3600 }
)
$handlers | Register-ZbxPassiveAgentHandler -Name 'web-agent'
Get-ZbxAgentItem -Agent '127.0.0.1:30150' -Key 'app.version', 'app.uptime' | Select-Object Key, Value
#endregion

Stop-ZbxPassiveAgent -Name 'web-agent'
