# This Source Code Form is subject to the terms of the Mozilla Public
# License, v. 2.0. Copyright (c) 2026 schroeder247. SPDX-License-Identifier: MPL-2.0
$null = Set-ZbxHost 'web-01' -Template 'Web App'
$null = Get-ZbxTemplate -Name 'Web App' | New-ZbxItem -Name 'Uptime' -Key 'app.uptime' -Type Trapper -ValueType Unsigned

#region Get a template by name
# -Name matches the template's technical name exactly; TechnicalName and VisibleName carry the API's host and name.
Get-ZbxTemplate -Name 'Web App' | Select-Object TemplateId, TechnicalName, VisibleName
#endregion

#region Get the templates linked to a host
# -Host takes a host name, id or object.
Get-ZbxTemplate -Host 'web-01' | Select-Object TemplateId, TechnicalName
#endregion

#region List the templates of a template group with their items
# -Group takes template group names, ids or objects; -SelectItems adds each template's items.
Get-ZbxTemplate -Group 'Templates/Web' -SelectItems |
    ForEach-Object { '{0}: {1}' -f $_.TechnicalName, ($_.Items.Key -join ', ') }
#endregion

#region Inspect a built-in template's macros
# The -Select* switches add related objects; here the macros that tune 'Linux by Zabbix agent'.
Get-ZbxTemplate -Name 'Linux by Zabbix agent' -SelectMacros |
    ForEach-Object Macros | Select-Object -First 5 Macro, Value
#endregion
