# This Source Code Form is subject to the terms of the Mozilla Public
# License, v. 2.0. Copyright (c) 2026 schroeder247. SPDX-License-Identifier: MPL-2.0
# Let the problems of the trigger 'Too many requests on web-01' be closed by hand, then fire it (last(/web-01/app.requests)>100). Values arrive
# once the server's configuration cache knows the new item (up to a minute).
Set-ZbxTrigger -InputObject 'Too many requests on web-01' -ManualClose
$sw = [Diagnostics.Stopwatch]::StartNew()
do {
    Start-Sleep -Seconds 2
    $null = Send-ZbxHistory 'web-01' 'app.requests' 150 -Clock ([DateTimeOffset]::UtcNow.ToUnixTimeSeconds()) -Confirm:$false
} until ((Get-ZbxProblem -Host 'web-01') -or $sw.Elapsed.TotalSeconds -gt 240)

<#
.DESCRIPTION
The Confirm-ZbxProblem cmdlet updates problems with event.acknowledge, the call behind the frontend's Update problem
form. It acknowledges the problems given to -InputObject (event ids, problem names, or problems piped from
Get-ZbxProblem; an event piped from Get-ZbxEvent binds by its EventId) and, in the same update, adds a -Message,
changes the -Severity, suppresses them (-Suppress, or -SuppressUntil a time), ends a suppression (-Unsuppress), closes
them (-Close, where the trigger allows manual close) or ranks them (-CauseEventId makes them symptoms of that problem,
-ConvertToCause makes them causes again). -Unacknowledge removes the acknowledgement instead, and -NoAcknowledge makes
only the other changes. Every problem the pipeline brings is updated in one call; the command writes the problems
only with -PassThru, and -WhatIf shows what it would change. Pairs Zabbix refuses in one update (-Suppress with
-Unsuppress, -Close with a suppression, -CauseEventId with -ConvertToCause) are refused before anything is sent.
#>
#region Acknowledge a problem with a message
# Get-ZbxProblem writes the host's open problems; -PassThru writes them back, Acknowledged now True.
Get-ZbxProblem -Host 'web-01' | Confirm-ZbxProblem -Message 'Looking into it' -PassThru | Select-Object Name, Severity, Acknowledged
#endregion

#region Hand a problem back
# -Unacknowledge removes the acknowledgement; the message says why. -InputObject takes the problem's name.
Confirm-ZbxProblem -InputObject 'Too many requests on web-01' -Unacknowledge -Message 'Handing over to the night shift'
#endregion

#region Acknowledge every severe problem nobody has acknowledged yet
# A realistic morning check: -Severity keeps High and Disaster problems, Where-Object the unacknowledged ones.
Get-ZbxProblem -Group 'Web servers' -Severity High, Disaster |
    Where-Object { -not $_.Acknowledged } |
    Confirm-ZbxProblem -Message 'Seen by the on-call engineer'
#endregion

#region Raise the severity and silence a problem for two hours
# -NoAcknowledge leaves the acknowledgement as it is; -SuppressUntil takes a date and implies -Suppress. The server
# applies the suppression a moment later, so the problem's Suppressed turns True on its next read.
Get-ZbxProblem -Host 'web-01' |
    Confirm-ZbxProblem -NoAcknowledge -Severity Disaster -SuppressUntil (Get-Date).AddHours(2) -PassThru |
    Select-Object Name, Severity
#endregion

#region Close a problem once it is fixed
# -Close needs a trigger that allows manual close (Set-ZbxTrigger -ManualClose); the server resolves the problem
# shortly after.
Get-ZbxProblem -Host 'web-01' | Confirm-ZbxProblem -Close -Message 'Fixed: the service was restarted'
#endregion
