# This Source Code Form is subject to the terms of the Mozilla Public
# License, v. 2.0. Copyright (c) 2026 schroeder247. SPDX-License-Identifier: MPL-2.0
#
# The module manifest. CmdletsToExport lists exactly the [Cmdlet] types the assembly compiles and AliasesToExport exactly
# their [Alias]es (ModuleShapeTests.The_manifest_exports_exactly_the_compiled_cmdlets); never a wildcard, so command
# discovery does not load the assembly to find out.
#
# In the source tree this manifest is a template whose CmdletsToExport / AliasesToExport are empty; the ZabbixGenerateManifest target
# (src/Zabbix.PowerShell.Module.targets) fills both arrays in the build's own copy (obj/<config>/<tfm>/module/, copied
# into the module folder) from every [Cmdlet]/[Alias] attribute in Commands/*.cs and the generator's own CRUD cmdlets
# (obj/generated). Nothing generated is checked in (docs/adr/0004-conventions.md).
#
# The agent and proxy commands are the sibling modules Community.Zabbix.Agent and Community.Zabbix.Proxy, which require this one
# (docs/adr/0003-powershell-module-split.md).
@{
    RootModule           = 'Community.Zabbix.PowerShell.dll'
    # Community.Zabbix.Api.dll carries every request/parameters/nested/read record (Zabbix.Api.csproj's own ZabbixEmit="Models")
    # that Community.Zabbix.types.ps1xml's <TypeAdapter> entries (ZabbixAdditionalFieldsAdapter, TypesToProcess below) name by
    # type. RequiredAssemblies loads BEFORE TypesToProcess/FormatsToProcess (about_Module_Manifest); without it the
    # CLR has not yet loaded Community.Zabbix.Api.dll (a referenced assembly loads lazily, on first real use of one of its
    # types — nothing in Community.Zabbix.PowerShell.dll touches one before then), and Update-TypeData -TypeAdapter — unlike
    # -TypeConverter — resolves its -TypeName against already-loaded assemblies EAGERLY, at type-data-file processing
    # time: without this, every ZabbixAdditionalFieldsAdapter registration fails ("The adapted type ... is not valid.")
    # on a clean Import-Module.
    RequiredAssemblies   = @('Community.Zabbix.Api.dll')
    ModuleVersion        = '1.0.0'
    GUID                 = '5f0d8a4e-6a0b-4d52-9b1e-3c7a2f4d9e61'
    Author               = 'schroeder247'
    CompanyName          = 'Community Zabbix contributors'
    Copyright            = 'Copyright (c) 2026 schroeder247 and Community Zabbix contributors'
    Description          = 'Zabbix from PowerShell: the API, the sender and getter, history, watches and configuration documents, over the Community Zabbix SDK. Provided "as is", without warranty of any kind: use at your own risk. This project is not affiliated with, endorsed by or sponsored by Zabbix LLC. Zabbix is a registered trademark of Zabbix LLC.'
    # net10.0: pwsh 7.6 is the first PowerShell on .NET 10.
    PowerShellVersion    = '7.6'
    CompatiblePSEditions = @('Core')
    CmdletsToExport      = @()
    AliasesToExport      = @()
    FunctionsToExport    = @()
    VariablesToExport    = @()
    # Community.Zabbix.types.ps1xml (generated every build under obj/ by ZabbixGenerateTypesPs1Xml, copied beside this manifest):
    # ZabbixRecordConverter as every request/parameters/nested-record type's <TypeConverter> (a bare hashtable binds
    # through PowerShell's OWN parameter binder, no cmdlet calling ZabbixJsonBinder), ZabbixAdditionalFieldsAdapter as
    # every role:read record's <TypeAdapter> ($result.newfield reads an unmodelled wire field).
    TypesToProcess       = @('Community.Zabbix.types.ps1xml')
    # Community.Zabbix.format.ps1xml (generated under obj/ every build by the same target from ZabbixTypesEmitter.GenerateFormat):
    # a table, list and wide view per read record, so a Get- prints the columns a reader scans.
    # Zabbix.PowerShell.format.ps1xml (hand-written): the module's own output types (connections, sessions, watches).
    FormatsToProcess     = @('Community.Zabbix.format.ps1xml', 'Zabbix.PowerShell.format.ps1xml')
    PrivateData          = @{
        PSData = @{
            Prerelease   = 'alpha1'
            Tags         = @('Zabbix', 'Monitoring', 'PSEdition_Core', 'Linux', 'Windows', 'MacOS')
            LicenseUri   = 'https://mozilla.org/MPL/2.0/'
            ProjectUri   = 'https://github.com/schroeder247/community-zabbix'
            IconUri      = 'https://raw.githubusercontent.com/schroeder247/community-zabbix/main/assets/icon.png'
            ReleaseNotes = 'https://github.com/schroeder247/community-zabbix/blob/main/CHANGELOG.md'
        }
    }
}
