# This Source Code Form is subject to the terms of the Mozilla Public
# License, v. 2.0. Copyright (c) 2026 schroeder247. SPDX-License-Identifier: MPL-2.0
#
# The fixtures every help example may assume. tools/Test-ZbxHelpExamples.ps1 runs this text, under the run's prefixed
# names, in a connected runspace before each example file. It is a script of its own, not text inside the harness, so
# the static binding that holds the help examples to real commands and parameters
# (HelpTests.The_live_examples_harness_binds_to_real_commands_and_parameters) holds these lines too.
$lin = New-ZbxHostGroup -Name 'Linux servers' -Confirm:$false
$web = New-ZbxHostGroup -Name 'Web servers' -Confirm:$false
$tg = New-ZbxTemplateGroup -Name 'Templates/Web' -Confirm:$false
$tpl = New-ZbxTemplate -Host 'Web App' -Group 'Templates/Web' -Confirm:$false
$h = New-ZbxHost -Host 'web-01' -Group 'Linux servers', 'Web servers' -InterfaceType Agent -InterfaceIP '127.0.0.1' -Tag @{ env = 'prod' } -Confirm:$false
$null = New-ZbxHost -Host 'db-01' -Group 'Linux servers' -InterfaceType Agent -InterfaceIP '127.0.0.2' -Confirm:$false
$null = New-ZbxItem -HostId $h.HostId -Name 'Requests' -Key 'app.requests' -Type Trapper -ValueType Unsigned -TrapperHosts '0.0.0.0/0,::/0' -Confirm:$false
$null = New-ZbxItem -HostId $h.HostId -Name 'Status' -Key 'app.status' -Type Trapper -ValueType Text -TrapperHosts '0.0.0.0/0,::/0' -Confirm:$false
$null = New-ZbxTrigger -Name 'Too many requests on web-01' -Expression 'last(/web-01/app.requests)>100' -Severity High -Confirm:$false
$null = New-ZbxUserMacro -HostId $h.HostId -Macro '{$APP.PORT}' -Value '8080' -Confirm:$false
$null = New-ZbxUserMacro -HostId $h.HostId -Macro '{$TRAPPER.ALLOWED_HOSTS}' -Value '0.0.0.0/0,::/0' -Confirm:$false
$null = New-ZbxUserGroup -Name 'Operators' -Confirm:$false
$null = Get-ZbxHost -Name 'web-01' | New-ZbxDiscoveryRule -Name 'Virtual machines' -Key 'vm.discovery' -Type ZabbixActive -Delay '1h' -Confirm:$false
$null = Get-ZbxHost -Name 'web-01' | New-ZbxDiscoveryRule -Name 'Database instances' -Key 'db.instances.discovery' -Type ZabbixActive -Delay '1h' -Confirm:$false
