<!--
This Source Code Form is subject to the terms of the Mozilla Public
License, v. 2.0. Copyright (c) 2026 schroeder247. SPDX-License-Identifier: MPL-2.0
-->
# Install

The libraries target .NET 10 (`net10.0`). To run an application that uses them, you need the .NET 10 runtime; to build one, you
need the .NET 10 SDK. The modules need PowerShell 7.6 or later on Windows, macOS, or Linux. Windows PowerShell 5.1 isn't
supported. One build of each module runs on every operating system.

There's no release yet. Community Zabbix is in alpha: nothing is on nuget.org, the PowerShell Gallery, a GitHub release or
GitHub Packages. Until 1.0.0, build it [from source](#from-source). The other sections describe how you'll install a release,
and they all carry the same files.

## From nuget.org and the PowerShell Gallery

Add the packages you use. Each one brings the packages it depends on:

```bash
dotnet add package Community.Zabbix.Sdk                                 # the API client and the tree
dotnet add package Community.Zabbix.Wire                                # ZabbixWireClient
dotnet add package Community.Zabbix.Agent                               # agents, the send loop and the stores
dotnet add package Community.Zabbix.Proxy                               # trapper, passive agent, proxies, connector
dotnet add package Community.Zabbix.Agent.Storage.EntityFrameworkCore   # a durable store on EF Core
dotnet add package Community.Zabbix.Agent.Plugins                       # item providers loaded from a folder
```

Install the modules for the current user:

```powershell
Install-PSResource Community.Zabbix
Install-PSResource Community.Zabbix.Agent      # agent sessions; installs Community.Zabbix
Install-PSResource Community.Zabbix.Proxy      # proxy sessions and the connector stream; installs Community.Zabbix
```

`Update-PSResource Community.Zabbix*` updates all three. `Install-Module` (PowerShellGet 2) works too.

## From a GitHub release

Every release on the [Releases page](https://github.com/schroeder247/community-zabbix/releases) carries the same files
the feeds receive:

| File | What it is |
| --- | --- |
| `Community.Zabbix-<version>.zip`, `Community.Zabbix.Agent-<version>.zip`, `Community.Zabbix.Proxy-<version>.zip` | The three modules, each as its module folder |
| `Community.Zabbix.<package>.<version>.nupkg` | The eight NuGet packages |
| `Community.Zabbix.<package>.<version>.snupkg` | Their symbol packages, for debugging into the SDK |
| `Community.Zabbix.<package>.<version>.spdx.json` | Each package's software bill of materials (SPDX 2.2) |

**The modules.** Download the zips and extract them into your user modules folder, the first folder on
`$env:PSModulePath`. On Windows, unblock each zip first: Windows marks a downloaded file as coming from the internet,
and PowerShell's execution policy can then refuse to load the module.

```powershell
$version = '1.0.0'
$modules = ($env:PSModulePath -split [IO.Path]::PathSeparator)[0]   # Documents\PowerShell\Modules on Windows,
                                                                     # ~/.local/share/powershell/Modules elsewhere
foreach ($module in 'Community.Zabbix', 'Community.Zabbix.Agent', 'Community.Zabbix.Proxy') {
    $zip = "$module-$version.zip"
    Invoke-WebRequest "https://github.com/schroeder247/community-zabbix/releases/download/v$version/$zip" -OutFile $zip
    if ($IsWindows) { Unblock-File $zip }
    Expand-Archive $zip -DestinationPath $modules -Force
}
Import-Module Community.Zabbix.Agent    # imports Community.Zabbix first
```

Take only the zips you need, but always `Community.Zabbix`: the agent and proxy modules hold only what it does not,
and each requires the `Community.Zabbix` of its own version. To update, delete the three module folders first, then
extract the new release: extracting over an older one can leave its files behind.

**The packages.** Download the `.nupkg` files into a folder and add it as a package source. Download all eight: a
package depends on its siblings at exactly its own version. Its other dependencies still come from nuget.org.

```bash
gh release download v1.0.0 --repo schroeder247/community-zabbix --pattern '*.nupkg' --dir ./zabbix-packages
dotnet nuget add source "$PWD/zabbix-packages" --name zabbix-release
dotnet add package Community.Zabbix.Sdk --version 1.0.0
```

## From GitHub Packages

Every release is also pushed to the NuGet feed of this repository's owner on GitHub Packages. GitHub Packages asks for a token even for public
packages: create a personal access token (classic) with the `read:packages` scope, then add the feed once:

```bash
dotnet nuget add source https://nuget.pkg.github.com/schroeder247/index.json --name github-schroeder247 \
    --username <your GitHub user> --password <the token> --store-password-in-clear-text
dotnet add package Community.Zabbix.Sdk
```

On Windows the password is stored encrypted and `--store-password-in-clear-text` can be left out. Never commit the
`nuget.config` that holds the token.

## Check what you downloaded

Every file on a release has a build-provenance attestation: a signed statement of the workflow run, commit and tag
that produced it. The GitHub CLI (signed in) checks one:

```bash
gh attestation verify Community.Zabbix-1.0.0.zip --repo schroeder247/community-zabbix
```

A file that was altered, or built anywhere but this repository's release workflow, fails the check. The `.spdx.json`
beside each package lists every file and dependency the package holds.

## From source

Clone the repository and build with the .NET 10 SDK; `global.json` pins the exact SDK version:

```bash
git clone https://github.com/schroeder247/community-zabbix.git && cd community-zabbix
dotnet build Zabbix.slnx -c Release
dotnet pack Zabbix.slnx -c Release -o ~/zabbix-packages                                  # the NuGet packages
pwsh -File tools/Stage-ZbxModules.ps1 -Configuration Release -Destination ~/zabbix-modules # the modules as installed folders
```

Each library builds into `src/<project>/bin/Release/net10.0/`. For the three module projects that folder is the
module itself, so `Import-Module src/Zabbix.PowerShell/bin/Release/net10.0/Community.Zabbix.psd1` loads it in place
(import it before the agent or proxy module when you load those by path).
Point `$env:PSModulePath` at the staged folder, or copy its three folders into your modules folder, to use them by name.
[CONTRIBUTING.md](../CONTRIBUTING.md) covers the tests and the other checks.

## Use your own build

To use packages you built yourself in a project, add the folder as a package source. Run these from the folder that holds your project,
`Hello`:

```bash
git clone https://github.com/schroeder247/community-zabbix
dotnet pack community-zabbix/Zabbix.slnx -c Release -o zabbix-packages
dotnet new nugetconfig -o Hello                         # nuget.org, for the packages' dependencies
dotnet nuget add source "$PWD/zabbix-packages" --name zabbix-local --configfile Hello/nuget.config
export NUGET_PACKAGES="$PWD/zabbix-packages-cache"      # a private package cache; keep it set for later builds
dotnet add Hello package Community.Zabbix.Sdk --version 1.0.0
dotnet add Hello package Community.Zabbix.Wire --version 1.0.0
```

NuGet caches a package by its id and version in `~/.nuget/packages`, and never reads that version from a source again. So without the
private cache, a version restored earlier wins over the build you just packed. To empty the shared cache instead, run
`dotnet nuget locals global-packages --clear`.
