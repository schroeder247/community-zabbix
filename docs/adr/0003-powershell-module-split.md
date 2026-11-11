<!--
This Source Code Form is subject to the terms of the Mozilla Public
License, v. 2.0. Copyright (c) 2026 schroeder247. SPDX-License-Identifier: MPL-2.0
-->
# 0003. The PowerShell module is three modules: Zabbix, Zabbix.Agent, Zabbix.Proxy

- **Status:** Accepted; decision 6's last sentence ("the generated files stay checked in") superseded by
  [0004](0004-conventions.md), under which nothing a build writes is committed; the `Zabbix` module's command count and references amended by [0006](0006-protocol-split.md);
  the siblings' `RequiredModules` amended before the first release to name `Zabbix` without a version (a version
  range excluded the prerelease); the compatibility aliases dropped by the amendment at the end of this record; the
  module names amended 2026-10-12 (`Community.Zabbix`, `Community.Zabbix.Agent`, `Community.Zabbix.Proxy`; below);
  decision 5's "no `IconUri`" clarified 2026-10-30 (below); the prerelease dropped for 1.0.0 by the 2026-10-30 amendment (below)
- **Date:** 2026-10-05

## Context

Until 2026-10-05 the PowerShell surface was one module, `Zabbix` (`src/Zabbix.PowerShell`, 313 cmdlets). Its folder
carried everything any of its cmdlets could need: the API and the wire (`Zabbix.Sdk`), the agent hosts
(`Zabbix.Agent`, with `Microsoft.Extensions.Hosting`, health checks and service discovery), the listeners
(`Zabbix.Proxy` and four ASP.NET Core assemblies from the runtime pack), the durable store (EF Core, SQLite with its
native libraries for 23 runtime identifiers, Npgsql) and the OpenTelemetry SDK — 82 MB that every user installed and
whose assemblies every session could conflict on, although most sessions only call the API.
The agent, proxy and connector-stream cmdlets were hidden behind three PowerShell experimental features
(`Zabbix.Agent`, `Zabbix.Proxy`, `Zabbix.Streaming`), which a user enables per machine and which the tests, the
parity harness and the live runs had to switch on with a `-SettingsFile`.

The SDK had already drawn the same line between its packages (`adr/0002-package-ids.md`): `Zabbix.Sdk` for the API and
the wire, `Zabbix.Agent` for the hosts, `Zabbix.Proxy` for the listeners, `Zabbix.Storage.EntityFrameworkCore` for the
store. Az and Microsoft.Graph ship one module per area the same way, beside one module they all require.

## Decision

1. Three modules, each a project whose build output is its module folder, each assembly named after its project:

   | Module (manifest) | Project / assembly | Holds | References |
   | --- | --- | --- | --- |
   | `Zabbix` (`Zabbix.psd1`) | `src/Zabbix.PowerShell` / `Zabbix.PowerShell.dll` | 272 cmdlets at the split, 259 after [ADR 0006](0006-protocol-split.md) removed thirteen: the generated CRUD, connections, the sender and getter, history, watches, configuration documents; and the infrastructure the others build on (`ZabbixCmdlet`, `ZabbixServerCmdlet`, the context and its sessions, `ZabbixScriptRunspace`, the telemetry, `ZabbixReference<T>`, the errors, the binder and converter) | `Zabbix.Sdk` only (with `Zabbix.Api`, `Zabbix.Abstractions`), the caching package, OpenTelemetry; since ADR 0006 also `Zabbix.Wire` (the framed wire, with BouncyCastle) |
   | `Zabbix.Agent` (`Zabbix.Agent.psd1`) | `src/Zabbix.PowerShell.Agent` / `Zabbix.PowerShell.Agent.dll` | 18 cmdlets at the split (20 with `Clear-Zbx*AgentLog`): the active and passive agent sessions, their handlers, the agent-host provisioning | `Zabbix.Agent`; `Zabbix.Proxy` for the passive agent's listener, with the socket transport |
   | `Zabbix.Proxy` (`Zabbix.Proxy.psd1`) | `src/Zabbix.PowerShell.Proxy` / `Zabbix.PowerShell.Proxy.dll` | 23 cmdlets at the split (25 with `Clear-Zbx*ProxyLog`): the active and passive proxy sessions, the proxy data stores, the proxy protocol's builders, the connector stream reader | `Zabbix.Proxy`, `Zabbix.Storage.EntityFrameworkCore` (`Zabbix.Agent.Storage.EntityFrameworkCore` since ADR 0006), SQLite, Npgsql, the socket transport |

2. **The RequiredModules graph.** `Zabbix.Agent` and `Zabbix.Proxy` each declare
   `RequiredModules = @('Zabbix')`, by name only (a version range would become the package dependency `[1.0.0, )`,
   which the prerelease `Zabbix` does not satisfy; the release publishes the three at one version);
   `Import-Module Zabbix.Proxy` imports
   `Zabbix` first. Neither sibling requires the other: the proxy commands use no type of the agent module (the one
   shared helper, the static options monitor, moved to `Zabbix`). `Zabbix` requires nothing.

   ```
   Zabbix.Agent ──requires──▶ Zabbix ◀──requires── Zabbix.Proxy
   ```

3. **Shared infrastructure stays internal.** The sibling assemblies reach `Zabbix.PowerShell`'s internals through
   `InternalsVisibleTo`, as the module already reaches the SDK's; the SDK libraries name the two new assemblies too.
   The public surface of each module assembly is its cmdlets, their abstract bases, the types they take and write,
   the module initializers, the exceptions a script catches, and the types a `.ps1xml` names
   (`ZabbixRecordConverter`, `ZabbixAdditionalFieldsAdapter`). Everything else — the binder, the property overrides,
   the entity shapes, completion, the sessions registry, the telemetry and log classes, the transformation and
   completer attributes — is internal.

4. **A sibling's folder carries only what `Zabbix`'s does not.** A project reference copies the referenced project's
   output, so a sibling's build output also holds `Zabbix.PowerShell.dll`, `Zabbix.Sdk.dll` and the rest;
   `tools/Stage-ZbxModules.ps1` (which the release runs) leaves out of a sibling every file the `Zabbix` folder holds
   with the same bytes. The runtime looks for a referenced assembly in the requesting assembly's own folder only, so
   `Zabbix` registers one `AssemblyLoadContext.Resolving` handler at import (`ZabbixModuleDependencies`) that answers a
   sibling's reference to a shared assembly from the `Zabbix` folder. Measured 2026-10-05 in pwsh 7.6.6: without it
   `Start-ZbxPassiveAgent` failed on `Microsoft.Extensions.Hosting.Abstractions` from the staged layout.
   Staged sizes (Debug, with symbols): `Zabbix` 38.5 MB, `Zabbix.Agent` 2.4 MB, `Zabbix.Proxy` 42.3 MB.

5. **Prerelease modules replace the experimental features.** The `[Experimental("Zabbix.Agent|Zabbix.Proxy|Zabbix.Streaming")]`
   attributes, the manifest's `ExperimentalFeatures` and every `-SettingsFile` that enabled them are gone: every
   command of an imported module is visible. The three manifests carry `ModuleVersion = '1.0.0'` and
   `PrivateData.PSData.Prerelease = 'preview1'` (the Gallery version `1.0.0-preview1`; `Install-Module -AllowPrerelease`),
   which is the module-level statement that the surface can still change. The SDK's own preview contract stays where
   it is: the C# `[Experimental("ZBX1001"…"ZBX1003")]` attributes on the proxy hosts, the active agent and the connector
   feed (`docs/PREVIEW.md`); the sibling projects suppress the ids they call on purpose. `ReleaseNotes` points at the
   changelog; there is no `HelpInfoURI` (no updatable-help host exists) and no `IconUri` (no icon is published).

6. **One build for the three.** `src/Zabbix.PowerShell.Module.targets`, imported by each project after it sets
   `ZabbixModuleName`, generates the manifest's `CmdletsToExport`/`AliasesToExport`, the MAML help
   (`en-US/<assembly>.dll-Help.xml`) and the reference pages (`reference/`) from that project's sources; the `Zabbix`
   project also sets `ZabbixModuleGeneratedCmdlets` (the generator's CRUD cmdlets are its sources too) and
   `ZabbixModuleTypesFromSchema` (`Zabbix.types.ps1xml`, `Zabbix.format.ps1xml`). The listener transport the two
   siblings carry is `src/Zabbix.PowerShell.AspNetCoreTransport.targets`. Each module has its own hand-written views
   (`Zabbix.PowerShell.format.ps1xml`, `Zabbix.Agent.format.ps1xml`, `Zabbix.Proxy.format.ps1xml`). The generated
   files stay checked in.

## Consequences

- `Install-Module Zabbix -AllowPrerelease` installs the API module alone: no host, listener, EF Core, SQLite or Npgsql
  assembly (`ModuleShapeTests.The_module_Zabbix_carries_no_host_listener_or_database_assembly`). A user of the agent or
  proxy commands installs that module too; `Import-Module Zabbix.Agent` is enough to use them.
- **The OpenTelemetry SDK stays in `Zabbix`.** The module is the application inside pwsh and exports its own telemetry
  from the first import when `OTEL_EXPORTER_OTLP_ENDPOINT` is set (the design standard asks for telemetry from the
  first release). It is the
  largest conflict surface left in the `Zabbix` folder (another module that loads an older OpenTelemetry first wins
  the load), mitigated by never loading it unless the endpoint is set (`ProcessTests.The_module_imports_in_a_fresh_pwsh_process`
  asserts no OpenTelemetry assembly is loaded otherwise) and by one process-wide set of exporters, created on the first
  exporting connection and disposed with the last (`ProcessTests.Two_connections_export_each_span_once`).
  Moving it into a fourth,
  optional module is the next step if a conflict is reported.
- `Remove-Module Zabbix` refuses while a sibling is imported (PowerShell's own rule for a required module); remove the
  siblings with it (`Remove-Module Zabbix.Agent, Zabbix.Proxy, Zabbix`). Removing a sibling stops the sessions it
  started (`ZabbixAgentModule`, `ZabbixProxyModule`); removing `Zabbix` closes the whole context.
- A handler scriptblock registered on an agent or proxy session runs in a runspace with `Zabbix` and that session's
  module imported.
- The tests import all three modules into one runspace (the test project's output holds them side by side); the
  process tests import them by name from the staged layout, as an installed module is. The help, manifest and
  module-shape tests run per module. The 26.x parity suites drive the three modules as the one module they were
  written for. (The parity harness was archived with the 26.x line the same day: [ADR 0001](0001-repository-layout.md),
  decision 3.)
- The release stages, zips and publishes three module folders, `Zabbix` first (a sibling's `RequiredModules` must
  resolve on the Gallery).

## Amendment (2026-10-06): before 1.0

- **No compatibility aliases.** The parameter aliases that kept the spellings of the never-released 26.x module, and
  the mechanical spellings the generated names replaced (`schema/words.json`), are dropped: 714 parameter aliases across
  the three modules become 362, and the command alias `Initialize-ZbxActiveProxy` is gone. An alias that binds what a
  name cannot stays ([powershell-binding.md](../design/powershell-binding.md), "Parameter names and aliases").
- **`Zabbix.Proxy` carries no store.** `Start-ZbxActiveProxy -Store`, `New-ZbxProxyDataStore` and
  `Get-ZbxProxyDataStore` are removed: the session never read the store. The module no longer references EF Core, SQLite
  or Npgsql, and has 23 cmdlets. `Zabbix` has 267 (the generated `ceprule`, `device` and `hostdashboard` commands).
- **One `-Server` shape.** Every server address is `ZabbixServerNode[]`, one HA cluster tried in order; there is no
  `-Port` beside it. `Start-ZbxActiveAgent -Server` is a `ZabbixServerActive`.
- **Smaller folders.** A module folder no longer carries PowerShell's own native files, and a staged folder no longer
  carries the assemblies' XML documentation or PDBs. Staged sizes (Debug): `Zabbix` 25.3 MB, `Zabbix.Agent` 1.5 MB,
  `Zabbix.Proxy` 1.5 MB.

## Amendment (2026-10-12): the module names carry `Community.`

The three modules are named `Community.Zabbix`, `Community.Zabbix.Agent` and `Community.Zabbix.Proxy`; the names above
are the earlier ones. Zabbix's trademark policy (<https://www.zabbix.com/trademark>) needs a licence for a name that
pairs "Zabbix" with "agent" or "proxy" or suggests an official link; `Community` means community software for Zabbix,
as in Ansible's `community.zabbix` collection. The siblings declare `RequiredModules = @('Community.Zabbix')`, the
manifests, the format and types files, the staged folders, the release zips and the `about_Community.Zabbix*` topics
follow the module name, and the cmdlet reference pages move to `reference/Community.Zabbix*/`. The project folders and
the assemblies (`Zabbix.PowerShell*.dll`, and so the MAML help files `<assembly>.dll-Help.xml`) keep their names.

The cmdlet noun prefix stays `Zbx` (`Get-ZbxHost`): a short prefix is the PowerShell community's convention
(`Get-AzVM`, `Get-MgUser`), the policy names identifiers such as cmdlet nouns as nominative fair use, and the module
name carries the full word.

## Clarification (2026-10-30): the module icon

Decision 5 says the manifests carry no `IconUri` because no icon was published. The project's own icon has since been
published (`assets/icon.png`; [ADR 0004](0004-conventions.md), amendment), and each of the three manifests sets
`PrivateData.PSData.IconUri` to it (`https://raw.githubusercontent.com/schroeder247/community-zabbix/main/assets/icon.png`).
There is still no `HelpInfoURI`.

## Amendment (2026-10-30): the first release is 1.0.0

The first release is 1.0.0, not a prerelease. The manifests carry no `Prerelease`, the modules install without
`-Prerelease`, and the agent, proxy and connector commands are ordinary commands of their modules. The SDK's
`[Experimental]` ids `ZBX1001` to `ZBX1003` are removed with them, so decision 5's "the SDK's own preview contract
stays" no longer holds. Decision 5's main point stands: nothing is hidden behind a PowerShell experimental feature.

Decision 2's dependency is now pinned in a release. The manifest templates still name `Community.Zabbix`, and a
release build (no `VersionSuffix`) writes `@{ ModuleName = 'Community.Zabbix'; RequiredVersion = '<version>' }` into
each sibling's built manifest, so the Gallery dependency is the exact version and a sibling never loads beside another
release of the module whose internals it compiles against. A prerelease build keeps the name, for the reason decision
2 gives.
