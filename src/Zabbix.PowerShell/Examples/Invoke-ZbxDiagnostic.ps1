# This Source Code Form is subject to the terms of the Mozilla Public
# License, v. 2.0. Copyright (c) 2026 schroeder247. SPDX-License-Identifier: MPL-2.0
<#
.SYNOPSIS
Asks the server for diagnostic information about its caches and processes.
.DESCRIPTION
The Invoke-ZbxDiagnostic cmdlet asks the server, with task.create, for the statistics of its history cache, value cache,
preprocessing, alerting and low-level discovery, the sections in -Request, or all of them when -Request is not given.
The server runs the task within seconds, and Get-ZbxTask then shows the statistics in the task's Result. The command
writes the task's id, and -WhatIf shows what it would ask.
#>
#region Ask for every section and read the result
# The task needs a moment to run; Result carries one entry per section.
$taskId = Invoke-ZbxDiagnostic
Start-Sleep -Seconds 5
(Get-ZbxTask -Id $taskId).Result
#endregion

#region Ask for the ten items with the most values in the history cache
# -Request is cast from a hashtable keyed by the API's field names.
Invoke-ZbxDiagnostic -Request @{ historycache = @{ stats = 'extend'; top = @{ values = 10 } } }
#endregion
