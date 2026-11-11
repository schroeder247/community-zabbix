# This Source Code Form is subject to the terms of the Mozilla Public
# License, v. 2.0. Copyright (c) 2026 schroeder247. SPDX-License-Identifier: MPL-2.0
# Vocabulary: Notify on

$condition = New-ZbxActionCondition -ConditionType TriggerSeverity -Operator MoreEqual -Value 5
$notify = New-ZbxActionOperation -OperationType Message -OperationMessageGroup (Get-ZbxUserGroup -Name 'Operators') -OperationMessage @{ default_msg = 1 }
$null = New-ZbxAction -Name 'Notify on disaster' -EventSource Triggers -Filter @{ evaltype = 'AndOr'; conditions = @($condition) } -Operation $notify

#region Disable an action
# -InputObject takes the action's name, id or object; only the parameters given are sent (action.update). With
# -PassThru the changed action is read back and written to the pipeline; without it nothing is.
Set-ZbxAction -InputObject 'Notify on disaster' -Status Disabled -PassThru | Select-Object Name, Status
#endregion

#region Enable every action a search finds, through the pipeline
# Get-ZbxAction writes action objects; Set-ZbxAction binds each one and updates them in one batched call.
Get-ZbxAction -Name 'Notify on*' | Set-ZbxAction -Status Enabled -PassThru | Select-Object Name, Status
#endregion

#region Replace the filter
# -Filter replaces the whole filter: here the action is narrowed to severity High or above on 'Web servers' hosts.
$conditions = @(
    New-ZbxActionCondition -ConditionType TriggerSeverity -Operator MoreEqual -Value 4
    New-ZbxActionCondition -ConditionType HostGroup -Operator Equal -Value (Get-ZbxHostGroup -Name 'Web servers').GroupId
)
Set-ZbxAction -InputObject 'Notify on disaster' -Filter @{ evaltype = 'And'; conditions = $conditions }
(Get-ZbxAction -Name 'Notify on disaster' -SelectFilter).Filter.conditions | Format-Table conditiontype, operator, value
#endregion
