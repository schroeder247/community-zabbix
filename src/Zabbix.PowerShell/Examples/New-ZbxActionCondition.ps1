# This Source Code Form is subject to the terms of the Mozilla Public
# License, v. 2.0. Copyright (c) 2026 schroeder247. SPDX-License-Identifier: MPL-2.0
# Vocabulary: Web alerts

#region Build a condition and use it in a new action
# The builder only writes a hashtable in the wire shape of one filter condition; nothing reaches the server until
# New-ZbxAction sends it in -Filter. Operator MoreEqual with value 5 reads "severity is Disaster or higher".
$severity = New-ZbxActionCondition -ConditionType TriggerSeverity -Operator MoreEqual -Value 5
$notify = New-ZbxActionOperation -OperationType Message -OperationMessageGroup (Get-ZbxUserGroup -Name 'Operators') -OperationMessage @{ default_msg = 1 }
New-ZbxAction -Name 'Notify on disaster' -EventSource Triggers -Filter @{ evaltype = 'AndOr'; conditions = @($severity) } -Operation $notify |
    Select-Object ActionId, Name, Status
#endregion

#region Combine several conditions with a custom formula
# -FormulaId labels each condition so the filter's formula can refer to it; -Value2 carries the tag name of an
# EventTagValue condition, -Value its value. Get-ZbxAction -SelectFilter reads the stored filter back.
$conditions = @(
    New-ZbxActionCondition -ConditionType HostGroup -Operator Equal -Value (Get-ZbxHostGroup -Name 'Web servers').GroupId -FormulaId A
    New-ZbxActionCondition -ConditionType TriggerSeverity -Operator MoreEqual -Value 4 -FormulaId B
    New-ZbxActionCondition -ConditionType EventTagValue -Operator Equal -Value2 'env' -Value 'prod' -FormulaId C
)
$notify = New-ZbxActionOperation -OperationType Message -OperationMessageGroup (Get-ZbxUserGroup -Name 'Operators') -OperationMessage @{ default_msg = 1 }
$null = New-ZbxAction -Name 'Web alerts' -EventSource Triggers -Filter @{ evaltype = 'Expression'; formula = 'A and (B or C)'; conditions = $conditions } -Operation $notify
(Get-ZbxAction -Name 'Web alerts' -SelectFilter).Filter.conditions | Format-Table formulaid, conditiontype, operator, value, value2
#endregion
