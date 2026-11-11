# This Source Code Form is subject to the terms of the Mozilla Public
# License, v. 2.0. Copyright (c) 2026 schroeder247. SPDX-License-Identifier: MPL-2.0
<#
.SYNOPSIS
Stops an active proxy session.
.DESCRIPTION
The Stop-ZbxActiveProxy cmdlet stops the active proxy session named in -Name, or each session piped from
Get-ZbxActiveProxyStatus: it stops pulling the configuration and uploading data, and is removed from the runspace. A
session also stops when the runspace closes. -WhatIf shows which session it would stop, and it writes nothing.
#>
#region Stop an active proxy's uplink by name
# Cancels the session's refresh loop, waits for a pull in flight to end, and disposes the session.
Start-ZbxActiveProxy -Name 'uplink' -ProxyName 'proxy-01' -Server 'zabbix.example:10051'
Stop-ZbxActiveProxy -Name 'uplink' -WarningAction SilentlyContinue
@(Get-ZbxActiveProxyStatus).Count
#endregion

#region Stop every active proxy session of this runspace
# Get-ZbxActiveProxyStatus writes each running session; Stop-ZbxActiveProxy binds its Name from the pipeline.
Start-ZbxActiveProxy -Name 'uplink-a' -ProxyName 'proxy-01' -Server 'zabbix.example:10051'
Start-ZbxActiveProxy -Name 'uplink-b' -ProxyName 'proxy-02' -Server 'zabbix.example:10051'
Get-ZbxActiveProxyStatus | Stop-ZbxActiveProxy -WarningAction SilentlyContinue
@(Get-ZbxActiveProxyStatus).Count
#endregion

#region Preview a stop with -WhatIf
# -WhatIf names the session it would stop and leaves it running. ConfirmImpact is Medium: -Confirm asks first.
Start-ZbxActiveProxy -Name 'uplink' -ProxyName 'proxy-01' -Server 'zabbix.example:10051'
Stop-ZbxActiveProxy -Name 'uplink' -WhatIf
Stop-ZbxActiveProxy -Name 'uplink' -WarningAction SilentlyContinue
#endregion
