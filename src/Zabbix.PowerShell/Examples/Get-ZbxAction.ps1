# This Source Code Form is subject to the terms of the Mozilla Public
# License, v. 2.0. Copyright (c) 2026 schroeder247. SPDX-License-Identifier: MPL-2.0
$condition = New-ZbxActionCondition -ConditionType TriggerSeverity -Operator MoreEqual -Value 5
$notify = New-ZbxActionOperation -OperationType Message -OperationMessageGroup (Get-ZbxUserGroup -Name 'Operators') -OperationMessage @{ default_msg = 1 }
$null = New-ZbxAction -Name 'Notify on disaster' -EventSource Triggers -Filter @{ evaltype = 'AndOr'; conditions = @($condition) } -Operation $notify

#region Get an action by name
# -Name matches the action's name exactly; a * turns it into a search. The result is a ZabbixAction record.
Get-ZbxAction -Name 'Notify on disaster' | Select-Object ActionId, Name, EventSource, Status, EscalationPeriod
#endregion

#region Show an action's conditions and operations
# The -Select* switches add the related objects (action.get's selectFilter, selectOperations) to each action.
$action = Get-ZbxAction -Name 'Notify on disaster' -SelectFilter -SelectOperations
$action.Filter.conditions | Format-Table conditiontype, operator, value
$action.Operations | Format-Table operationtype, esc_step_from, esc_step_to
#endregion

#region Find the actions that notify a user group
# -UserGroup takes a user group's name, id or object and returns the actions whose operations send to it; useful
# before deleting or renaming the group.
Get-ZbxAction -UserGroup 'Operators' | Select-Object Name, Status
#endregion

#region List the disabled actions
# Status is an enum on the record, so it filters like any PowerShell property.
Get-ZbxAction | Where-Object Status -EQ Disabled | Select-Object Name
#endregion
