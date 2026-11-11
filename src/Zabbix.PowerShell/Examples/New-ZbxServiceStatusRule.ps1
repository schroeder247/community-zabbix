# This Source Code Form is subject to the terms of the Mozilla Public
# License, v. 2.0. Copyright (c) 2026 schroeder247. SPDX-License-Identifier: MPL-2.0
$null = New-ZbxService -Name 'Checkout' -Algorithm MostCritical -Position 0 -ProblemTag (New-ZbxServiceProblemTag -Tag 'service' -Value 'checkout')

#region Build a status rule and use it in a new service
# The builder writes one status-rule hashtable; New-ZbxService sends it in -StatusRule. This one reads: if at
# least 1 child is High or worse, set the service to Disaster.
$rule = New-ZbxServiceStatusRule -Type AtLeastNWithStatusOrAbove -LimitValue 1 -LimitStatus High -NewStatus Disaster
New-ZbxService -Name 'Web checkout' -Algorithm MostCritical -Position 0 -Child 'Checkout' -StatusRule $rule |
    Select-Object Name, Algorithm
#endregion

#region Combine rules by count and by percentage
# The Percent types compare -LimitValue as a percentage of the children; each rule sets -NewStatus when it
# matches, and the highest resulting status wins. -SelectStatusRules reads the rules back.
$rules = @(
    New-ZbxServiceStatusRule -Type AtLeastPercentWithStatusOrAbove -LimitValue 50 -LimitStatus Average -NewStatus High
    New-ZbxServiceStatusRule -Type AtLeastNWithStatusOrAbove -LimitValue 2 -LimitStatus Disaster -NewStatus Disaster
)
$null = Set-ZbxService -InputObject 'Web checkout' -StatusRule $rules
(Get-ZbxService -Name 'Web checkout' -SelectStatusRules).StatusRules | Format-Table Type, LimitValue, LimitStatus, NewStatus
#endregion
