# This Source Code Form is subject to the terms of the Mozilla Public
# License, v. 2.0. Copyright (c) 2026 schroeder247. SPDX-License-Identifier: MPL-2.0
# Fire the trigger 'Too many requests on web-01' (last(/web-01/app.requests)>100). Values arrive once the server's configuration cache
# knows the new item (up to a minute).
$sw = [Diagnostics.Stopwatch]::StartNew()
do {
    Start-Sleep -Seconds 2
    $null = Send-ZbxHistory 'web-01' 'app.requests' 150 -Clock ([DateTimeOffset]::UtcNow.ToUnixTimeSeconds()) -Confirm:$false
} until ((Get-ZbxTrigger -Host 'web-01').Value -eq 'Problem' -or $sw.Elapsed.TotalSeconds -gt 240)

#region List a host's triggers
# -Host takes a host name, id or object. Value is the trigger's current state (Ok or Problem).
Get-ZbxTrigger -Host 'web-01' | Select-Object TriggerId, Name, Priority, Value
#endregion

#region Get a trigger by its name
# -Name matches the trigger's description (its name in the frontend) exactly; a * makes it a search.
Get-ZbxTrigger -Name 'Too many requests on web-01' -SelectHosts |
    Select-Object Name, @{ n = 'Host'; e = { $_.Hosts.TechnicalName } }, Priority
#endregion

#region Read a trigger's expression with its hosts and item keys
# Zabbix stores an expression with function ids ({36115}>100); -ExpandExpression asks trigger.get to write each function
# out with its host and item key, as it was created (last(/web-01/app.requests)>100).
Get-ZbxTrigger -Name 'Too many requests on web-01' -ExpandExpression | Select-Object Name, Expression
#endregion

#region Get the triggers that watch an item, through the pipeline
# A piped item binds -Item by its ItemId; -SelectItems adds the items each trigger reads.
Get-ZbxItem -Host 'web-01' -Name 'Requests' | Get-ZbxTrigger -SelectItems |
    Select-Object Name, @{ n = 'Items'; e = { $_.Items.Key -join ', ' } }
#endregion

#region Find the triggers of a group that are in the problem state
# A realistic check for a script: every trigger of the group whose Value is Problem, most severe first.
Get-ZbxTrigger -Group 'Web servers' -SelectHosts |
    Where-Object Value -EQ 'Problem' |
    Sort-Object Priority -Descending |
    Select-Object @{ n = 'Host'; e = { $_.Hosts.TechnicalName } }, Name, Priority
#endregion
