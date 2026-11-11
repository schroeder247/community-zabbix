# This Source Code Form is subject to the terms of the Mozilla Public
# License, v. 2.0. Copyright (c) 2026 schroeder247. SPDX-License-Identifier: MPL-2.0
<#
.SYNOPSIS
Checks a configuration document before it is imported.
.DESCRIPTION
The Test-ZbxConfiguration cmdlet reads a configuration document (text, or files with -Path, which takes wildcards, or -LiteralPath) in JSON, XML or
YAML, the format told from its first character or given with -Format, and reports whether it is well formed and which
plaintext secrets, such as passwords or PSKs, it carries. It runs on the client and sends nothing to a server. -Quiet
writes only $true or $false, for a script's if.
#>
#region Check an exported document for secrets
# Export-ZbxConfiguration redacts secrets by default; the report lists any that are still there.
Export-ZbxConfiguration -Host 'web-01' | Test-ZbxConfiguration
#endregion

#region Check that a document is well formed before importing it
# -Quiet writes $true or $false; the second document is cut off, so it is not well formed.
'{"zabbix_export":{"version":"7.0","host_groups":[{"uuid":"a571c0d144b14fd4a87a9d9b2aa9fcd6","name":"Linux servers"}]}}' | Test-ZbxConfiguration -Quiet
'{"zabbix_export":' | Test-ZbxConfiguration -Quiet
#endregion
