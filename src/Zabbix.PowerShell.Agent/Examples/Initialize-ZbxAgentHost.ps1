# This Source Code Form is subject to the terms of the Mozilla Public
# License, v. 2.0. Copyright (c) 2026 schroeder247. SPDX-License-Identifier: MPL-2.0
<#
.SYNOPSIS
Creates the host, interface and items an agent session answers, or reuses those that exist.
.DESCRIPTION
The Initialize-ZbxAgentHost cmdlet makes sure the server has the host an agent answers as, in -HostGroup, with one
agent interface (-InterfaceAddress and -InterfacePort) and one item per key: each key the session named in -Session
answers, or each key in -Key. A passive session gets Zabbix agent items, which the server polls on that interface
(its port is the session's listener port unless -InterfacePort is given), and an active one active checks; a second
run reuses what exists, by host name and then by item key, and adds only what is missing. A new item's value type is
the built-in key's own (agent.ping Unsigned; agent.version, agent.hostname and system.uname Character, as Zabbix's own
agent templates have them) and -ValueType for every other key: Text unless given, which stores whatever a handler
answers; give Unsigned or Float for a key that answers numbers you want to graph. With -Session, what was created
is remembered on the session for Get-ZbxAgentHost, Publish-ZbxAgentHost and Clear-ZbxAgentHost; the command writes the
host's status only with -PassThru.
#>
#region Create a host with agent items from a list of keys
# Creates web-02 (host group -HostGroup, created if missing; 'PowerShell agents' when omitted), one agent interface
# (127.0.0.1:10050 unless -InterfaceAddress/-InterfacePort) and one active item (ZabbixActive, every 1m) per key.
# -ValueType applies to every new item but a built-in key's (Text when omitted). -PassThru writes the resulting host
# status.
Initialize-ZbxAgentHost -HostName 'web-02' -Key 'app.health', 'app.version' -HostGroup 'Web servers' -ValueType Character -PassThru |
    ForEach-Object Items
#endregion

Remove-ZbxHost -InputObject 'web-02' -Confirm:$false

#region Publish what a running agent answers
# With -Session, the keys are the session's own: the built-in four plus every registered handler (HasLocalHandler),
# and the host name defaults to the session's -Hostname. The built-in keys get their own value types and app.health
# Text, so every item takes the answer it is sent (for a handler that answers numbers you want to graph, add
# -ValueType Unsigned or Float). The agent's own requests log 'host not found' until the server's configuration cache
# loads the new host (up to CacheUpdateFrequency, 60 s by default).
Start-ZbxActiveAgent -Name 'web-agent' -Hostname 'web-02' -Server 'zabbix.example:10051'
Register-ZbxActiveAgentHandler -Name 'web-agent' -Key 'app.health' -ScriptBlock { 'ok' }
Initialize-ZbxAgentHost -Session 'web-agent' -HostGroup 'Web servers' -PassThru | ForEach-Object Items
Get-ZbxItem -Host 'web-02' | Select-Object Key, ValueType | Sort-Object Key
Stop-ZbxActiveAgent -Name 'web-agent'
#endregion

#region Run it again safely
# Idempotent by host name and item key: a second run reuses the host, interface and items that already exist and
# only adds what is missing (here app.uptime); nothing is reshaped. -WhatIf shows the host it would touch.
Initialize-ZbxAgentHost -HostName 'web-02' -Key 'app.health', 'app.uptime' -HostGroup 'Web servers' -WhatIf
Initialize-ZbxAgentHost -HostName 'web-02' -Key 'app.health', 'app.uptime' -HostGroup 'Web servers'
Get-ZbxItem -Host 'web-02' | Select-Object Key, Type | Sort-Object Key
#endregion

Remove-ZbxHost -InputObject 'web-02' -Confirm:$false

#region Let the server poll a passive agent
# A passive session's keys become Zabbix agent items, which the server polls on the interface: its port is the
# session's listener port, and -InterfaceAddress is where the server reaches this machine (an IP address, or a DNS name
# the server resolves). The agent answers only the peers -AllowedPeer lets in, so it names the server's network.
Start-ZbxPassiveAgent -Name 'web-agent' -Hostname 'web-02' -AllowedPeer '10.0.0.0/24' -Port 30150
Register-ZbxPassiveAgentHandler -Name 'web-agent' -Key 'app.health' -ScriptBlock { 'ok' }
Initialize-ZbxAgentHost -Session 'web-agent' -HostGroup 'Web servers' -InterfaceAddress '10.0.0.7' -PassThru | ForEach-Object Items
Stop-ZbxPassiveAgent -Name 'web-agent'
#endregion
