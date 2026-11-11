# This Source Code Form is subject to the terms of the Mozilla Public
# License, v. 2.0. Copyright (c) 2026 schroeder247. SPDX-License-Identifier: MPL-2.0
# Vocabulary: Payments

#region Create a service that follows tagged problems
# A service's status comes from the problems whose tags match its -ProblemTag (built with
# New-ZbxServiceProblemTag). -Algorithm MostCritical takes the worst status; -SortOrder is required by the API.
$tag = New-ZbxServiceProblemTag -Tag 'service' -Operator Equal -Value 'checkout'
New-ZbxService -Name 'Checkout' -Algorithm MostCritical -Position 0 -ProblemTag $tag |
    Select-Object ServiceId, Name, Status, Algorithm
#endregion

#region Build a service tree
# -Child (or -Parent) takes service names, ids or objects, so a parent can be created over existing services.
# Get-ZbxService -SelectChildren reads the tree back.
$null = New-ZbxService -Name 'Payments' -Algorithm MostCritical -Position 1 -ProblemTag (New-ZbxServiceProblemTag -Tag 'service' -Value 'payments')
$null = New-ZbxService -Name 'Web checkout' -Algorithm MostCritical -Position 0 -Child 'Checkout', 'Payments' -Tag @{ tag = 'team'; value = 'web' }
(Get-ZbxService -Name 'Web checkout' -SelectChildren).Children.Name
#endregion

#region Raise the parent's status when two children are in trouble
# -StatusRule (built with New-ZbxServiceStatusRule) override the algorithm: here the parent becomes Disaster when at
# least two children are High or worse. -WhatIf previews without creating.
$rule = New-ZbxServiceStatusRule -Type AtLeastNWithStatusOrAbove -LimitValue 2 -LimitStatus High -NewStatus Disaster
New-ZbxService -Name 'Web checkout' -Algorithm MostCritical -Position 0 -StatusRule $rule -WhatIf
#endregion
