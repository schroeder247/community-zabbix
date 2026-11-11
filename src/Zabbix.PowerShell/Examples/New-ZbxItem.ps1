# This Source Code Form is subject to the terms of the Mozilla Public
# License, v. 2.0. Copyright (c) 2026 schroeder247. SPDX-License-Identifier: MPL-2.0
#region Create a trapper item on a host
# A trapper item receives values pushed with Send-ZbxData (Zabbix 8.0 takes them only from the addresses in the
# {$TRAPPER.ALLOWED_HOSTS} macro, 127.0.0.1 by default); -HostId is the host's id. The item is read back and written.
$hostId = (Get-ZbxHost -Name 'web-01').HostId
New-ZbxItem -HostId $hostId -Name 'Queue length' -Key 'app.queue' -Type Trapper -ValueType Unsigned |
    Select-Object ItemId, Name, Key, Type, ValueType
#endregion

#region Create an agent item that polls every minute
# A polled agent item needs the host's agent interface (-InterfaceId) and an update interval (-Delay).
$h = Get-ZbxHost -Name 'web-01' -SelectInterfaces
New-ZbxItem -HostId $h.HostId -InterfaceId $h.Interfaces[0].InterfaceId -Name 'Uptime' -Key 'system.uptime' `
    -Type Zabbix -ValueType Unsigned -Units 'uptime' -Delay '1m' |
    Select-Object Name, Key, Delay, Units
#endregion

#region Add the same item to every host of a group, through the pipeline
# A piped host binds -HostId by its HostId property, so each host gets its own item.
Get-ZbxHost -Group 'Linux servers' |
    New-ZbxItem -Name 'Deployments' -Key 'app.deployments' -Type Trapper -ValueType Unsigned |
    Select-Object HostId, Key
#endregion

#region Create a calculated item on a template
# A piped template binds -HostId through its TemplateId. -Params is the calculated item's formula; the item is
# inherited by every host the template is linked to.
Get-ZbxTemplate -Name 'Web App' |
    New-ZbxItem -Name 'Requests per minute' -Key 'app.requests.rate' -Type Calculated -ValueType Float `
        -Params 'avg(//app.requests,1m)' -Delay '1m' -Units 'rpm' |
    Select-Object ItemId, Key, Params
#endregion
