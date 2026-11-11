# This Source Code Form is subject to the terms of the Mozilla Public
# License, v. 2.0. Copyright (c) 2026 schroeder247. SPDX-License-Identifier: MPL-2.0
# The help script HelpGenTests reads for the sample's Remove-SmpWidget: its comment-based help block replaces the
# class's synopsis and description, and each region is an example.
<#
.SYNOPSIS
Removes widgets from the store.
.DESCRIPTION
The Remove-SmpWidget cmdlet deletes the widget given to -InputObject.
It asks first unless -Force is given.
#>
$widget = Get-SmpWidget -Name 'gear'

#region Remove a widget
# The widget comes from the pipeline; -Force skips the prompt.
#
# A bare comment line starts a second paragraph.
$widget | Remove-SmpWidget -Force
#endregion
