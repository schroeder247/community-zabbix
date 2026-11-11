<!--
This Source Code Form is subject to the terms of the Mozilla Public
License, v. 2.0. Copyright (c) 2026 schroeder247. SPDX-License-Identifier: MPL-2.0
-->
# The PowerShell modules: the contributor contract

This page is for contributors: the contracts the generated and hand-written cmdlets follow. To use the module, start with
[the PowerShell walk-through](../../src/Zabbix.PowerShell/GETTING-STARTED.md) and `Get-Help about_Community.Zabbix`.

The PowerShell surface is three modules ([ADR 0003](../adr/0003-powershell-module-split.md)), each a project whose build
output is its module folder:

| Module | Project | Cmdlets | What |
| --- | --- | --- | --- |
| `Community.Zabbix` | `src/Zabbix.PowerShell` (`Community.Zabbix.PowerShell.dll`) | 267 | the API (generated CRUD), connections, `Send-ZbxData` and `Get-ZbxAgentItem`, the server's diagnostics, history, watches, configuration documents, frontend page links (`Show-ZbxPage`); the shared infrastructure below |
| `Community.Zabbix.Agent` | `src/Zabbix.PowerShell.Agent` (`Community.Zabbix.PowerShell.Agent.dll`) | 20 | active and passive agent sessions, item handlers, agent-host provisioning |
| `Community.Zabbix.Proxy` | `src/Zabbix.PowerShell.Proxy` (`Community.Zabbix.PowerShell.Proxy.dll`) | 23 | active and passive proxy sessions, proxy protocol builders, the connector stream |

`Import-Module src/Zabbix.PowerShell/bin/Debug/net10.0/Community.Zabbix.psd1` (pwsh 7.6+, the first PowerShell on .NET 10); the siblings declare
`RequiredModules = Community.Zabbix`, pinned to the same version in a release build, and no command is hidden behind an experimental feature. Together the three export 310 cmdlets, each documented in its module's built MAML and in one reference page (built under each project's `obj/`, published by docfx).
`tools/Stage-ZbxModules.ps1` lays the three out as installed module folders (what the release publishes).

```powershell
Connect-ZbxServer https://zabbix.example/ -ApiToken (Read-Host -AsSecureString)
Get-ZbxHost 'web*' -Limit 10                                                        # case-insensitive; * and ? are wildcards
Get-ZbxHost -Filter @{ limit = 5 }                                                  # binds to ZabbixHostGetParameters
Get-ZbxContext                                                                      # this runspace's connections
```

## The contract a generated cmdlet fills in

Every command derives `ZabbixCmdlet` (the bridge) or `ZabbixServerCmdlet` (the bridge plus `-Connection` and the
runspace's current server). A command that speaks a framed protocol takes the TLS words from a wire base, so every one
has the same parameters: `ZabbixTlsCmdlet` (`-PskIdentity`, `-Psk`, `-Certificate`, `-CertificateKey`,
`-CertificateAuthority`, `-CertificateRevocationList`, `-ServerIssuer`, `-ServerSubject`; a listener adds `-TlsAccept`),
`ZabbixWireCmdlet` (those and `-Tls`, for a command with no connection), and `ZabbixServerWireCmdlet` (the same on a
`ZabbixServerCmdlet`, falling back to `Connect-ZbxServer`'s TLS words). The CRUD bases (`ZabbixGetCmdlet`, `ZabbixNewCmdlet`, `ZabbixSetCmdlet`,
`ZabbixRemoveCmdlet`, one file each) hold the behaviour; the generator emits only what depends on the entity —
the attributes (an attribute cannot take a type parameter), the SDK calls, and the entity's own reference filters.

**Get** — `ZabbixGetCmdlet<TEntity, TParameters>` owns the parameter sets `All` / `ById` (`-Id ulong[]`, which also
binds by property name in the default set, so a piped record selects itself) / `ByName` (`-Name`: one case-insensitive
`search` with `searchWildcardsEnabled`; `*` and `?` are wildcards, brackets are literal, `[SupportsWildcards]`;
`ZabbixNamePattern`; a name with no wildcard that matches nothing is a non-terminating `ObjectNotFound`,
`ZabbixNameNotFound`, as `Get-Service` writes, while a pattern that matches nothing writes nothing — not under `-Limit`
or `-CountOutput`) / `ByFilter` (`-Filter TParameters`, the typed `*GetParameters`), and `-Limit`,
`-SortField`/`-SortOrder`, `-Search` and its switches, `-CountOutput`. A `.get` that takes a time window gets
`-From`/`-Till` (`DateTimeOffset`: a date, Unix seconds or `clock.ns` text), and one that takes history's value type gets
`-ValueType` (a piped item's `ValueType` binds), so `Get-ZbxItem | Get-ZbxHistory` reads each item's
own table (`Get-ZbxHistory`, `-Trend`, `-Event`, `-Problem`, `-Alert`, `-AuditLog`). `-CountOutput` calls the client's
`CountAsync` with the same parameters, which carry no `countOutput` of their own. The generated class (simplified: the real
one also emits the `-CountOutput` branch and the entity's reference filters):

```csharp
[Cmdlet(VerbsCommon.Get, "ZbxHostGroup", DefaultParameterSetName = ZabbixParameterSets.All)]
[OutputType(typeof(ZabbixHostGroup))]
public sealed class GetHostGroupCommand : ZabbixGetCmdlet<ZabbixHostGroup, ZabbixHostGroupGetParameters>
{
    /// <summary>The objects' names, case-insensitive; <c>*</c> and <c>?</c> are wildcards, brackets are literal.</summary>
    [Parameter(Mandatory = true, Position = 0, ParameterSetName = ZabbixParameterSets.ByName)]
    [SupportsWildcards]
    [ZabbixCompleter<ZabbixHostGroup>]
    public override string[]? Name { get; set; }

    /// <inheritdoc/>
    protected override void Query(ZabbixClient client, ZabbixHostGroupGetParameters parameters)
        => WriteAll(cancellationToken => client.HostGroups.GetAsync(parameters, cancellationToken), parameters);
}
```

`WriteAll` takes the call as a function of the stop token (never a started `Task`: the call must start after the lease is taken and with the command's pipeline current, so its log records and its error answer are this command's), whether it answers a `Task<IReadOnlyList<T>>` or an `IAsyncEnumerable<T>`, so the line above compiles
against either shape of the entity client.

**Remove** — `ZabbixRemoveCmdlet<TEntity>` gathers references from the pipeline, resolves every name in one `.get`,
confirms per object with `ShouldProcess` alone (`ConfirmImpact.High`, so it asks at the default `$ConfirmPreference`;
[docs/design/decisions.md](decisions.md)), and deletes in one call. `-Force` skips that prompt, so a script or a host that
cannot prompt can delete, but never overrides `-WhatIf` or an explicit `-Confirm` (`ShouldProcessUnlessForced`):

```csharp
[Cmdlet(VerbsCommon.Remove, "ZbxHost", SupportsShouldProcess = true, ConfirmImpact = ConfirmImpact.High)]
public sealed class RemoveHostCommand : ZabbixRemoveCmdlet<ZabbixHost>
{
    /// <summary>The hosts: ids, names or objects.</summary>
    [Parameter(Mandatory = true, Position = 0, ValueFromPipeline = true)]
    [Alias("Id")]
    [ZabbixReference<ZabbixHost>]
    [ZabbixCompleter<ZabbixHost>]
    public override ZabbixReference<ZabbixHost>[]? InputObject { get; set; }

    /// <inheritdoc/>
    protected override Task DeleteAsync(ZabbixClient client, IReadOnlyList<ulong> ids, CancellationToken cancellationToken)
        => client.Hosts.DeleteAsync(ids, cancellationToken);
}
```

**New / Set** — derive `ZabbixNewCmdlet<TEntity>` / `ZabbixSetCmdlet<TEntity, TRequest>`
(`ZabbixNewCmdlet.cs`, `ZabbixSetCmdlet.cs`); one `[Parameter]` per request-record property (the generator reads the
`*CreateRequest`/`*UpdateRequest` record: a field the API requires is **not** `Mandatory` — it is checked after
`-AdditionalProperty` merges — a reference to another object → `ZabbixReference<T>[]` with `[ZabbixReference<T>]` +
`[ZabbixCompleter<T>]`, a secret — a name ending in `password`, `passwd`, `passphrase`, `secret`, `apikey`, `psk` or `token`, so not a PSK
identity or a token URL — → `SecureString`).
The generated class supplies `CreateAsync` (New) or `BuildRequest` and `UpdateAsync` (Set), resolving each reference
field all or nothing (`ResolveFieldAsync`: a name that does not resolve stops that record with a
`ZabbixParameterException` and sends nothing, because a request's list REPLACES the object's). Both
bases confirm with `ShouldProcessUnlessForced` (`ConfirmImpact.Medium`; `-Force` skips a prompt a lowered
`$ConfirmPreference` would raise) and run the call with `TryRun`. **New** reads the created objects back in one `.get`
and writes them, so it has no `-PassThru`. **Set** is a sink: it builds each piped record's
request from that record's own bound values, updates in one call, and writes nothing unless
`-PassThru`, which reads the changed objects back in one `.get` and writes them (Microsoft Learn, "Support the PassThru
Parameter").

**Names** — a generated parameter is named after its record property, and both after the wire field's words
(`schema/words.json`, `src/Zabbix.Generator/ZabbixNames.cs`: `sortorder` → `-SortOrder`, `opmessage_grp` →
`-OperationMessageGroup`); the mechanical spelling is not kept as an alias. An alias
is kept only where it binds what a name cannot: `-Id` on every `-InputObject`; the piped object's own id property on a
filter or an owner (`-HostId` on `Get-ZbxItem -Host`, `-TemplateId` on a `New-` that creates on a host or template),
so `Get-ZbxHost | Get-ZbxItem` binds by property name; and PowerShell's own path aliases (`-PSPath`, `-LP`). A field
that would be named `-Host` (hiding `PSCmdlet.Host`) takes a name of its own — `-Name` for the technical name on host,
host prototype and template requests (the visible name is `-DisplayName`), `-LdapHost` on user directories,
`-HostReference` on `Get-` filters — and keeps `-Host` as its alias.

**Entity shape** — each entity is registered once in `ZabbixEntities` (`ZabbixEntityShape.Create<TEntity, TParameters>`:
API name, id and name fields, the generated JSON contracts). It is what turns a name into an id and what completion
fetches. The generator emits the table.

Rules every command follows (the bases enforce them; `ModuleShapeTests` checks the rest by reflection):
- all I/O goes through `TryRun`/`WriteAll` with `StopToken`; nothing calls `WriteObject` off the pipeline thread;
- a secret parameter is `SecureString` or `PSCredential`, never positional (`A_secret_is_never_a_positional_string`);
- a change confirms through `ShouldProcessUnlessForced` (`-Force` skips the prompt, never `-WhatIf` or an explicit
  `-Confirm`), once per resolved object and named by it (`host 'web-01' (10084)`: resolve with `ResolveTargetsAsync`,
  confirm with `Confirmed`, or both at once with `TryConfirm`, then one call; the action names the kind of change,
  `Remove host`, never a bare `Remove` — `SourceConformanceTests`), so `ConfirmImpact` alone decides whether it prompts by
  default; only a genuinely dangerous extra step takes the second question, `ShouldChange` (`Clear-ZbxHistory`, an
  import whose rules delete what the document does not name); nothing touches
  `$ConfirmPreference` (`ModuleShapeTests.Every_state_changing_verb_supports_ShouldProcess`,
  `Every_high_impact_cmdlet_has_Force`);
- the verb is a `Verbs*` constant, the noun starts with `Zbx`;
- the manifest lists exactly the compiled cmdlets and aliases (`The_manifest_exports_exactly_the_compiled_cmdlets`);
- no command or parameter carries PowerShell's `[Experimental]`
  (`ModuleShapeTests.No_command_or_parameter_is_an_experimental_feature`, ADR 0003);
- a sibling module (`Community.Zabbix.Agent`, `Community.Zabbix.Proxy`) uses this assembly's infrastructure through `InternalsVisibleTo`;
  what is not a cmdlet, a type a cmdlet takes or writes, or a type a `.ps1xml` names is `internal`.

**Manifest** — `CmdletsToExport`/`AliasesToExport` are never hand-edited. The three module projects share one build,
`../Zabbix.PowerShell.Module.targets` (each sets `ZabbixModuleName`; this one also `ZabbixModuleGeneratedCmdlets` and
`ZabbixModuleTypesFromSchema`). Its `ZabbixGenerateManifest` target (after `CoreCompile`) runs HelpGen's
`ZabbixWriteManifestExportsTask`, which reads every `[Cmdlet]` class and its own `[Alias]` attributes from the source's
Roslyn syntax tree (`CommandSourceParser`, the reader the help is built from) — from `Commands/*.cs` and the
generator's own output (`EmitCompilerGeneratedFiles` writes it to `obj/generated/**/*.g.cs`, the same "generator
output is a build artifact" move `Zabbix.Sdk.csproj`'s `ZabbixApiJsonContext` target makes) — rather than a reflection
load of the assembly the same `Build` is producing (which would lock the file). The checked-in `Community.Zabbix.psd1` beside
this file is the template (both arrays empty); the target writes the completed manifest to
`obj/<configuration>/net10.0/module/Community.Zabbix.psd1`, which joins the project's `None` items before the output copy, so the
module folder and every referencing project's output (the transitive copy is how `Zabbix.PowerShell.Tests` gets its
own) carry it. Nothing generated is committed (`docs/adr/0004-conventions.md`).

**Help** — `en-US/Community.Zabbix.PowerShell.dll-Help.xml` (MAML) is built the same way the manifest is: never
hand-edited, and generated at every build from the same XML doc comments already on every cmdlet class and property
(`<para type="synopsis"/"description">` on the class, `<summary>` on each parameter — the same convention the
generator's own emitted cmdlets follow) rather than a second, hand-maintained Markdown/help tree. The
`ZabbixBuildHelp` target (`../Zabbix.PowerShell.Module.targets`, after `CoreCompile`; one
`en-US/<assembly>.dll-Help.xml` and one `reference/` per module) hands `Commands/*.cs`,
the generator's `obj/generated/**/*.g.cs`, and the cmdlet base classes (`ZabbixServerCmdlet.cs`,
`ZabbixGetCmdlet.cs`/`ZabbixNewCmdlet.cs`/`ZabbixSetCmdlet.cs`/`ZabbixRemoveCmdlet.cs`, `ZabbixSingletonGetCmdlet.cs`/`ZabbixSingletonSetCmdlet.cs` — inherited parameters like `-Connection`, `-Id`/`-Filter`/`-Limit`,
`-Force` that a derived `[Cmdlet]` class never redeclares) to `Zabbix.PowerShell.HelpGen`'s `ZabbixBuildHelpTask`
(`tools/Zabbix.PowerShell.HelpGen/`), an MSBuild task that reads that source through the same Roslyn syntax tree
the manifest task does (never a reflection load of the assembly the same `Build` is
producing, which would lock the file; see the comment on `Zabbix.PowerShell.HelpGen.csproj`). PlatyPS is not the MAML
generator here because its Markdown comes from `Get-Help` against the *live* module, so it never sees a C# XML doc
comment on a cmdlet with no help yet ([docs/design/decisions.md](decisions.md)). The words a user
reads come from two seams, never a template:

- **A hand-written command** takes its synopsis and description from its class's doc comment, and its help script,
  `Examples/<Verb-Noun>.ps1`, can replace them with PowerShell's own comment-based help block (`<# .SYNOPSIS ...
  .DESCRIPTION ... #>`, read by HelpGen's `HelpScriptParser`; any other keyword fails the build), so the user text is
  written and reviewed apart from the command's C#. The script's `#region` blocks are the examples: the leading `#` lines
  are the remarks (a wrapped comment is one paragraph, a bare `#` starts the next) and the rest is the code; the text
  before the first region is setup and after the last is teardown, which `tools/Test-ZbxHelpExamples.ps1` runs with
  the examples against a live server. Every hand-written command has one: 188 help scripts across the three modules.
- **A generated command** gets its synopsis, description and examples from `Zabbix.Generator`
  (`ZabbixCmdletEmitter.Help.cs`), written as the class's doc comment (`<para type="synopsis">`,
  `<para type="description">`, `<example title="..."><code>...</code><para>...</para></example>`). They are composed from
  the API object's help words in `schema/overlay.json` (`objects.<object>.help`, carried into `schema/zabbix-api.json`):
  `noun` and `plural`, a `role` clause, a realistic `sample` name, and the `create`/`createSetup` and `change` arguments
  of its examples, with `find`/`found` for an object without a name. The rules: Get gets *the {plural} {role}*, New
  creates *{a noun}*, Set changes *{a noun}*, Remove removes *{plural}*, a builder builds *{a noun}* for the owner
  parameters that take it; a description opens with the object's own sentence (`objects.<object>.summary`) and then
  says which API method runs, how the command selects or what it requires, what it writes and what it needs. An example
  names only the command's own parameters and values from the object's domain (web-01, Linux servers, 10.0.0.5,
  system.cpu.util): Get by name, by its owner (`-Host 'web-01'`) and with `-Filter`; New with every field the server
  requires (an enumeration's first member or a referenced object's sample name fills one the data leaves out, and a
  command whose required fields the data cannot fill gets no example), piped from `Get-ZbxHost` when the object lives
  on a host; Set changing a meaningful field (never an id, a uuid or a name the data did not choose) and again with
  `-PassThru`; Remove piped from a Get with `-Confirm:$false`, and a preview with `-WhatIf`. A help script replaces
  the generated examples for the major commands. The same task writes every generated example, shown or replaced, to
  `obj/<configuration>/net10.0/examples/generated-examples.json` (`GeneratedExamples`), and
  `tools/Test-ZbxHelpExamples.ps1` runs each shown one against a live server, from its fixtures (`tools/Test-ZbxHelpExamples.Fixtures.ps1`), after the example
  that creates the object it works on; an example the server cannot run is marked with its reason in the overlay
  (`objects.<object>.help.notRun`).

A command left with no example from either seam fails the build. The help is written like the manifest: to
`obj/<configuration>/net10.0/module/en-US/`, copied into the module folder, and only when its content changed, so a
build that changes no command leaves the help's timestamps alone. The HelpGen tasks run through
`TaskFactory="TaskHostFactory"`: in a task host process that exits with the task, so a reused MSBuild node never holds
`Zabbix.PowerShell.HelpGen.dll` locked against its next build.
`HelpGenTests` pins the reader, the help-script parser and the MAML writer on a checked-in sample (`HelpGenSample/`, a
golden MAML file). `tests/Zabbix.PowerShell.Tests/HelpTests.cs` is the gate: every exported cmdlet has a real synopsis,
a description, every non-common parameter documented, and at least one example, checked against the built MAML directly
and through `Get-Help` in a real runspace. `HelpQualityTests` holds the text to Microsoft's cmdlet-help voice: no
synopsis is a template ("Gets action objects.") and each is one sentence starting with a verb in the third person; every
description differs from its synopsis and has two to five sentences; no example carries a placeholder (`'<name>'`,
`value1`) or a wrong article ("a action"); every command has an example whose remark is a sentence; and every
hand-written command has a help script, counted above.

The task also reads each property's `[Parameter]` uses per set (position, mandatory, pipeline by
value and by property name), `[SupportsWildcards]`, `[OutputType]` and the `[Cmdlet]`'s default set and
`SupportsShouldProcess`: the MAML has **one syntax line per parameter set** (the default first, `-WhatIf`/`-Confirm`
where supported) with typed values, positions, pipeline input and globbing, INPUTS and OUTPUTS, and an "Online
Version" link. The same run writes **one Markdown reference page per command** (PlatyPS's section order) into
`docs/reference/<module>/` (267, 20 and 23 pages, each folder with its `README.md` index), committed so it reads on GitHub;
docfx publishes it as `reference/<module>/`, the page `Get-Help <cmdlet> -Online` opens.
Six `about_` topics ship in `en-US/`: `about_Community.Zabbix` (the module in one page), `about_Community.Zabbix_Sessions`,
`about_Community.Zabbix_Properties`, `about_Community.Zabbix_Pipeline`, `about_Community.Zabbix_Filters`, and
`about_Community.Zabbix_ErrorHandling`; the module Community.Zabbix.Agent ships its own, `about_Community.Zabbix.Agent`, in its
`en-US/`. A parameter's help is its `<summary>` only and a command's its synopsis/description
paragraphs; maintainers' notes go in `<remarks>`. `HelpTests` pins the syntax to the engine's own
`CommandInfo`, binds every example with `StaticParameterBinder`, and finds every `about_` topic and the online page.

**Views** — `Community.Zabbix.format.ps1xml` is generated beside `Community.Zabbix.types.ps1xml` by the same target
(`ZabbixTypesEmitter.GenerateFormat`): a table, list and wide view per read record (id and name first, then up to six
scalar fields; `clock` as local time). `Zabbix.PowerShell.format.ps1xml` is hand-written for the module's own output
types (connections, the context, sessions, watches, the proxy/server protocol answers). The manifest loads both
(`FormatsToProcess`); `ZabbixFormatPs1XmlTests` checks the regeneration and that every output type with more than
four properties has a view.

## Terminating or not

A failure is one `ErrorRecord` (`ZabbixErrors`: the id names the kind, the category is read from it). Whether it ends
the command is decided by what failed, never by the command's mood; `SourceConformanceTests` finds every
`ThrowTerminatingError` and checks its type is in this table, and that no pipeline method throws past it.

| What failed | Record | Ends the command |
| --- | --- | --- |
| No connection in the runspace (`ZabbixServerCmdlet.BeginProcessing`) | `ZabbixNotConnected` | yes |
| A binding: a parameter set, a validation, a transformation (`ZabbixReferenceAttribute`, `ulong`) | the engine's (`AmbiguousParameterSet`, `MissingMandatoryParameter`, `ParameterArgumentTransformationError`, …) | yes |
| A usage the command checks after binding: `-Search` values (`ZabbixGetCmdlet`), `-CountOutput` with a client-side `-Name`, `New-ZbxNetworkDiscoveryCheck -AdditionalProperty`, `Confirm-ZbxProblem` changes Zabbix refuses in one update (`-Suppress` with `-Unsuppress`, `-Close` with a suppression) or no change at all | `ZabbixSearchValueInvalid`, `ZabbixCountOutputNameNotServerSide`, `ZabbixPropertyInvalid`, `ZabbixParameterInvalid` | yes |
| `Connect-ZbxServer` cannot connect (`ConnectZbxServerCommand`), or cannot use its TLS words (a certificate file it cannot read) | the failure's kind | yes |
| The server refuses `Connect-ZbxServer`'s token or password: an operation's failure a command ends on (`ZabbixCmdlet.Terminating`) | `ZabbixApi.SessionTerminated`, `ZabbixApi.NotAuthorized`, `ZabbixApi.TokenExpired`, `ZabbixApi.LoginFailed` (`AuthenticationError`) | yes: nothing is added |
| TLS words without a mode to choose: a key and a certificate without `-Tls` (`ZabbixWireCmdlet`, `ZabbixServerWireCmdlet`) | `ZabbixTlsModeAmbiguous` | yes |
| A second confirmation a host cannot prompt for (`ZabbixCmdlet.ShouldChange`) | `ZabbixConfirmationUnavailable` | yes |
| `Test-ZbxAgentItem -Session` names no running session (`TestZbxAgentItemCommand`; a `Test-` otherwise writes its finding, never an error) | `ZabbixAgentNotFound` | yes |
| The API refuses a call, a request fails, a connection closed under the call | `ZabbixApi.<Kind>`, the exception's `error.type` | no: the next object goes on |
| A reference or a `-Name` that matches nothing, or several | `ZabbixReferenceNotFound`, `ZabbixNameNotFound`, `ZabbixReferenceAmbiguous` | no |
| A document file that cannot be read or written (`ZabbixDocumentFile`) | `ZabbixFileNotFound`, `ZabbixFileAmbiguous`, `ZabbixFileExists`, `ZabbixFileReadFailed`, `ZabbixFileWriteFailed` | no (`Test-ZbxConfiguration` reports it as its finding) |
| A stop: Ctrl+C, `Select-Object -First` | `PipelineStoppedException`, not an error | — |

`-ErrorAction Stop` makes any of them terminating, as everywhere in PowerShell.

## The modules on the SDK's public API

The modules use the SDK as an application does, so whatever a script does, a C# program does with the same calls:

- **A connection** is `AddZabbixClient(name).WithEndpoint(…).WithCredential(…)` in the connection's own container, with
  `-SkipCertificateCheck`, `-TimeoutSec` and `-Proxy` applied through `ConfigureHttpClientBuilder` and `WithTimeout`.
  The `sid` of `ZabbixWireClient` logs in through the keyed `IZabbixTransport` that `AddZabbixClient` registers under the name.
- **A method no generated client names**, and a read-back by id after `New-` and `Set-`, go through
  `ZabbixClient.SendAsync` with the SDK's JSON contract (`ZabbixJson.Options`, which `ZabbixContracts` looks up; the
  generated commands do the same).
- **The wire commands** open a `ZabbixWireLink` and build a `ZabbixWireClient`
  or the proxy uplink over its exchange; `Receive-ZbxProxyConfig` and the active proxy session accept large replies, as
  `ZabbixActiveProxy` does.
- **An agent session** builds `ZabbixActiveAgent` or `ZabbixPassiveAgent` with its builder (`CreateBuilder(name)`,
  `Configure` with the parameters, `UseLogging`, `UseTimeProvider` and `UseMeterFactory` with the module's logging, clock and
  meters, `ZabbixSessionHost`): its scriptblocks go into the agent's `Items`, its plugins load through the builder's
  `AddPlugins`, and `Test-ZbxAgentItem` is `ZabbixItemDictionary.AnswerAsync`. A passive proxy session builds
  `ZabbixPassiveProxy` the same way, with `OnConfig`, `OnStats` and one `AddProvider` that answer from what the
  `Register-ZbxPassiveProxy*` commands register while it runs; an active proxy session is a `ZabbixProxyUplink` over a link.

No library but `Zabbix.Abstractions` grants the modules its internals, and that one only for the telemetry and
error-type plumbing in `Community.Zabbix.Diagnostics`, since the modules are a layer of the same telemetry
(`ZabbixLayerTelemetry`, `ZabbixErrorType`). Everything else they use is the public API. `ModulePublicApiTests` reads
each module's compiled metadata, generated code included, and holds it to that.

## What the hand-written commands share

- **The SDK root is `ZabbixClient`** (a `ZabbixServerCmdlet` property named after its type, as `ZabbixContext` is), so a
  command keeps a `-Server` parameter (the trapper's nodes) without hiding it.
- **`-Server` is one shape everywhere**: `DnsEndPoint[]`, the nodes of one HA cluster tried in order, each read
  from text by `ZabbixEndPoint.Parse` (`host`, `host:port`, an IPv6 address, `[ipv6]:port`; port 10051 when none
  is named). `Connect-ZbxServer -Trapper` takes the same list. `Start-ZbxActiveAgent -Server` is the one exception:
  text in Zabbix's `ServerActive` grammar (`'a;b'` fails over, `'a,b'` is two independent
  destinations, each sent every value); a list given there is one HA cluster too. `Get-ZbxAgentItem -Agent` is an
  agent, a `DnsEndPoint` whose port is 10050 when the text names none.
- **Objects the operation needs** (not the pipeline): `await ListAsync(ZabbixClient.X.GetAsync(p, ct), ct)` compiles
  against both shapes of a generated client (stream or list), as `WriteAll` does; the generator's shape switch is a
  recompile.
- **References** resolve with `ResolveAsync` inside the operation: one `.get` for all names; a name that matches nothing
  is `ZabbixReferenceNotFound`, a name that matches several (item names repeat across hosts) `ZabbixReferenceAmbiguous`
  — a name never picks one of several objects to change. Shapes for the entities the commands refer to are in
  `ZabbixEntities` (the generator emits the whole table).
- **The server's own protocol** (`Get-ZbxQueue`, `Test-ZbxItem`, `Send-ZbxData`…) finds the trapper with
  `ResolveTrapperAsync(-Server)`: the command's `-Server`, else `Connect-ZbxServer -Trapper`, else the active HA node
  from `hanode.get` — the frontend's own rule (`ZBase.php`), never the API host with a guessed port. The wire runs
  over every node of that list in order, through one connection (`ZabbixWireLinks.Open`, a `ZabbixWireLink`) that the
  command opens for its call and disposes when the call ends (`ZabbixServerWireCmdlet.CallWireAsync`), so certificate
  mode's key is released then; an active proxy session holds its own until it stops. `Send-ZbxData -Server` needs no
  connection: the sender protocol carries no credentials.
  The token it sends as `sid` is the connection's own (`ZabbixConnection.GetTokenAsync`: API token or login session;
  `trapper_auth.c` takes both). `Send-ZbxData` sends trapper-sized batches with `Write-Progress`, each value stamped to
  the nanosecond (never twice the same instant in one pipeline, so the server drops none as a duplicate);
  `Send-ZbxHistory` writes the typed `ZabbixHistoryPushResult`; `Send-ZbxProxyData -Version` is the protocol version the
  uplink announces (`ZabbixProxyUplink.DefaultVersion`, 7.4.0, when omitted).
- **A large export** is batched: `Export-ZbxConfiguration` exports hosts and templates `-BatchSize` (20) at a time
  and merges the answers into one document (a single export of a stock server's templates exhausts the frontend's PHP
  memory limit). The merge model, `ZabbixConfigurationDocument` (`Commands/ZabbixConfigurationDocument.cs`), reads
  JSON, XML and YAML.
- **A reply is its record.** A command writes the SDK's model of what it asked (`Get-ZbxServerStatus` a
  `ZabbixServerStatus`, `Invoke-ZbxScript` a `ZabbixScriptExecuteResult`, `Get-ZbxSli` a `ZabbixSliReport`,
  `Test-ZbxItem` a `ZabbixItemTestResult`), and the format file gives the records with more than a handful of
  members a view. `Test-ZbxItem -Data` binds through the wire's own JSON contract (`ZabbixWireJson.Options`), so a
  hashtable shaped as the frontend sends `item.test` works as it is, by wire or member names, and a field the request
  doesn't model is sent by its wire name.
- **Untyped JSON** (`Invoke-ZbxApi`, and the fields the API leaves free-form) crosses the edge through
  `ZabbixJsonShape`, which produces the shapes `ConvertFrom-Json` users know (`[pscustomobject]`, `object[]`,
  `long`/`double`) but walks System.Text.Json in both directions; PowerShell's `JsonObject` (Newtonsoft underneath) is
  rejected there (`ZabbixJsonShape.cs:22-30`), and a string the server sent stays a string.
- **Typed input binds through PowerShell's own binder.** `ZabbixRecordConverter` (a `PSTypeConverter`) is the
  `<TypeConverter>` of every request, parameters and nested record in `Community.Zabbix.types.ps1xml`, generated at build by
  `ZabbixGenerateTypesPs1Xml` from the schema and loaded by the manifest's `TypesToProcess`. A command declares the
  record type and does nothing else: `-Filter`, nested record lists and `[Zabbix.<Record>]@{…}` casts reach
  `ZabbixJsonBinder`, which remaps keys to wire names and deserializes once through the SDK's JSON contract
  (`ZabbixJson.Options`, looked up by `ZabbixContracts`). Never call
  the binder from a command; `-AdditionalProperty` (`ZabbixPropertyOverrides`, a merge onto a built request), the flat
  name-to-value pre-shape of every `{tag|name, value}` list (`ZabbixNameValueListAttribute<T>`: `-Tag`, `-Header`,
  `-Variable`, webhook `-Parameter`) and `Publish-ZbxDashboard`'s widget shorthands (`graph`/`item` resolved to
  widget fields) are the sanctioned exceptions.
- **Unmodelled fields read as properties.** `ZabbixAdditionalFieldsAdapter` (a `PSPropertyAdapter`) is the
  `<TypeAdapter>` of every read record, so `$h.<field>` reads the record's unmodelled fields, which the adapter reaches
  through `IZabbixAdditionalFields` (the bag is not a public property of the record, so `ConvertTo-Json` does not write
  it). The manifest's
  `RequiredAssemblies = @('Community.Zabbix.Api.dll')` is what lets the adapter register: `<TypeAdapter>` resolves its type
  when the types file is processed. `GetProperties` returns the record's own public properties first, then the bag's
  keys, so `Get-Member` and `Format-List` see both (`ZabbixAdditionalFieldsAdapter.cs:46-57`).
- **Anything that keeps running between commands** (a watcher, an agent, a proxy) is an `IZabbixSession` in the
  runspace's `ZabbixContext.Sessions`, found by kind and name, stopped by its `Stop-` command, by `Remove-Module` or by
  the runspace closing — no process-wide registry.
- **Log events** are the one `ZabbixPowerShellLog` class (`Diagnostics/ZabbixPowerShellLog.cs`), ids in the
  module's 14xxx block: 140xx connections, 141xx completion, 142xx sessions and watches, 143xx script-handler
  invocations; 144xx and above are unassigned.

## Design decisions (each with its test)

The module's decisions in full are in [docs/design/decisions.md](decisions.md).

| Decision | Why | Locked by |
| --- | --- | --- |
| Per-runspace `ZabbixContext` in a `ConditionalWeakTable<Runspace, …>` | Measured on pwsh 7.6.6: a binary module's `SessionState` exists only when imported through its manifest (`null` when imported by dll path), `PSCmdlet.SessionState` is the caller's scope, and completers/transformations cannot reach the module's state; `Runspace.DefaultRunspace` keys all of them. Closed on runspace close and on `Remove-Module` | `Two_runspaces_have_their_own_contexts`, `Removing_the_module_closes_the_runspaces_connections` |
| A connection is leased per call | Disconnect/reconnect in any runspace never disposes a container under an in-flight call; the last lease out disposes it | `Disconnecting_does_not_dispose_a_connection_under_another_runspaces_call` |
| `StopToken` = `Cmdlet.PipelineStopToken` (7.6) linked with the command's own cancellation | Ctrl+C reaches the socket; `Select-Object -First` cancels the read | `Ctrl_c_reaches_the_socket`, `A_downstream_command_that_has_enough_cancels_the_read` |
| The pipeline thread drains a `BlockingCollection` | The cmdlet contract is synchronous; a channel reader would need sync-over-async (RS0030) | every command test |
| One `ILoggerProvider`: Trace→Verbose, Debug→Debug, Information→Information (tagged), Warning→Warning, Error→`ErrorRecord` `ZabbixLog.<event name>`; the resilience pipeline's `Polly` category→Verbose at every level | Every SDK log line reaches the PowerShell stream a user already watches; routed by an `AsyncLocal` to the command that caused the line, in its runspace; a retried call's tries narrate its one result or error record, so they never print as warnings | `Each_log_level_reaches_its_own_stream`, `Sdk_logs_of_two_runspaces_reach_their_own_commands`, `An_unreachable_server_is_one_error_and_its_tries_are_verbose_not_warnings` |
| Errors: id = `ZabbixApi.<Kind>` or the telemetry's `error.type`; category from the kind | One word names a failure in `$Error`, a span and a metric; never from prose | `An_api_error_is_one_error_record_named_by_its_kind` |
| OTLP export only when `OTEL_EXPORTER_OTLP_ENDPOINT` is set at import; the OpenTelemetry SDK is never loaded otherwise | Telemetry costs nothing until asked for; an unloaded dependency cannot conflict | `Otlp_export_follows_the_endpoint_set_at_import`, `The_module_imports_in_a_fresh_pwsh_process` |
| Input binding is a `PSTypeConverter` per record over the SDK's JSON contract, registered by a generated `Community.Zabbix.types.ps1xml` | `LanguagePrimitives` fails on `IReadOnlyList<T>` properties and on `PSCustomObject` → `Hashtable`; PowerShell's JSON writes C# names; a converter needs no call site | `ZabbixRecordConverterTests`, `ZabbixTypesPs1XmlTests` |
| `-AdditionalProperty` wins over a named parameter; required fields are checked after the merge | `Mandatory` would defeat the `-AdditionalProperty`-only idiom | `New_ZbxHostGroup_Properties_wins_over_the_named_parameter`, `No_generated_New_or_Set_parameter_is_Mandatory_for_an_API_required_field` |
| `-Id` and the New/Set read-back select by the entity's `*ids` option | `template.get` answers `[]` to `filter.templateid` | `Every_generated_entitys_id_selection_uses_its_own_ids_option_when_its_GetParameters_carries_one` |
| `System.Management.Automation` 7.6 as a reference (`ExcludeAssets=runtime`) | Microsoft Learn, "Choosing the right PowerShell NuGet package": version-specific APIs (`PipelineStopToken`, `ArgumentCompleterFactoryAttribute`) need the version's reference, never shipped | build |

## Assembly isolation — measured, not yet built

**ASP.NET Core in pwsh.** pwsh ships no ASP.NET Core shared framework. `Zabbix.Proxy` (the
listeners: passive agent, proxy, trapper) compiles against it, so the module projects reference it with
`DisableTransitiveFrameworkReferences` — with the framework reference flowing, the .NET SDK resolves `Microsoft.Extensions.*`
to the framework, stops copying them into the module folder and `Import-Module` fails on
`Microsoft.Extensions.Logging.Abstractions`. The two modules that start a listener (`Community.Zabbix.Agent`, `Community.Zabbix.Proxy`)
therefore carry the four assemblies the listeners need — `Microsoft.AspNetCore.Connections.Abstractions`,
`Microsoft.Extensions.Features`, Kestrel's socket transport and `Microsoft.AspNetCore.Hosting.Abstractions` — copied
from the ASP.NET Core runtime packs (NuGet packages) at a pinned version
(`../Zabbix.PowerShell.AspNetCoreTransport.targets`), so `Start-ZbxPassiveAgent` and `Start-ZbxPassiveProxy` run
inside pwsh. The module `Community.Zabbix` carries none of them (ADR 0003).

**One copy per platform.** A runtime pack's assemblies are ReadyToRun images compiled for one operating system and
processor, and the runtime refuses an image built for another (`BadImageFormatException`: linux-x64's copies do not load
on osx-arm64 or win-x64). Each listener module therefore carries the four for every platform pwsh 7.6 is released for
(`win-x64`, `win-x86`, `win-arm64`, `linux-x64`, `linux-arm64`, `linux-arm`, `osx-x64`, `osx-arm64`; Alpine's pwsh runs
`linux-x64`'s), at `runtimes/<platform>/lib/net10.0/`, about 2 MB in all, and none beside its assembly. The module's
import (`ZabbixAgentModule`, `ZabbixProxyModule`) registers `ZabbixModulePlatformAssemblies`, an
`AssemblyLoadContext.Resolving` handler that answers a reference from the running platform's folder, only for an
assembly that folder holds and only at a version at least the requested one (`ZabbixModulePlatformAssembliesTests`,
which also check each folder's images name its platform's machine).

**The siblings' shared assemblies.** An installed `Community.Zabbix.Agent`/`Community.Zabbix.Proxy` folder holds only what
`Community.Zabbix`'s does not; `ZabbixModuleDependencies` (registered at `Community.Zabbix`'s import) answers a sibling's reference to a
shared assembly from `Community.Zabbix`'s folder through `AssemblyLoadContext.Resolving`. The handler is process-wide, so it
answers only for an assembly of `Community.Zabbix`'s own closure (its `Community.Zabbix.PowerShell.deps.json`, read by
`AssemblyDependencyResolver`) and only when the file's assembly version is at least the requested one; a stray file in
the folder, or a reference to a newer version than `Community.Zabbix` ships, gets no answer (`ZabbixModuleDependenciesTests`).
Isolation in its own load context is still the open step below.

Measured on pwsh 7.6.6 (.NET 10.0.12):
- pwsh itself ships no `Microsoft.Extensions.*` beyond `ObjectPool` 10.0.0.0 (the same assembly version this module
  carries), so the module imports cleanly in a fresh process (`The_module_imports_in_a_fresh_pwsh_process`).
- **The dependency conflict reproduces** when another module loads an older copy first: after
  `Add-Type -Path Microsoft.Extensions.Logging.Abstractions 8.0.0`, `Import-Module Community.Zabbix` fails with
  `FileLoadException … 10.0.0.0 … does not match`. The reverse order works only for a reference: an assembly compiled
  against 8.0 binds to the loaded 10.0, but loading the 8.0 file by path (`Add-Type -Path`, a module's
  `RequiredAssemblies`) fails with `Assembly with same name is already loaded`. The scenarios, siblings and
  `Remove-Module` included, are tests in `ProcessTests` and a table in `docs/KNOWN-LIMITATIONS.md`.
- The OpenTelemetry SDK loads inside pwsh 7.6 and exports all three signals over OTLP/HTTP; it is loaded only when the
  endpoint is set (the JIT loads it with the first method that uses it, and those calls are guarded).

The designated seam (Microsoft Learn, "Resolving PowerShell module assembly dependency conflicts") is a custom
`AssemblyLoadContext` behind a cmdlet assembly that references only `System.Management.Automation`. It cannot hide
the conflict measured above by itself: `Microsoft.Extensions.Logging.Abstractions`, `…DependencyInjection.Abstractions`
and `…Options` are in the SDK's public surface (`ZabbixClient.Loggers`, `AddZabbixClient`), and PowerShell hides types from
non-default contexts, so the records and the root must stay in the default context. What an ALC can isolate is the
implementation-only set (Polly, `Microsoft.Extensions.Http.Resilience`, BouncyCastle, OpenTelemetry, the caching
package): the next step is to move them behind a `Community.Zabbix.PowerShell.Engine` assembly resolved by the module's own
`AssemblyLoadContext` from a `Dependencies/` folder, with a test that pre-loads an older copy of each and imports.

Provided "as is", without warranty of any kind: use at your own risk.

This project is not affiliated with, endorsed by or sponsored by Zabbix LLC. Zabbix is a registered trademark of Zabbix LLC.
