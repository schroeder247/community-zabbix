# This Source Code Form is subject to the terms of the Mozilla Public
# License, v. 2.0. Copyright (c) 2026 schroeder247. SPDX-License-Identifier: MPL-2.0
#region Start a passive proxy's listener
# The session listens on -Port (10051 by default) for the server, which pushes the configuration and pulls history.
# -AllowedPeer is required: the server's addresses, CIDR blocks or DNS names; anyone else is refused.
# -Version is required: the server's major.minor, which a server never tells a passive proxy.
Start-ZbxPassiveProxy -Name 'dmz-proxy' -AllowedPeer '127.0.0.1' -Version '7.4' -Port 30160
Get-ZbxPassiveProxyStatus -Name 'dmz-proxy' | Select-Object Name, Started
Stop-ZbxPassiveProxy -Name 'dmz-proxy'
#endregion

#region Listen on one interface for the server's subnet
# -Address binds one interface instead of every one. The server polls the proxy object's Address/Port
# (New-ZbxProxy -OperatingMode Passive); Send-ZbxPassiveProxyHistory then queues values for hosts it has configured.
Start-ZbxPassiveProxy -Name 'dmz-proxy' -AllowedPeer '127.0.0.1', '10.0.0.0/24' -Version '7.4' -Address '127.0.0.1' -Port 30160
(Get-ZbxPassiveProxyStatus -Name 'dmz-proxy').Listener | Select-Object LocalEndPoint
Stop-ZbxPassiveProxy -Name 'dmz-proxy'
#endregion

#region Keep the listener for the length of a script
# -PassThru writes the session; the finally block always frees the port, even on failure.
$proxy = Start-ZbxPassiveProxy -Name 'dmz-proxy' -AllowedPeer '127.0.0.1' -Version '7.4' -Port 30160 -PassThru
try {
    'Listening as {0} since {1:u}' -f $proxy.Name, $proxy.Started
}
finally {
    Stop-ZbxPassiveProxy -Name 'dmz-proxy'
}
#endregion
