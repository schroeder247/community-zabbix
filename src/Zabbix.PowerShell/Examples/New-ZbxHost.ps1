# This Source Code Form is subject to the terms of the Mozilla Public
# License, v. 2.0. Copyright (c) 2026 schroeder247. SPDX-License-Identifier: MPL-2.0
# Vocabulary: web-03
#region Create a host in a group with an agent interface
# -Group takes host group names, ids or objects. -InterfaceType/-InterfaceIP describe one main interface
# (port 10050 for an agent unless -InterfacePort says otherwise). The created host is read back and written.
New-ZbxHost -Host 'web-02' -Group 'Web servers' -InterfaceType Agent -InterfaceIP '10.0.0.12' |
    Select-Object HostId, HostName, Status
#endregion

#region Create a host that is reached by DNS name
# -UseIP:$false makes the shorthand interface connect by -InterfaceDns instead of an IP address.
New-ZbxHost -Host 'web-03' -Group 'Web servers' -InterfaceType Agent -InterfaceDns 'web-03.example.com' -UseIP:$false |
    Select-Object HostId, HostName
Get-ZbxHost -Name 'web-03' -SelectInterfaces | ForEach-Object Interfaces | Select-Object Type, UseIP, Dns, Port
#endregion

Remove-ZbxHost -InputObject 'web-02', 'web-03' -Confirm:$false

#region Create a host with a template, tags and a macro
# -Template links templates by name; -Tag and -Macro take hashtables (or New-ZbxTag objects) shaped like the
# API's tag and macro objects.
New-ZbxHost -Host 'web-02' -Group 'Web servers', 'Linux servers' -Template 'Web App' `
    -Tag @{ tag = 'env'; value = 'test' }, @{ tag = 'team'; value = 'shop' } `
    -Macro @{ macro = '{$APP.PORT}'; value = '9090' } `
    -InterfaceType Agent -InterfaceIP '10.0.0.12' | Out-Null
Get-ZbxHost -Name 'web-02' -SelectParentTemplates -SelectTags -SelectMacros |
    Select-Object HostName, @{ n = 'Templates'; e = { $_.ParentTemplates.HostName } },
        @{ n = 'Tags'; e = { ($_.Tags | ForEach-Object { '{0}={1}' -f $_.Tag, $_.Value }) -join ', ' } },
        @{ n = 'Macros'; e = { ($_.Macros | ForEach-Object { '{0}={1}' -f $_.Macro, $_.Value }) -join ', ' } }
#endregion

Remove-ZbxHost -InputObject 'web-02' -Confirm:$false

#region Create several hosts from a list
# A realistic script: one New-ZbxHost per record. -WhatIf would list the hosts without creating them.
$servers = @(
    [pscustomobject]@{ Name = 'web-02'; IP = '10.0.0.12' }
    [pscustomobject]@{ Name = 'web-03'; IP = '10.0.0.13' }
)
$servers | ForEach-Object {
    New-ZbxHost -Host $_.Name -Group 'Web servers' -InterfaceType Agent -InterfaceIP $_.IP
} | Select-Object HostId, HostName
#endregion
