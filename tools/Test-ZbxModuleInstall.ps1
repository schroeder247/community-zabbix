# This Source Code Form is subject to the terms of the Mozilla Public
# License, v. 2.0. Copyright (c) 2026 schroeder247. SPDX-License-Identifier: MPL-2.0
<#
.SYNOPSIS
    Publishes the three staged modules to a temporary local PSResource repository and installs Community.Zabbix.Agent and
    Community.Zabbix.Proxy from it, the way a user installs them from the PowerShell Gallery (with -Prerelease, so a
    prerelease version installs too).

.DESCRIPTION
    The gate for the modules' dependency graph (docs/adr/0003-powershell-module-split.md): Community.Zabbix.Agent and
    Community.Zabbix.Proxy require Community.Zabbix, and Publish-PSResource turns RequiredModules into the package's dependency. A version
    range there that the published Community.Zabbix does not satisfy makes Install-PSResource Community.Zabbix.Agent fail
    although every module is on the feed; this script finds that before a release does.

    The steps are the release workflow's: publish Community.Zabbix first, then the siblings (with the staged folder on
    PSModulePath, as Publish-PSResource validates a manifest's RequiredModules locally). Then Community.Zabbix.Agent and
    Community.Zabbix.Proxy are installed, which must pull Community.Zabbix in as their dependency, and each is imported by
    name from the installed location.

    Everything runs in a child pwsh whose HOME (and the XDG directories under it) is a temporary folder, so the repository
    registration and the installed modules never touch the caller's own PowerShell configuration; the folder is
    deleted at the end. It runs on Linux and macOS (the CI runner), where those variables are the user scope. On macOS
    PSResourceGet keeps its repository list under ~/Library/Application Support whatever HOME says, so the repository
    is registered under a name of its own and unregistered when the check ends, pass or fail.

.EXAMPLE
    pwsh -NoProfile -File tools/Stage-ZbxModules.ps1 -Destination stage
    pwsh -NoProfile -File tools/Test-ZbxModuleInstall.ps1 -Stage stage
#>
[CmdletBinding()]
param(
    # The folder tools/Stage-ZbxModules.ps1 staged the three module folders in.
    [Parameter(Mandatory)]
    [string]$Stage
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'
if ($IsWindows) { throw 'Test-ZbxModuleInstall: the sandboxed user scope is built from HOME and XDG_*; run it on Linux or macOS.' }

$Stage = (Resolve-Path $Stage).Path
foreach ($name in 'Community.Zabbix', 'Community.Zabbix.Agent', 'Community.Zabbix.Proxy') {
    if (-not (Test-Path (Join-Path $Stage "$name/$name.psd1"))) { throw "Test-ZbxModuleInstall: no $name/$name.psd1 under '$Stage'; run tools/Stage-ZbxModules.ps1 first." }
}

$sandbox = Join-Path ([IO.Path]::GetTempPath()) "zbx-install-$([guid]::NewGuid().ToString('n'))"
New-Item -ItemType Directory -Path (Join-Path $sandbox 'feed') | Out-Null
$repository = "ZbxLocal-$([guid]::NewGuid().ToString('n'))"
$check = {
    param($Stage, $Feed, $Repository)
    Set-StrictMode -Version Latest
    $ErrorActionPreference = 'Stop'
    Register-PSResourceRepository -Name $Repository -Uri $Feed -Trusted
    try {
        $env:PSModulePath = $Stage + [IO.Path]::PathSeparator + $env:PSModulePath
        # As the release publishes: Community.Zabbix, then the siblings without the repository-side dependency lookup (the Gallery
        # may not list a package it accepted seconds ago). The dependency is still written into their packages.
        Publish-PSResource -Path (Join-Path $Stage 'Community.Zabbix') -Repository $Repository
        foreach ($module in 'Community.Zabbix.Agent', 'Community.Zabbix.Proxy') {
            Publish-PSResource -Path (Join-Path $Stage $module) -Repository $Repository -SkipDependenciesCheck
        }
        # Only the user scope from here on: the staged folders must not satisfy the dependency.
        $env:PSModulePath = ($env:PSModulePath -split [IO.Path]::PathSeparator | Where-Object { $_ -ne $Stage }) -join [IO.Path]::PathSeparator
        Install-PSResource -Name Community.Zabbix.Agent, Community.Zabbix.Proxy -Repository $Repository -Prerelease -Scope CurrentUser -TrustRepository -Quiet
        $installed = @(Get-InstalledPSResource -Scope CurrentUser | Sort-Object Name)
        foreach ($module in 'Community.Zabbix.Agent', 'Community.Zabbix.Proxy') {
            Import-Module $module
            $loaded = Get-Module $module
            if ($loaded.ModuleBase.StartsWith($Stage)) { throw "$module was imported from the staged folder, not the installed one." }
        }
        if (-not (Get-Module Community.Zabbix)) { throw 'Importing Community.Zabbix.Agent and Community.Zabbix.Proxy did not import Community.Zabbix.' }
        $installed | ForEach-Object { [pscustomobject]@{ Name = $_.Name; Version = "$($_.Version)$(if ($_.Prerelease) { "-$($_.Prerelease)" })" } }
    }
    finally { Unregister-PSResourceRepository -Name $Repository }
}

$saved = @{}
$variables = @{
    HOME            = $sandbox
    # Their defaults under HOME: PSResourceGet installs to HOME/.local/share/powershell/Modules whatever XDG_DATA_HOME
    # says, and pwsh's user module path follows XDG_DATA_HOME, so the two must agree.
    XDG_CONFIG_HOME = Join-Path $sandbox '.config'
    XDG_DATA_HOME   = Join-Path $sandbox '.local/share'
    XDG_CACHE_HOME  = Join-Path $sandbox '.cache'
}
# .NET resolves a special folder only when it exists (an empty path otherwise, which PSResourceGet would read as a
# folder relative to the working directory), so the sandbox's are created first and the child starts inside it.
$variables.Values | Where-Object { $_ -ne $sandbox } | ForEach-Object { New-Item -ItemType Directory -Path $_ | Out-Null }
Push-Location $sandbox
try {
    foreach ($name in $variables.Keys) {
        $saved[$name] = [Environment]::GetEnvironmentVariable($name)
        [Environment]::SetEnvironmentVariable($name, $variables[$name])
    }
    # The child's PSModulePath is its own default (the sandboxed user scope and the installation's modules), not the
    # caller's, which may hold a build's module folders.
    $saved['PSModulePath'] = $env:PSModulePath
    $env:PSModulePath = $null
    $installed = & pwsh -NoProfile -NonInteractive -Command $check -args $Stage, (Join-Path $sandbox 'feed'), $repository
    if ($LASTEXITCODE -ne 0) { throw "Test-ZbxModuleInstall: publishing to or installing from the local repository failed (exit code $LASTEXITCODE)." }
    $installed
    $names = @($installed | ForEach-Object Name)
    foreach ($name in 'Community.Zabbix', 'Community.Zabbix.Agent', 'Community.Zabbix.Proxy') {
        if ($names -notcontains $name) { throw "Test-ZbxModuleInstall: $name was not installed." }
    }
}
finally {
    Pop-Location
    foreach ($name in $saved.Keys) { [Environment]::SetEnvironmentVariable($name, $saved[$name]) }
    Remove-Item -Recurse -Force $sandbox -ErrorAction SilentlyContinue
}
