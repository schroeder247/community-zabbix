# This Source Code Form is subject to the terms of the Mozilla Public
# License, v. 2.0. Copyright (c) 2026 schroeder247. SPDX-License-Identifier: MPL-2.0
Start-ZbxPassiveProxy -Name 'dmz-proxy' -AllowedPeer '127.0.0.1' -Version '7.0' -Port 30160

<#
.SYNOPSIS
Registers a scriptblock that checks every configuration the server pushes to a passive proxy session.
.DESCRIPTION
The Register-ZbxPassiveProxyConfigHandler cmdlet sets the configuration handler of the passive proxy session named in
-Name: before the session takes a configuration the server pushes, it runs the scriptblock on its own runspace with
the incoming and the currently held configuration. Returning nothing accepts the configuration; returning text, or
throwing, refuses it, and the text is the reason the server is told. It writes the session only with -PassThru.
#>
#region Refuse a configuration without hosts
# Returning text refuses the configuration, and the server is told why; returning nothing takes it.
Register-ZbxPassiveProxyConfigHandler -Name 'dmz-proxy' -ScriptBlock {
    param($incoming, $held)
    if ($incoming.HostsByName.Count -eq 0) { 'refusing a configuration with no hosts' }
}
#endregion

#region Take only the hosts this proxy should monitor
# HostsByName holds the configuration's hosts by technical name; any other name refuses the whole configuration.
Register-ZbxPassiveProxyConfigHandler -Name 'dmz-proxy' -ScriptBlock {
    param($incoming, $held)
    $unexpected = @($incoming.HostsByName.Keys | Where-Object { $_ -notlike 'web-*' })
    if ($unexpected.Count -gt 0) { "unexpected hosts: $($unexpected -join ', ')" }
}
#endregion

Stop-ZbxPassiveProxy -Name 'dmz-proxy'
