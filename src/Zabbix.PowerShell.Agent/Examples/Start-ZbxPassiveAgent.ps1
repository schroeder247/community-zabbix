# This Source Code Form is subject to the terms of the Mozilla Public
# License, v. 2.0. Copyright (c) 2026 schroeder247. SPDX-License-Identifier: MPL-2.0
<#
.DESCRIPTION
The Start-ZbxPassiveAgent cmdlet starts a passive agent as a session of this runspace: a listener on -Port (10050 by
default) that answers the built-in keys (agent.ping, agent.version, agent.hostname, system.uname, and the bundled items:
Zabbix's own system, process, file, network and web keys, such as vfs.fs.size and proc.num) and every handler
registered on it, to the peers -AllowedPeer lets in. -KeyRule limits which keys it answers, with Zabbix's AllowKey and
DenyKey lines, and -NoBundledItems leaves the bundled items out. -AllowedPeer is the agent's Server= line and defaults to
127.0.0.1 and ::1 only, as Zabbix's own agent does. Give the agent's credentials (-PskIdentity and -Psk, or
-Certificate, -CertificateKey and -CertificateAuthority) to require TLS: the listener then accepts those kinds only,
unless -TlsAccept says otherwise, and without credentials it accepts unencrypted connections only.
Stop-ZbxPassiveAgent ends the session, and -PassThru writes it.
#>
#region Start a passive agent and query it
# The session listens on -Port (10050 by default) in the background of this runspace and answers the built-in keys
# (agent.ping, agent.version, agent.hostname, system.uname, and Zabbix's own keys such as vfs.fs.size) to whoever
# -AllowedPeer lets in: loopback only by default.
Start-ZbxPassiveAgent -Name 'web-agent' -Hostname 'web-01' -Port 30150
Get-ZbxAgentItem -Agent '127.0.0.1:30150' -Key 'agent.ping', 'agent.hostname' | Select-Object Key, Value
Stop-ZbxPassiveAgent -Name 'web-agent'
#endregion

#region Let the Zabbix server poll the agent
# -AllowedPeer is Zabbix's Server= line: IP addresses, CIDR blocks or DNS names. Anything not listed is refused.
# -Address binds one interface instead of every one; -PassThru writes the session.
Start-ZbxPassiveAgent -Name 'web-agent' -Hostname 'web-01' -AllowedPeer '127.0.0.1', '10.0.0.0/24' -Address '127.0.0.1' `
    -Port 30150 -PassThru | Select-Object Name, Kind, Started
Stop-ZbxPassiveAgent -Name 'web-agent'
#endregion

#region Answer only the keys you allow
# -KeyRule takes Zabbix's AllowKey and DenyKey lines, tried in order: here the agent answers agent.* and vfs.fs.* keys and
# nothing else. A denied key is answered as "This agent does not answer the key.", as a key nobody answers is.
Start-ZbxPassiveAgent -Name 'web-agent' -Hostname 'web-01' -Port 30150 -KeyRule 'AllowKey=agent.*', 'AllowKey=vfs.fs.*[*]', 'DenyKey=*'
Get-ZbxAgentItem -Agent '127.0.0.1:30150' -Key 'vfs.fs.size[/,pfree]', 'proc.num' | Select-Object Key, Value, Error
Stop-ZbxPassiveAgent -Name 'web-agent'
#endregion

#region Answer a key of your own
# A scriptblock registered on the session answers its key; the server (or Get-ZbxAgentItem) sees an ordinary item.
Start-ZbxPassiveAgent -Name 'web-agent' -Hostname 'web-01' -Port 30150
Register-ZbxPassiveAgentHandler -Name 'web-agent' -Key 'app.health' -ScriptBlock { 'ok' }
Get-ZbxAgentItem -Agent '127.0.0.1:30150' -Key 'app.health' | Select-Object Key, Value, IsSupported
Stop-ZbxPassiveAgent -Name 'web-agent'
#endregion

#region Keep an agent running for the length of a script
# A script that serves checks while it does its own work, and always stops the listener, even on failure.
Start-ZbxPassiveAgent -Name 'web-agent' -Hostname 'web-01' -Port 30150
try {
    (Get-ZbxAgentItem -Agent '127.0.0.1:30150' -Key 'agent.version').Value
}
finally {
    Stop-ZbxPassiveAgent -Name 'web-agent'
}
#endregion
