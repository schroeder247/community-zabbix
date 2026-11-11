# This Source Code Form is subject to the terms of the Mozilla Public
# License, v. 2.0. Copyright (c) 2026 schroeder247. SPDX-License-Identifier: MPL-2.0
<#
.SYNOPSIS
Exports hosts, templates and other objects as a configuration document.
#>
#region Export a template as YAML
# -Template takes template names, ids or objects; the document is written to the pipeline as one string
# (configuration.export). -Format is json by default, or xml/yaml.
Export-ZbxConfiguration -Template 'Web App' -Format Yaml
#endregion

#region Export the hosts of a group to a file
# -HostReference (alias -Host) takes host names, ids or objects, so the hosts Get-ZbxHost finds can be passed straight in (-HostGroup
# exports the group objects themselves, not their hosts). -Path writes the document to a file, in the format its
# extension names (.json, .xml, .yaml or .yml) unless -Format says so; a -Format the extension contradicts is refused.
$path = Join-Path ([IO.Path]::GetTempPath()) 'linux-servers.json'
Export-ZbxConfiguration -Host (Get-ZbxHost -Group 'Linux servers') -Path $path
(Get-Content $path -Raw | ConvertFrom-Json).zabbix_export.hosts | Select-Object host, @{ n = 'items'; e = { ($_.items | Measure-Object).Count } }
Remove-Item $path
#endregion

#region Export the hosts a search finds, through the pipeline
# Piped hosts bind -HostReference; the whole selection becomes one document.
Get-ZbxHost -Group 'Web servers' | Export-ZbxConfiguration -Format Xml |
    ForEach-Object { ([xml]$_).zabbix_export.hosts.host.host }
#endregion

#region Export a media type, secrets redacted
# Plaintext media type, IPMI and OAuth secrets are redacted by default; -Unredacted keeps them (and asks first in
# an interactive session). Built-in objects such as the 'Email' media type may be exported like any other.
(Export-ZbxConfiguration -MediaType 'Email' -Format Yaml) -split '\r?\n' | Select-String -Pattern '^\s+(- )?(name|type|smtp_\w+):'
#endregion
