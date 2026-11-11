# This Source Code Form is subject to the terms of the Mozilla Public
# License, v. 2.0. Copyright (c) 2026 schroeder247. SPDX-License-Identifier: MPL-2.0
# Vocabulary: Escalate web problems; Restart web service
$null = New-ZbxScript -Name 'Restart web service' -Scope Action -Type Script -ExecuteOn Agent -Command 'systemctl restart nginx'

#region Send the default message to a user group
# One operation of type Message; -OperationMessageGroup takes user group objects (a builder resolves no names, so read the
# group first). default_msg = 1 sends the media type's default message; mediatypeid 0 (omitted) means every media.
$notify = New-ZbxActionOperation -OperationType Message -OperationMessageGroup (Get-ZbxUserGroup -Name 'Operators') -OperationMessage @{ default_msg = 1 }
New-ZbxAction -Name 'Notify on disaster' -EventSource Triggers -Operation $notify |
    Select-Object ActionId, Name, EventSource
#endregion

#region Escalate: notify first, run a script if the problem persists
# -EscalationStepFrom/-EscalationStepTo place an operation on the escalation ladder; step 2 starts one -EscalationPeriod after
# step 1. -OperationCommand names the global script (scope Action) and -OperationCommandHost the host to run it on.
$trigger = Get-ZbxTrigger -Name 'Too many requests on web-01'
$operations = @(
    New-ZbxActionOperation -OperationType Message -EscalationStepFrom 1 -EscalationStepTo 1 -OperationMessageGroup (Get-ZbxUserGroup -Name 'Operators') -OperationMessage @{ default_msg = 1 }
    New-ZbxActionOperation -OperationType Command -EscalationStepFrom 2 -EscalationStepTo 2 -OperationCommand @{ scriptid = (Get-ZbxScript -Name 'Restart web service').ScriptId } -OperationCommandHost (Get-ZbxHost -Name 'web-01')
)
$condition = New-ZbxActionCondition -ConditionType Trigger -Operator Equal -Value $trigger.TriggerId
$null = New-ZbxAction -Name 'Escalate web problems' -EventSource Triggers -EscalationPeriod '15m' -Filter @{ evaltype = 'AndOr'; conditions = @($condition) } -Operation $operations
(Get-ZbxAction -Name 'Escalate web problems' -SelectOperations).Operations | Format-Table operationtype, esc_step_from, esc_step_to
#endregion
