# This Source Code Form is subject to the terms of the Mozilla Public
# License, v. 2.0. Copyright (c) 2026 schroeder247. SPDX-License-Identifier: MPL-2.0
<#
.SYNOPSIS
    Stages the three PowerShell modules as module folders under one root: Community.Zabbix, Community.Zabbix.Agent and Community.Zabbix.Proxy, the
    layout the PowerShell Gallery installs and a PSModulePath entry finds (docs/adr/0003-powershell-module-split.md).

.DESCRIPTION
    Each module project's build output is its module folder (src/Zabbix.PowerShell, src/Zabbix.PowerShell.Agent,
    src/Zabbix.PowerShell.Proxy; bin/<Configuration>/net10.0). A sibling module's build output also carries the
    files of the module Community.Zabbix it references (Community.Zabbix.PowerShell.dll, Community.Zabbix.Sdk.dll, Community.Zabbix.Wire.dll, Community.Zabbix.psd1, their help and views)
    — the build copies a project reference's output. Community.Zabbix.Agent and Community.Zabbix.Proxy require Community.Zabbix (RequiredModules), so
    those files are loaded from the Community.Zabbix folder: staging leaves out every file of a sibling's output that the Community.Zabbix
    folder already holds with the same bytes at the same relative path. What a sibling keeps is what only it needs
    (Community.Zabbix.Agent: the hosts, the Zabbix.Proxy listener and the socket transport; Community.Zabbix.Proxy: the same listener stack),
    and its own LICENSE and THIRD-PARTY-NOTICES.md, since each module is published alone.

    A staged folder carries what pwsh loads and its notices, nothing else: the build leaves out PowerShell's own native files (the
    module targets), and staging leaves out the assemblies' XML documentation and their PDBs (a module's help is its
    MAML in en-US/, built from that documentation; the symbols ship with the NuGet packages' snupkg).

    The release workflow zips and publishes the staged folders; tests/lab/live.sh and the module's process tests import
    from them by name (Import-Module Community.Zabbix.Proxy, through PSModulePath), as an installed module is.

    -WhatIf lists the module folders it would replace and changes nothing.

.EXAMPLE
    pwsh -NoProfile -File tools/Stage-ZbxModules.ps1 -Configuration Release -Destination stage
    $env:PSModulePath = "$PWD/stage" + [IO.Path]::PathSeparator + $env:PSModulePath; Import-Module Community.Zabbix.Proxy
#>
[CmdletBinding(SupportsShouldProcess)]
param(
    # The build configuration whose output is staged.
    [ValidateSet('Debug', 'Release')]
    [string]$Configuration = 'Debug',

    # The folder the three module folders are created in (emptied of them first).
    [Parameter(Mandatory)]
    [string]$Destination
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'
$root = Split-Path -Parent $PSScriptRoot
$modules = [ordered]@{
    'Community.Zabbix'       = 'Zabbix.PowerShell'
    'Community.Zabbix.Agent' = 'Zabbix.PowerShell.Agent'
    'Community.Zabbix.Proxy' = 'Zabbix.PowerShell.Proxy'
}

New-Item -ItemType Directory -Force -Path $Destination -WhatIf:$false | Out-Null
$Destination = (Resolve-Path $Destination).Path
$core = Join-Path $Destination 'Community.Zabbix'

foreach ($name in $modules.Keys) {
    $source = Join-Path $root "src/$($modules[$name])/bin/$Configuration/net10.0"
    if (-not (Test-Path (Join-Path $source "$name.psd1"))) { throw "Stage-ZbxModules: no $name.psd1 in '$source'; build $Configuration first." }
    $target = Join-Path $Destination $name
    # The folder is deleted and rebuilt: one confirmation for it, then its own steps run unprompted.
    if (-not $PSCmdlet.ShouldProcess($target, "Stage the $Configuration build of $name (replacing the folder)")) { continue }
    if (Test-Path $target) { Remove-Item -Recurse -Force $target -Confirm:$false }
    Copy-Item -Path $source -Destination $target -Recurse -Confirm:$false
    # An assembly's XML documentation and its symbols, beside it: never loaded by pwsh. The MAML help (en-US/*.xml) and
    # the views (*.ps1xml) are not assembly documentation and stay.
    Get-ChildItem -Path $target -File -Filter *.pdb | Remove-Item -Confirm:$false
    Get-ChildItem -Path $target -File -Filter *.xml |
        Where-Object { Test-Path (Join-Path $target ([IO.Path]::ChangeExtension($_.Name, '.dll'))) } |
        Remove-Item -Confirm:$false
    if ($name -ne 'Community.Zabbix') {
        # A module's licence and notices stay in its own folder: each module is published, and carries them, alone.
        Get-ChildItem -Path $target -File -Recurse | Where-Object Name -NotIn 'LICENSE', 'THIRD-PARTY-NOTICES.md' | ForEach-Object {
            $relative = [IO.Path]::GetRelativePath($target, $_.FullName)
            $shared = Join-Path $core $relative
            if ((Test-Path $shared) -and (Get-FileHash $shared).Hash -eq (Get-FileHash $_.FullName).Hash) { Remove-Item $_.FullName -Confirm:$false }
        }
        Get-ChildItem -Path $target -Directory -Recurse | Sort-Object { $_.FullName.Length } -Descending |
            Where-Object { -not (Get-ChildItem -Path $_.FullName -Force) } | Remove-Item -Confirm:$false
    }
    $size = (Get-ChildItem -Path $target -File -Recurse | Measure-Object -Property Length -Sum).Sum
    [pscustomobject]@{ Module = $name; Path = $target; Files = @(Get-ChildItem -Path $target -File -Recurse).Count; MegaBytes = [math]::Round($size / 1MB, 1) }
}
