# This Source Code Form is subject to the terms of the Mozilla Public
# License, v. 2.0. Copyright (c) 2026 schroeder247. SPDX-License-Identifier: MPL-2.0
$null = New-ZbxProxy -Name 'proxy-01' -OperatingMode Active
Start-ZbxActiveProxy -Name 'uplink' -ProxyName 'proxy-01' -Server 'zabbix.example:10051' -RefreshInterval '2s'
Start-Sleep -Seconds 3

<#
.SYNOPSIS
Changes how often a running active proxy session pulls its configuration.
.DESCRIPTION
The Set-ZbxActiveProxy cmdlet sets the -RefreshInterval of the active proxy session named in -Name while it keeps
running, without losing the configuration it holds; the next pull follows the new interval. The interval is seconds or
a time span. The command writes the session only with -PassThru, and -WhatIf shows the change.
#>
#region Pull the configuration every minute
# The interval takes Zabbix's time suffixes ('1m', '30s'); a number is seconds, and a time span works too.
Set-ZbxActiveProxy -Name 'uplink' -RefreshInterval '1m'
#endregion

#region Pull more often while testing, and show the session
# -PassThru writes the session after the change.
Set-ZbxActiveProxy -Name 'uplink' -RefreshInterval 5 -PassThru | Select-Object Name, Started
#endregion

Stop-ZbxActiveProxy -Name 'uplink'
