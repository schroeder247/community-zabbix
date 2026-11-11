# This Source Code Form is subject to the terms of the Mozilla Public
# License, v. 2.0. Copyright (c) 2026 schroeder247. SPDX-License-Identifier: MPL-2.0
# Vocabulary: App endpoints; Mounted filesystems
#region Create a low-level discovery rule that receives its data
# A trapper LLD rule (-Type Trapper) takes discovery JSON sent to its key (Send-ZbxData); -HostId takes the host's
# id. The created rule is read back and written; Lifetime is how long lost entities are kept (7d by default).
$web = Get-ZbxHost -Name 'web-01'
New-ZbxDiscoveryRule -HostId $web.HostId -Name 'App endpoints' -Key 'app.endpoints.discovery' -Type Trapper |
    Select-Object ItemId, Name, Key, Type, Lifetime
#endregion

#region Create an agent discovery rule with its own schedule
# An active agent rule (-Type ZabbixActive) runs on the agent every -Delay; -LifetimeType/-Lifetime say when
# resources the rule no longer finds are deleted.
$web = Get-ZbxHost -Name 'web-01'
New-ZbxDiscoveryRule -HostId $web.HostId -Name 'Mounted filesystems' -Key 'vfs.fs.discovery' -Type ZabbixActive -Delay '1h' `
    -LifetimeType After -Lifetime '3d' | Select-Object Name, Key, Type, Delay, Lifetime
#endregion

#region Create the rule on a template
# On a template the rule is inherited by every host that links it; -HostId takes the template's id.
$template = Get-ZbxTemplate -Name 'Web App'
$null = New-ZbxDiscoveryRule -HostId $template.TemplateId -Name 'App endpoints' -Key 'app.endpoints.discovery' -Type Trapper
Get-ZbxDiscoveryRule -Template 'Web App' | Select-Object ItemId, Name, Key, HostId
#endregion
