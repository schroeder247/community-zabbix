# This Source Code Form is subject to the terms of the Mozilla Public
# License, v. 2.0. Copyright (c) 2026 schroeder247. SPDX-License-Identifier: MPL-2.0
#region Build an operation that closes the old problems
# -Type is positional: Old closes the open problems the new event matches, New closes the new event itself.
New-ZbxCorrelationOperation Old
#endregion

#region Use it in an event correlation
# New-ZbxCorrelation takes the operation in -Operation, alone or with the other one.
$condition = New-ZbxCorrelationCondition NewEventTag -Tag 'scope'
New-ZbxCorrelation -Name 'Disaster correlation' -Filter @{ evaltype = 'AndOr'; conditions = @($condition) } -Operation (New-ZbxCorrelationOperation Old), (New-ZbxCorrelationOperation New) |
    Select-Object Name, Status
#endregion
