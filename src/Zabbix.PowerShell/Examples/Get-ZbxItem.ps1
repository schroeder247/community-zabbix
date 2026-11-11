# This Source Code Form is subject to the terms of the Mozilla Public
# License, v. 2.0. Copyright (c) 2026 schroeder247. SPDX-License-Identifier: MPL-2.0
# Values arrive once the server's configuration cache knows the new item (up to a minute).
$sw = [Diagnostics.Stopwatch]::StartNew()
do {
    Start-Sleep -Seconds 2
    $null = Send-ZbxHistory 'web-01' 'app.requests' 42 -Clock ([DateTimeOffset]::UtcNow.ToUnixTimeSeconds()) -Confirm:$false
} until ((Get-ZbxItem -Host 'web-01' -Name 'Requests').LastTime -or $sw.Elapsed.TotalSeconds -gt 240)

#region List a host's items
# -Host takes a host name, id or object. Key is the item key (the API's key_ field).
Get-ZbxItem -Host 'web-01' | Select-Object ItemId, Name, Key, Type, ValueType
#endregion

#region Find items by name with a wildcard
# A * in -Name searches instead of matching exactly; combine it with -Host to stay on one host. LastClock is the
# time of the latest value, and the Unix epoch before the first; LastTime is that as a local date, empty before the first.
Get-ZbxItem -Name 'Req*' -Host 'web-01' |
    Select-Object Name, Key, LastValue, LastTime
#endregion

#region Search the items of every host in a group, through the pipeline
# A piped host binds -Host by its HostId; -Search is item.get's substring search, keyed by the API field name.
Get-ZbxHost -Group 'Web servers' | Get-ZbxItem -Search @{ key_ = 'app.' } | Select-Object HostId, Key
#endregion

#region Show the triggers that watch an item
# -SelectTriggers adds each item's triggers to its Triggers property.
Get-ZbxItem -Host 'web-01' -SelectTriggers |
    Where-Object { $_.Triggers } |
    ForEach-Object { '{0}: {1}' -f $_.Key, ($_.Triggers.Name -join '; ') }
#endregion
