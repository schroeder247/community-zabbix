# This Source Code Form is subject to the terms of the Mozilla Public
# License, v. 2.0. Copyright (c) 2026 schroeder247. SPDX-License-Identifier: MPL-2.0
#region Build a tag
# New-ZbxTag writes a hashtable with the API's tag and value keys; nothing is sent to the server.
New-ZbxTag -Tag 'env' -Value 'prod'
#endregion

#region Create a host with tags built by New-ZbxTag
# Any -Tag parameter (hosts, templates, items, triggers...) casts from the same shape.
$tags = (New-ZbxTag -Tag 'env' -Value 'test'), (New-ZbxTag -Tag 'team' -Value 'shop')
New-ZbxHost -Host 'web-02' -Group 'Web servers' -Tag $tags | Out-Null
(Get-ZbxHost -Name 'web-02' -SelectTags).Tags | Select-Object Tag, Value
#endregion

#region Tag an item when creating it
# The same tag objects work for an item's -Tag; -Value is optional (a tag without a value).
$hostId = (Get-ZbxHost -Name 'web-01').HostId
New-ZbxItem -HostId $hostId -Name 'Errors' -Key 'app.errors' -Type Trapper -ValueType Unsigned `
    -Tag (New-ZbxTag -Tag 'component' -Value 'application'), (New-ZbxTag -Tag 'alerting') | Out-Null
(Get-ZbxItem -Host 'web-01' -Name 'Errors' -SelectTags).Tags | Select-Object Tag, Value
#endregion
