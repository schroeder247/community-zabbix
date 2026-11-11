# This Source Code Form is subject to the terms of the Mozilla Public
# License, v. 2.0. Copyright (c) 2026 schroeder247. SPDX-License-Identifier: MPL-2.0
<#
.SYNOPSIS
Gets the frontend's global settings.
.DESCRIPTION
The Get-ZbxSetting cmdlet reads the global settings of Administration > General with settings.get: the default theme
and language, the work period, the severity names and colors, the login lockout and the other frontend options. It
writes one ZabbixSettingsRecord object and needs a connection made with Connect-ZbxServer. Set-ZbxSetting changes the
settings.
#>
#region Read the work period and the default theme
# WorkPeriod is the days and hours graphs shade as working time.
Get-ZbxSetting | Select-Object WorkPeriod, DefaultTheme, DefaultLanguage
#endregion

#region List the severity names
# SeverityName0 to SeverityName5 are the six severities, from Not classified to Disaster.
$settings = Get-ZbxSetting
0..5 | ForEach-Object { $settings."SeverityName$_" }
#endregion
