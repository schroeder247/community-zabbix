# This Source Code Form is subject to the terms of the Mozilla Public
# License, v. 2.0. Copyright (c) 2026 schroeder247. SPDX-License-Identifier: MPL-2.0
<#
.SYNOPSIS
Imports a configuration document into the server.
.DESCRIPTION
The Import-ZbxConfiguration cmdlet applies a configuration document (text, or files with -Path, which takes wildcards, or -LiteralPath) in JSON, XML
or YAML (-Format, else a file's extension, else the text's first character) with configuration.import, creating or changing the hosts, templates, groups, maps,
media types and other objects it holds. -Rules says, for each kind of object, whether the import creates what is
missing, updates what exists and deletes what the document leaves out. Compare-ZbxConfiguration shows what an import
would change first. It asks for confirmation unless -Confirm:$false or -Force is given.
#>
$null = Set-ZbxHost 'web-01' -Description 'Front end'

#region Restore a deleted template from an export
# -Rules says what each object family may do (the typed rules of configuration.import; a hashtable casts to it).
# An import always asks for a second confirmation, so a script passes -Confirm:$false (or -Force).
$backup = Export-ZbxConfiguration -Template 'Web App' -Format Yaml
Remove-ZbxTemplate -InputObject 'Web App' -Confirm:$false
$backup | Import-ZbxConfiguration -Format Yaml -Rules @{
    Templates = @{ CreateMissing = $true }
    Items     = @{ CreateMissing = $true }
} -Confirm:$false | Out-Null
Get-ZbxTemplate -Name 'Web App' | Select-Object TemplateId, TechnicalName
#endregion

#region Put a host back the way a file describes it
# -Path reads the document from a file, in the format its extension names (.yaml here, as Export-ZbxConfiguration
# wrote it). UpdateExisting overwrites the host's current settings with the file's.
$path = Join-Path ([IO.Path]::GetTempPath()) 'web-01.yaml'
Export-ZbxConfiguration -Host 'web-01' -Path $path
Set-ZbxHost 'web-01' -Description 'changed by hand' | Out-Null
Import-ZbxConfiguration -Path $path -Rules @{ Hosts = @{ UpdateExisting = $true } } -Confirm:$false | Out-Null
Get-ZbxHost -Name 'web-01' | Select-Object TechnicalName, Description
Remove-Item $path
#endregion

#region Preview an import
# -WhatIf names the document without sending it; Compare-ZbxConfiguration shows what would change.
Export-ZbxConfiguration -Host 'web-01' | Import-ZbxConfiguration -Rules @{ Hosts = @{ UpdateExisting = $true } } -WhatIf
#endregion
