# This Source Code Form is subject to the terms of the Mozilla Public
# License, v. 2.0. Copyright (c) 2026 schroeder247. SPDX-License-Identifier: MPL-2.0
#region Get the last day's events of a host
# -Host takes a host's name, id or object; -From takes a date, Unix seconds or a clock.
Get-ZbxEvent -Host 'web-01' -From (Get-Date).AddDays(-1) | Select-Object EventId, Name, Severity, Clock
#endregion

#region Get the newest trigger events with their tags
# -SortField and -SortOrder sort on the server; -Limit keeps the first ten, and -SelectTags adds each event's tags.
Get-ZbxEvent -Group 'Linux servers' -SortField clock -SortOrder Descending -Limit 10 -SelectTags |
    Select-Object Name, Severity, @{ n = 'Tags'; e = { ($_.Tags | ForEach-Object { '{0}={1}' -f $_.Tag, $_.Value }) -join ', ' } }
#endregion

#region Find the events with a name
# A * in -Name searches instead of matching exactly.
Get-ZbxEvent -Name 'Too many requests*' -Limit 5 | Select-Object EventId, Name, Clock
#endregion
