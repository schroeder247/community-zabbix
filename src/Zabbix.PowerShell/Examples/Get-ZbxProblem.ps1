# This Source Code Form is subject to the terms of the Mozilla Public
# License, v. 2.0. Copyright (c) 2026 schroeder247. SPDX-License-Identifier: MPL-2.0
# Fire the trigger 'Too many requests on web-01' (last(/web-01/app.requests)>100). Values arrive once the server's configuration cache
# knows the new item (up to a minute).
$sw = [Diagnostics.Stopwatch]::StartNew()
do {
    Start-Sleep -Seconds 2
    $null = Send-ZbxHistory 'web-01' 'app.requests' 150 -Clock ([DateTimeOffset]::UtcNow.ToUnixTimeSeconds()) -Confirm:$false
} until ((Get-ZbxProblem -Host 'web-01') -or $sw.Elapsed.TotalSeconds -gt 240)

#region List a host's open problems
# -Host takes a host name, id or object. Clock is when the problem started, as Zabbix sends it (a DateTimeOffset); Time is the same instant as a local DateTime, a code property the module adds to every record with a Clock
# (Get-Member lists it under CodeProperty, not Property).
Get-ZbxProblem -Host 'web-01' |
    Select-Object EventId, Name, Severity, @{ n = 'Since'; e = { $_.Time } }
#endregion

#region Get the problems of every host in a group, through the pipeline
# A piped host group binds -Group by its GroupId; -SelectTags adds each problem's tags.
Get-ZbxHostGroup -Name 'Web servers' | Get-ZbxProblem -SelectTags | Select-Object Name, Severity, Acknowledged
#endregion

#region Find a problem by name
# -Name matches the problem name exactly (a * makes it a search); -Host narrows it to one host's problems.
Get-ZbxProblem -Name 'Too many requests on web-01' -Host 'web-01' | Select-Object EventId, ObjectId, Name
#endregion

#region Get only the severe problems
# -Severity takes one or more severities (NotClassified, Information, Warning, Average, High, Disaster) and the server
# filters on them: the same as -Filter @{ severities = @('High', 'Disaster') }.
Get-ZbxProblem -Severity High, Disaster | Select-Object Name, Severity, Acknowledged
#endregion

#region Report the severe, unacknowledged problems of a group
# A realistic check for a script: High and Disaster problems nobody has acknowledged yet (Acknowledged is True or
# False), oldest first. Confirm-ZbxProblem acknowledges them.
Get-ZbxProblem -Group 'Linux servers' -Severity High, Disaster |
    Where-Object { -not $_.Acknowledged } |
    Sort-Object Clock |
    Select-Object Name, Severity, EventId, Time
#endregion
