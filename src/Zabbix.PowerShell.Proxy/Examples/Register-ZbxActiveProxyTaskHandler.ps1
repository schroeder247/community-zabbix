# This Source Code Form is subject to the terms of the Mozilla Public
# License, v. 2.0. Copyright (c) 2026 schroeder247. SPDX-License-Identifier: MPL-2.0
$null = New-ZbxProxy -Name 'proxy-01' -OperatingMode Active
Start-ZbxActiveProxy -Name 'uplink' -ProxyName 'proxy-01' -Server 'zabbix.example:10051' -RefreshInterval '2s'
Start-Sleep -Seconds 3

<#
.SYNOPSIS
Registers a scriptblock that runs the tasks the server gives a running active proxy session.
.DESCRIPTION
The Register-ZbxActiveProxyTaskHandler cmdlet sets the task handler of the active proxy session named in -Name: the
server hands a proxy tasks, such as a remote command, in the answer to an upload, and the session runs the scriptblock
for each, on its own runspace, with the task as its argument. The scriptblock's last output is reported back as the
task's result; a scriptblock that throws reports a failure with the exception's message. It writes the session only
with -PassThru.
#>
#region Answer remote commands
# The task carries the command; the handler's output is what the server shows as the result.
Register-ZbxActiveProxyTaskHandler -Name 'uplink' -ScriptBlock {
    param($task)
    "received task $($task.TaskId)"
}
#endregion

#region Refuse every task
# Throwing reports the task as failed, with the message as its reason; -PassThru writes the session.
Register-ZbxActiveProxyTaskHandler -Name 'uplink' -ScriptBlock { throw 'remote commands are disabled on this proxy' } -PassThru |
    Select-Object Name
#endregion

Stop-ZbxActiveProxy -Name 'uplink'
