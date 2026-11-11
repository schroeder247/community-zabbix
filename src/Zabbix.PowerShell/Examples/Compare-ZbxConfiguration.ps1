# This Source Code Form is subject to the terms of the Mozilla Public
# License, v. 2.0. Copyright (c) 2026 schroeder247. SPDX-License-Identifier: MPL-2.0
<#
.SYNOPSIS
Shows what importing a configuration document would change.
.DESCRIPTION
The Compare-ZbxConfiguration cmdlet sends a configuration document to the server with configuration.importcompare and
writes the differences an import with the same rules would make, object by object, without changing anything. Run it
before Import-ZbxConfiguration to review an import. The before side of an updated object carries the server's current
values, including plaintext secrets, so a difference with secrets is redacted unless -Unredacted is given.
#>
$null = Set-ZbxTemplate -InputObject 'Web App' -Description 'Shop front end'

#region Preview what an edited template document would change
# configuration.importcompare: the result lists, per object family, what would be added, updated (before/after)
# or removed with these -Rules. Nothing is changed on the server. The server compares templates (and what they
# contain) only; a document's hosts are not compared.
$doc = Export-ZbxConfiguration -Template 'Web App' -Format Yaml
$edited = $doc -replace 'description: .*', "description: 'Shop front end, v2'"
$diff = $edited | Compare-ZbxConfiguration -Format Yaml -Rules @{ Templates = @{ UpdateExisting = $true } }
$diff.templates.updated | ForEach-Object { '{0}: {1} -> {2}' -f $_.after.template, $_.before.description, $_.after.description }
#endregion

#region Check that a template still matches its saved file
# An empty result means the server already matches the document for the families the rules cover; -Path reads
# the document from a file, in the format its extension names unless -Format says otherwise.
$path = Join-Path ([IO.Path]::GetTempPath()) 'web-app.json'
Export-ZbxConfiguration -Template 'Web App' -Path $path
if (-not (Compare-ZbxConfiguration -Path $path -Rules @{ Templates = @{ UpdateExisting = $true } })) { 'in sync' }
Set-ZbxTemplate -InputObject 'Web App' -Description 'changed by hand' | Out-Null
$drift = Compare-ZbxConfiguration -Path $path -Rules @{ Templates = @{ UpdateExisting = $true } }
$drift.templates.updated | ForEach-Object { 'drifted: {0} is now "{1}", the file says "{2}"' -f $_.before.template, $_.before.description, $_.after.description }
Remove-Item $path
#endregion
