# This Source Code Form is subject to the terms of the Mozilla Public
# License, v. 2.0. Copyright (c) 2026 schroeder247. SPDX-License-Identifier: MPL-2.0
<#
.SYNOPSIS
    Checks that the packages, the three module manifests and (with -Tag) a release tag name one version, and returns it.

.DESCRIPTION
    The version has one source, Directory.Build.props: VersionPrefix and VersionSuffix, read through MSBuild's own
    evaluation (dotnet msbuild -getProperty), so the packages' version is what this reports. The rules:

    - The package version is VersionPrefix, followed by -VersionSuffix when a suffix is set (1.1.0-beta.1).
    - Every module manifest's ModuleVersion is VersionPrefix.
    - Every module manifest's PSData.Prerelease is VersionSuffix without its dots (beta.1 is beta1), because a
      PowerShell prerelease label is letters and digits only; with no suffix, no manifest carries a Prerelease.
    - A tag is v followed by the package version. A release tag is refused while a VersionSuffix is set (it would publish
      prerelease packages under a release's version); v1.1.0-beta.1 is a prerelease.
    - With a tag, CHANGELOG.md has the version's heading dated (## [1.0.0] - YYYY-MM-DD): every package's
      release notes and the GitHub release link to the changelog at the tag, which must not still say Unreleased.

    Without -Tag only the packages and the manifests are compared (the CI build job runs it that way). The output is
    the version, whether it is a prerelease, and the modules' prerelease label; the release workflow publishes with
    them and marks the GitHub release a prerelease when it is one.

.EXAMPLE
    pwsh -NoProfile -File tools/Test-ZbxReleaseVersion.ps1 -Tag v1.0.0
#>
[CmdletBinding()]
param(
    # The release tag (v<version>), with or without the leading v.
    [string]$Tag
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'
$root = Split-Path -Parent $PSScriptRoot

$evaluated = & dotnet msbuild (Join-Path $root 'src/Zabbix.Sdk/Zabbix.Sdk.csproj') -getProperty:VersionPrefix -getProperty:VersionSuffix -getProperty:PackageVersion
if ($LASTEXITCODE -ne 0) { throw "Test-ZbxReleaseVersion: MSBuild could not evaluate the version: $evaluated" }
$properties = ($evaluated | Out-String | ConvertFrom-Json).Properties
$prefix = [string]$properties.VersionPrefix
$suffix = [string]$properties.VersionSuffix
$version = [string]$properties.PackageVersion
$expected = if ($suffix) { "$prefix-$suffix" } else { $prefix }
if ($version -ne $expected) { throw "Test-ZbxReleaseVersion: the package version is $version, not VersionPrefix-VersionSuffix ($expected)." }

$label = $suffix -replace '\.', ''
if ($label -and $label -notmatch '^[A-Za-z0-9]+$') {
    throw "Test-ZbxReleaseVersion: VersionSuffix '$suffix' without its dots ('$label') is not a PowerShell prerelease label (letters and digits only)."
}

$failures = [Collections.Generic.List[string]]::new()
foreach ($manifest in 'src/Zabbix.PowerShell/Community.Zabbix.psd1', 'src/Zabbix.PowerShell.Agent/Community.Zabbix.Agent.psd1', 'src/Zabbix.PowerShell.Proxy/Community.Zabbix.Proxy.psd1') {
    $data = Import-PowerShellDataFile -Path (Join-Path $root $manifest)
    if ($data.ModuleVersion -ne $prefix) { $failures.Add("$manifest has ModuleVersion '$($data.ModuleVersion)'; VersionPrefix is '$prefix'.") }
    $prerelease = $data.PrivateData.PSData['Prerelease']
    if ([string]$prerelease -cne $label) {
        $failures.Add("$manifest has Prerelease '$prerelease'; VersionSuffix '$suffix' needs '$label' (the suffix without its dots, empty when there is no suffix).")
    }
}

if ($Tag) {
    $tagged = $Tag -replace '^v', ''
    if ($tagged -ne $version) {
        $failures.Add($(if ($suffix -and $tagged -eq $prefix) {
            "the tag $Tag names a final release but VersionSuffix is '$suffix'; remove VersionSuffix (and the manifests' Prerelease) for the release commit, or tag v$version."
        } else {
            "the tag $Tag does not name the version $version (Directory.Build.props: VersionPrefix '$prefix', VersionSuffix '$suffix')."
        }))
    }
    $heading = "## [$version] - "
    $dated = Get-Content -Path (Join-Path $root 'CHANGELOG.md') | Where-Object { $_.StartsWith($heading, [StringComparison]::Ordinal) -and $_.Substring($heading.Length) -match '^\d{4}-\d{2}-\d{2}$' }
    if (-not $dated) {
        $failures.Add("CHANGELOG.md has no dated heading '$heading<YYYY-MM-DD>' for $version; date it in the release commit (CONTRIBUTING.md, 'Releasing').")
    }
}

if ($failures.Count) { throw "Test-ZbxReleaseVersion: $($failures -join ' ')" }
[pscustomobject]@{ Version = $version; Prerelease = [bool]$suffix; ModulePrerelease = $label }
