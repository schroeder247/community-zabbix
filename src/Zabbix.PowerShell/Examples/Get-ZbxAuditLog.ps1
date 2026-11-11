# This Source Code Form is subject to the terms of the Mozilla Public
# License, v. 2.0. Copyright (c) 2026 schroeder247. SPDX-License-Identifier: MPL-2.0
$null = New-ZbxHost -Host 'web-02' -Group 'Web servers'
$null = Set-ZbxHost -InputObject 'web-02' -Status Disabled

#region Show the latest changes
# Audit records are returned oldest first unless sorted; -SortField clock -SortOrder Descending with -Limit gives
# the newest. Action is a number (0 add, 1 update, 2 delete, 8 login ...).
Get-ZbxAuditLog -SortField clock -SortOrder Descending -Limit 5 |
    Select-Object Time, Username, Action, ResourceName
#endregion

#region Show what changed on one object
# -Filter takes auditlog.get's own parameters: here the records whose resourcename is the host, in the last hour.
# Details holds the changed fields as JSON.
$hour = [DateTimeOffset]::UtcNow.AddHours(-1).ToUnixTimeSeconds()
Get-ZbxAuditLog -Filter @{ filter = @{ resourcename = 'web-02' }; time_from = $hour } |
    Select-Object Action, Details
#endregion

#region List a user's logins
# -User takes a username, id or object; with -Filter the server keeps only action 8 (login), newest first.
Get-ZbxAuditLog -User 'Admin' -Filter @{ filter = @{ action = 8 }; limit = 5 } -SortField clock -SortOrder Descending |
    Select-Object Time, Ip
#endregion
