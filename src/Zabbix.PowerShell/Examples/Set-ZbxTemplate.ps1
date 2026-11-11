# This Source Code Form is subject to the terms of the Mozilla Public
# License, v. 2.0. Copyright (c) 2026 schroeder247. SPDX-License-Identifier: MPL-2.0
#region Describe a template
# -InputObject (position 0) takes the template's name, id or object.
Set-ZbxTemplate -InputObject 'Web App' -Description 'Monitors the web application tier' -PassThru | Select-Object HostName, Description
#endregion

#region Set a template's macros
# -Macro replaces the template's macros; a host linked to the template inherits them unless it sets its own.
Set-ZbxTemplate -InputObject 'Web App' -Macro @{ macro = '{$APP.PORT}'; value = '8080' }, @{ macro = '{$APP.TIMEOUT}'; value = '5s' }
#endregion
