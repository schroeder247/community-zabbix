# This Source Code Form is subject to the terms of the Mozilla Public
# License, v. 2.0. Copyright (c) 2026 schroeder247. SPDX-License-Identifier: MPL-2.0
# Vocabulary: Close stale web problems

#region Build a condition and use it in a new correlation
# The builder writes one correlation-filter condition as a hashtable; New-ZbxCorrelation sends it in -Filter. This
# rule closes the old problem when a new event with the same "scope" tag value arrives (operation Old; New closes the new event instead).
$sameScope = New-ZbxCorrelationCondition -Type EventTagPair -OldTag 'scope' -NewTag 'scope'
New-ZbxCorrelation -Name 'Disaster correlation' -Filter @{ evaltype = 'AndOr'; conditions = @($sameScope) } -Operation @{ type = 'Old' } |
    Select-Object CorrelationId, Name, Status
#endregion

#region Combine conditions on tags and host group
# NewEventTag checks that the new event has a tag, NewEventHostgroup that it comes from a host group (-GroupId),
# NewEventTagValue compares a tag's value (-Operator). evaltype 'And' requires all of them.
$conditions = @(
    New-ZbxCorrelationCondition -Type NewEventTag -Tag 'resolved'
    New-ZbxCorrelationCondition -Type NewEventHostgroup -Operator Equal -GroupId (Get-ZbxHostGroup -Name 'Web servers').GroupId
    New-ZbxCorrelationCondition -Type NewEventTagValue -Tag 'env' -Operator Equal -Value 'prod'
)
$null = New-ZbxCorrelation -Name 'Close stale web problems' -Filter @{ evaltype = 'And'; conditions = $conditions } -Operation @{ type = 'Old' }, @{ type = 'New' }
(Get-ZbxCorrelation -Name 'Close stale web problems' -SelectFilter).Filter.conditions | Format-Table type, tag, operator, value, groupid
#endregion
