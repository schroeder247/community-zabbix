# This Source Code Form is subject to the terms of the Mozilla Public
# License, v. 2.0. Copyright (c) 2026 schroeder247. SPDX-License-Identifier: MPL-2.0
# Vocabulary: Register Linux agents; Notify and recover

#region Create an action that notifies a user group about disasters
# A trigger action (-EventSource Triggers) with one condition and one message operation, built with
# New-ZbxActionCondition and New-ZbxActionOperation. The created action is read back and written to the pipeline.
$condition = New-ZbxActionCondition -ConditionType TriggerSeverity -Operator MoreEqual -Value 5
$notify = New-ZbxActionOperation -OperationType Message -OperationMessageGroup (Get-ZbxUserGroup -Name 'Operators') -OperationMessage @{ default_msg = 1 }
New-ZbxAction -Name 'Notify on disaster' -EventSource Triggers -Filter @{ evaltype = 'AndOr'; conditions = @($condition) } -Operation $notify |
    Select-Object ActionId, Name, EventSource, Status
#endregion

#region Also notify when the problem is resolved, and start it disabled
# -RecoveryOperation runs when the problem closes (operationtype 11 = notify all involved); -Status Disabled
# creates the action without letting it fire until it is enabled with Set-ZbxAction.
$condition = New-ZbxActionCondition -ConditionType TriggerSeverity -Operator MoreEqual -Value 5
$notify = New-ZbxActionOperation -OperationType Message -OperationMessageGroup (Get-ZbxUserGroup -Name 'Operators') -OperationMessage @{ default_msg = 1 }
New-ZbxAction -Name 'Notify and recover' -EventSource Triggers -Status Disabled -Filter @{ evaltype = 'AndOr'; conditions = @($condition) } -Operation $notify -RecoveryOperation @{ operationtype = 11; opmessage = @{ default_msg = 1 } } |
    Select-Object Name, Status
#endregion

#region Register Linux agents automatically
# An autoregistration action: agents whose HostMetadata contains "Linux" are added as hosts, put into the
# 'Linux servers' group and linked to a template. -OperationGroup and -OperationTemplate take objects read with Get-Zbx*.
$condition = New-ZbxActionCondition -ConditionType HostMetadata -Operator Like -Value 'Linux'
$operations = @(
    New-ZbxActionOperation -OperationType HostAdd
    New-ZbxActionOperation -OperationType GroupAdd -OperationGroup (Get-ZbxHostGroup -Name 'Linux servers')
    New-ZbxActionOperation -OperationType TemplateAdd -OperationTemplate (Get-ZbxTemplate -Name 'Web App')
)
New-ZbxAction -Name 'Register Linux agents' -EventSource Autoregistration -Filter @{ evaltype = 'AndOr'; conditions = @($condition) } -Operation $operations |
    Select-Object Name, EventSource
#endregion

#region Preview the action without creating it
# -WhatIf prints what New-ZbxAction would do and sends nothing (ConfirmImpact is Medium, so without -WhatIf or
# -Confirm it creates the action straight away).
New-ZbxAction -Name 'Notify on disaster' -EventSource Triggers -WhatIf
#endregion
