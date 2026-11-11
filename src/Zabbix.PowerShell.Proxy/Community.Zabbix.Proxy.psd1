# This Source Code Form is subject to the terms of the Mozilla Public
# License, v. 2.0. Copyright (c) 2026 schroeder247. SPDX-License-Identifier: MPL-2.0
#
# The module manifest of Community.Zabbix.Proxy (docs/adr/0003-powershell-module-split.md). It requires the module Community.Zabbix, whose
# connection, context and sessions its commands use; Import-Module Community.Zabbix.Proxy imports Community.Zabbix first.
#
# In the source tree this manifest is a template whose CmdletsToExport / AliasesToExport are empty; the ZabbixGenerateManifest target
# (src/Zabbix.PowerShell.Module.targets) fills both arrays in the build's own copy (obj/<config>/<tfm>/module/, copied
# into the module folder) from the [Cmdlet]/[Alias] attributes in Commands/*.cs; never a wildcard, so command discovery
# does not load the assembly. Nothing generated is checked in (docs/adr/0004-conventions.md).
@{
    RootModule           = 'Community.Zabbix.PowerShell.Proxy.dll'
    ModuleVersion        = '1.0.0'
    GUID                 = 'c4a92d17-6e3b-4f80-b5d1-7e9a0c2f4b68'
    Author               = 'schroeder247'
    CompanyName          = 'Community Zabbix contributors'
    Copyright            = 'Copyright (c) 2026 schroeder247 and Community Zabbix contributors'
    Description          = 'Zabbix proxies from PowerShell: active and passive proxy sessions, the proxy protocol builders and the connector stream, over the Community Zabbix SDK. Provided "as is", without warranty of any kind: use at your own risk. This project is not affiliated with, endorsed by or sponsored by Zabbix LLC. Zabbix is a registered trademark of Zabbix LLC.'
    # net10.0: pwsh 7.6 is the first PowerShell on .NET 10.
    PowerShellVersion    = '7.6'
    CompatiblePSEditions = @('Core')
    # By name here; a release build pins it to the family's version (RequiredVersion, src/Zabbix.PowerShell.Module.targets),
    # because this module compiles against Community.Zabbix's internals. tools/Test-ZbxModuleInstall.ps1 proves the
    # three install together from a feed.
    RequiredModules      = @('Community.Zabbix')
    CmdletsToExport      = @()
    AliasesToExport      = @()
    FunctionsToExport    = @()
    VariablesToExport    = @()
    # The views of this module's own output types (hand-written).
    FormatsToProcess     = @('Community.Zabbix.Proxy.format.ps1xml')
    PrivateData          = @{
        PSData = @{
            Prerelease   = 'alpha1'
            Tags         = @('Zabbix', 'Monitoring', 'Proxy', 'PSEdition_Core', 'Linux', 'Windows', 'MacOS')
            LicenseUri   = 'https://mozilla.org/MPL/2.0/'
            ProjectUri   = 'https://github.com/schroeder247/community-zabbix'
            IconUri      = 'https://raw.githubusercontent.com/schroeder247/community-zabbix/main/assets/icon.png'
            ReleaseNotes = 'https://github.com/schroeder247/community-zabbix/blob/main/CHANGELOG.md'
        }
    }
}
