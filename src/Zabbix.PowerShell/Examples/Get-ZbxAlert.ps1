# This Source Code Form is subject to the terms of the Mozilla Public
# License, v. 2.0. Copyright (c) 2026 schroeder247. SPDX-License-Identifier: MPL-2.0
# Setup: a webhook media type that only returns "OK" and a disabled one (its alerts fail), a user with both, an
# action on the trigger 'Too many requests on web-01', and values over and under the threshold until the server has written the alerts
# (values sent before the new objects reach the server's configuration cache are dropped or find no action).
# Zabbix 8.0 limits trapper items to {$TRAPPER.ALLOWED_HOSTS} (127.0.0.1,::1) by default; allow any sender here.
$null = Get-ZbxItem -Host 'web-01' | Where-Object Key -EQ 'app.requests' | Set-ZbxItem -TrapperHosts '0.0.0.0/0,::/0'
$mediaType = New-ZbxMediaType -Type Webhook -Name 'Service status' -Status Active -Script 'return "OK";'
$disabled = New-ZbxMediaType -Type Webhook -Name 'Mail relay' -Status Disabled -Script 'return "OK";'
$user = New-ZbxUser -UserName 'jdoe' -Password (ConvertTo-SecureString 'Chang3-me-at-first-login!' -AsPlainText -Force) -RoleId (Get-ZbxRole -Name 'Super admin role').RoleId -UserGroup 'Operators' -Media @{ mediatypeid = $mediaType.MediaTypeId; sendto = @('ops') }, @{ mediatypeid = $disabled.MediaTypeId; sendto = @('relay') }
$condition = New-ZbxActionCondition -ConditionType Trigger -Operator Equal -Value (Get-ZbxTrigger -Name 'Too many requests on web-01').TriggerId
$notify = New-ZbxActionOperation -OperationType Message -OperationMessageUser $user -OperationMessage @{ default_msg = 0; subject = 'Problem: {EVENT.NAME}'; message = 'Host {HOST.NAME}: {EVENT.NAME}' }
$null = New-ZbxAction -Name 'Notify on disaster' -EventSource Triggers -Filter @{ evaltype = 'AndOr'; conditions = @($condition) } -Operation $notify
$deadline = [datetime]::UtcNow.AddSeconds(150)
$value = 500
do {
    $null = Send-ZbxData -HostName 'web-01' -Key 'app.requests' -Value $value -Server 'zabbix.example:10051' -WarningAction SilentlyContinue
    $value = if ($value -eq 500) { 50 } else { 500 }
    Start-Sleep -Seconds 5
} until ((Get-ZbxAlert -Action 'Notify on disaster' -CountOutput) -ge 2 -or [datetime]::UtcNow -gt $deadline)
if ((Get-ZbxAlert -Action 'Notify on disaster' -CountOutput) -lt 2) { throw 'setup: no alerts within 150 s' }

#region Get the alerts an action sent
# -Action takes an action's name, id or object. Each ZabbixAlert is one message (or command) the server sent,
# with its recipient, status and any error.
Get-ZbxAlert -Action 'Notify on disaster' | Select-Object AlertId, SendTo, Subject, Status, Error
#endregion

#region Get the latest alerts about a host, with the media type used
# -Host narrows to alerts about the host's events; sort by clock, newest first, and keep ten. -SelectMediaTypes
# adds the media type to each alert.
Get-ZbxAlert -Host 'web-01' -SelectMediaTypes -SortField clock -SortOrder Descending -Limit 10 |
    Select-Object Time, SendTo,
        @{ n = 'Media'; e = { $_.MediaTypes.Name } }, Status
#endregion

#region Find alerts that failed to go out
# Status is an enum on the record; failed alerts carry the reason in Error (here: the 'Mail relay' media type is
# disabled).
Get-ZbxAlert -Limit 100 -SortField clock -SortOrder Descending | Where-Object Status -EQ Failed |
    Select-Object AlertId, SendTo, Error
#endregion
