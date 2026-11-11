# This Source Code Form is subject to the terms of the Mozilla Public
# License, v. 2.0. Copyright (c) 2026 schroeder247. SPDX-License-Identifier: MPL-2.0
#region Build a problem tag and use it in a new service
# The builder writes one problem-tag hashtable (tag, operator, value); New-ZbxService sends it in -ProblemTag. The
# service then takes its status from problems tagged service=checkout.
$tag = New-ZbxServiceProblemTag -Tag 'service' -Operator Equal -Value 'checkout'
New-ZbxService -Name 'Checkout' -Algorithm MostCritical -Position 0 -ProblemTag $tag | Select-Object Name, Status
#endregion

#region Match several tags, one with a pattern
# -Operator Like matches when the problem's tag value contains the text. Several tags in -ProblemTag must all match.
$tags = @(
    New-ZbxServiceProblemTag -Tag 'service' -Operator Equal -Value 'checkout'
    New-ZbxServiceProblemTag -Tag 'env' -Operator Like -Value 'prod'
)
$null = Set-ZbxService -InputObject 'Checkout' -ProblemTag $tags
(Get-ZbxService -Name 'Checkout' -SelectProblemTags).ProblemTags | Format-Table Tag, Operator, Value
#endregion
