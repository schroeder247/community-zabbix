# This Source Code Form is subject to the terms of the Mozilla Public
# License, v. 2.0. Copyright (c) 2026 schroeder247. SPDX-License-Identifier: MPL-2.0
# Setup: 'Checkout' follows problems tagged env=prod (web-01's host tag, which its problems inherit); a value over
# the threshold of the trigger 'Too many requests on web-01' opens such a problem. Resent until the server has the new item in its cache.
# Zabbix 8.0 limits trapper items to {$TRAPPER.ALLOWED_HOSTS} (127.0.0.1,::1) by default; allow any sender here.
$null = Get-ZbxItem -Host 'web-01' | Where-Object Key -EQ 'app.requests' | Set-ZbxItem -TrapperHosts '0.0.0.0/0,::/0'
$null = New-ZbxService -Name 'Checkout' -Algorithm MostCritical -Position 0 -ProblemTag (New-ZbxServiceProblemTag -Tag 'env' -Value 'prod')
$null = New-ZbxService -Name 'Web checkout' -Algorithm MostCritical -Position 0 -Child 'Checkout' -StatusRule (New-ZbxServiceStatusRule -Type AtLeastNWithStatusOrAbove -LimitValue 1 -LimitStatus High -NewStatus Disaster)
$deadline = [datetime]::UtcNow.AddSeconds(150)
do {
    $null = Send-ZbxData -HostName 'web-01' -Key 'app.requests' -Value 500 -Server 'zabbix.example:10051' -WarningAction SilentlyContinue
    Start-Sleep -Seconds 5
} until ((Get-ZbxService -Name 'Web checkout').Status -ne 'Ok' -or [datetime]::UtcNow -gt $deadline)
if ((Get-ZbxService -Name 'Web checkout').Status -eq 'Ok') { throw 'setup: the service did not leave Ok within 150 s' }

#region Get a service and its current status
# -Name matches exactly; a * turns it into a search. Status is the service's computed severity (Ok when no problem).
Get-ZbxService -Name 'Web checkout' | Select-Object ServiceId, Name, Status, Algorithm
#endregion

#region List the children of a service
# -Parent returns the services directly below the given one (a name, id or object).
Get-ZbxService -Parent 'Web checkout' | Select-Object Name, Status
#endregion

#region Show how a service computes its status
# The -Select* switches add the problem tags it follows and its status rules.
Get-ZbxService -Name 'Checkout', 'Web checkout' -SelectProblemTags -SelectStatusRules | ForEach-Object {
    [pscustomobject]@{
        Service     = $_.Name
        ProblemTags = ($_.ProblemTags | ForEach-Object { '{0}={1}' -f $_.Tag, $_.Value }) -join ', '
        Rules       = ($_.StatusRules | ForEach-Object { '{0} {1} {2} -> {3}' -f $_.Type, $_.LimitValue, $_.LimitStatus, $_.NewStatus }) -join '; '
    }
}
#endregion

#region List the services that are not OK
# Status is an enum on the record; anything but Ok means a matching problem is open (here the problem of the
# trigger 'Too many requests on web-01' makes 'Checkout' High and, through its status rule, 'Web checkout' Disaster).
Get-ZbxService | Where-Object Status -NE Ok | Select-Object Name, Status
#endregion
