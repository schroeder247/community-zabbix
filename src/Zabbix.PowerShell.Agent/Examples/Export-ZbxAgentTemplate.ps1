# This Source Code Form is subject to the terms of the Mozilla Public
# License, v. 2.0. Copyright (c) 2026 schroeder247. SPDX-License-Identifier: MPL-2.0
Start-ZbxActiveAgent -Name 'web-agent' -Hostname 'web-02' -Server 'zabbix.example:10051'
Register-ZbxActiveAgentHandler -Name 'web-agent' -Key 'app.health' -ScriptBlock { 'ok' }
$null = Initialize-ZbxAgentHost -Session 'web-agent' -HostGroup 'Web servers'

<#
.SYNOPSIS
Exports the host an agent session provisioned, with its interface and items.
.DESCRIPTION
The Export-ZbxAgentTemplate cmdlet exports, with configuration.export, the host Initialize-ZbxAgentHost provisioned for
the agent session named in -Session: the host, its agent interface and its items, as a document Import-ZbxConfiguration
reads back. -Format chooses JSON (the default), XML or YAML, and -Path writes the document to a file instead of the
pipeline. Plaintext secrets are redacted unless -Unredacted is given.
#>
#region Export the agent's host as JSON
# The document's hosts section holds the host with its interface and items.
Export-ZbxAgentTemplate -Session 'web-agent' | ConvertFrom-Json | ForEach-Object { $_.zabbix_export.hosts.host }
#endregion

#region Save the export as YAML
# -Path writes the file and nothing to the pipeline; -NoClobber keeps a file that exists.
$path = Join-Path ([IO.Path]::GetTempPath()) "web-agent-$PID.yaml"
Export-ZbxAgentTemplate -Session 'web-agent' -Format Yaml -Path $path
Get-Content $path -TotalCount 5
Remove-Item $path
#endregion

Stop-ZbxActiveAgent -Name 'web-agent'
