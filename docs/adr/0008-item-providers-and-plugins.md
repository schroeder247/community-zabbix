<!--
This Source Code Form is subject to the terms of the Mozilla Public
License, v. 2.0. Copyright (c) 2026 schroeder247. SPDX-License-Identifier: MPL-2.0
-->
# 0008. Item providers in the contracts, and plugins loaded from a folder

- **Status:** Accepted; amended 2026-11-03 (the folders above a plugin; below)
- **Date:** 2026-11-03
- **Amended by:** [0013](0013-builders-verbs-and-typed-values.md): `IZabbixItemsBuilder` is `Name` and `Services`, and an
  agent's providers are keyed by its name, so an active and a passive agent of one name answer the same items.

## Context

An agent answers item keys. A library that wants to offer keys (a set of SQL Server checks, a hardware vendor's
sensors) should be able to implement them against a small contract, and an agent should be able to take them either
from code or from a folder its configuration names, as Zabbix's own agents take theirs.

**Before this decision**, items were handlers registered on one agent, key by key:

| Piece | Where it lives |
| --- | --- |
| `ZabbixItemHandler`, the delegate that answers a key | `Zabbix.Agent` (namespace `Community.Zabbix.Hosting`) |
| `ZabbixAgentAnswer`, the value or the reason a key is not supported | `Zabbix.Wire` (namespace `Community.Zabbix.Protocol`), also the getter's result |
| `ZabbixItemKey`, the key and its parameters | `Zabbix.Abstractions` |
| `AddHandler(name, …)`, `UseHandlers(table)` | the active agent's builder (`Zabbix.Agent`) and the passive agent's (`Zabbix.Proxy`) |

At start an agent composed its table from the built-in four (`agent.ping`, `agent.version`, `agent.hostname`,
`system.uname`), the application's live table and every `AddHandler` set, the later over the earlier
(`ZabbixItemHandlers.Compose`). The PowerShell agent module added scriptblock handlers the same way
(`Register-ZbxAgentHandler`).

That worked for an application's own keys, and fell short for a library of them:

- **The contract is spread over three packages.** To declare one handler a library references `Zabbix.Agent`, which
  brings the wire, its TLS stack and the hosting packages, although it needs three types.
- **There is no unit larger than a key.** Keys that share a connection, a configuration and a lifetime have no type to
  live in, and no list of what they answer: a key is known only by asking it.
- **Nothing loads from a folder.** Every key is compiled into the application that runs the agent.

### How Zabbix's agents take items

| Agent | Mechanism | Configuration | Process |
| --- | --- | --- | --- |
| Zabbix agent (C) | loadable modules: shared libraries exporting `zbx_module_api_version`, and optionally `zbx_module_init`, `zbx_module_item_list` (an array of `ZBX_METRIC`: key, flags, function, test parameter), `zbx_module_item_timeout` and `zbx_module_uninit` | `LoadModulePath` (the folder) and one `LoadModule` line per module; the agent does not start when a listed module cannot be loaded | in the agent's process |
| Zabbix agent 2 | built-in plugins (Go, compiled in) and, since 6.0, loadable plugins: executables built separately against a package shared with the agent, implementing one or more of the Exporter, Configurator and Runner interfaces | `Plugins.<Name>.System.Path` names each executable; its settings are `Plugins.<Name>.<Parameter>` | a process of its own, connected over Unix sockets or Windows named pipes |
| Both | user parameters: `UserParameter=<key>,<command>` | one line per key | a process per check |

Zabbix's documentation also records what loadable modules cost: binary compatibility only within one LTS cycle, and
modules built inside the Zabbix source tree.

### How .NET loads plugins

Microsoft's plugin guidance ("Create a .NET application with plugins") loads each plugin into an
`AssemblyLoadContext` of its own, resolving its dependencies through an `AssemblyDependencyResolver` over its
`.deps.json`. The contract assembly is shared: the plugin references it without copying it (`Private="false"`,
`ExcludeAssets="runtime"`), so the host and the plugin see one type. Native AOT does not load assemblies at run time
("No dynamic loading, for example, `Assembly.LoadFile`", the deployment's documented limitations), and the libraries of
this repository are AOT-compatible by default (`Directory.Build.targets`).

## Decision

### 1. The principle

An item is a contract a library implements against `Zabbix.Abstractions` alone. An agent takes items from code, chosen
at compile time, or from a folder its configuration names, chosen at start. Both end in the table an agent already
composes, so the agent's dispatch, timeouts, concurrency limit and telemetry are the same for every key.

### 2. The contract lives in `Zabbix.Abstractions`

`Zabbix.Abstractions` is already the package "to code against the SDK without its implementation" (its package page),
and takes abstractions only ([ADR 0006](0006-protocol-split.md), §2). The item contract joins it:

- **`ZabbixItemHandler` and `ZabbixAgentAnswer` move into it**, their namespaces unchanged (`Community.Zabbix.Hosting`,
  `Community.Zabbix.Protocol`), so source that uses them compiles as before. `Zabbix.Agent` and `Zabbix.Wire` already reference
  `Zabbix.Abstractions`.
- **`ZabbixItemProvider`**, an abstract class (an extension point that may gain members, the shape `ZabbixCredential`
  has; the Framework Design Guidelines: "DO favor defining classes over interfaces"):

```csharp
namespace Community.Zabbix.Hosting;

public abstract class ZabbixItemProvider
{
    /// The keys this provider answers, by name; it is asked only for these.
    public abstract IReadOnlyList<ZabbixItemDefinition> Items { get; }

    /// The value of one key, or why it is not supported.
    public abstract ValueTask<ZabbixAgentAnswer> GetValueAsync(ZabbixItemKey key, CancellationToken cancellationToken);
}

/// A key's name, what it measures, and a key to try it with (ZBX_METRIC's test parameter).
public sealed record ZabbixItemDefinition
{
    public ZabbixItemDefinition(string keyName, string description);   // a name without parameters
    public string KeyName { get; }
    public string Description { get; }
    public string? TestKey { get; init; }                               // a key of the same name
}
```

- **`IZabbixItemsBuilder`** (`Name`, `Services`, and `ServiceKey`, the key the agent resolves its providers under),
  which the active and the passive agent's builders are. A provider is a keyed `ZabbixItemProvider` service of that
  key, so every registration verb dependency injection has applies, and `AddItems` names the three a library needs:
  `AddItems<TProvider>()` (built by the container and disposed with it), `AddItems(provider)` (the caller's instance,
  which stays the caller's) and `AddItems(services => …)` (a factory). The instance and factory forms are generic over
  the builder, so the caller's chain keeps its type; the type form names its provider explicitly, which C# cannot
  combine with inferring the builder, so the active and the passive agent's builders each have their own
  `AddItems<TProvider>()` returning themselves, and the contract's returns the `IZabbixItemsBuilder`. A library then
  offers one registration for both agents:

```csharp
public static TBuilder AddSqlServerItems<TBuilder>(this TBuilder builder, Action<SqlServerItemsOptions>? configure = null)
    where TBuilder : IZabbixItemsBuilder
{
    string agent = builder.Name;                                       // the factory below captures it
    if (configure is not null) builder.Services.Configure(agent, configure);
    builder.AddItems(services => new SqlServerItems(
        services.GetRequiredService<IOptionsMonitor<SqlServerItemsOptions>>().Get(agent)));
    return builder;
}
```

Zabbix agent 2's interfaces map onto what .NET already has, so the contract adds none of them:

| Agent 2 | Here |
| --- | --- |
| Exporter (`Export`) | `ZabbixItemProvider.GetValueAsync` |
| Configurator (`Configure`, `Validate`) | options named after the agent, bound and validated before the agent starts |
| Runner (`Start`, `Stop`) | an `IHostedService` the library registers beside its provider, or the provider's `DisposeAsync` |

### 3. Items from code

```csharp
await using ZabbixRunner agent = await ZabbixRunner.StartActiveAgentAsync("web-01", a => a
    .SendTo("zbx-server-01")
    .AddSqlServerItems(o => o.ConnectionString = connectionString)    // a library's registration
    .AddItems<DiskItems>()                                            // a provider built by the container
    .AddHandler("app.version", _ => "2.4.1"));                        // one key, as before
```

- **One table.** At start the agent adds each provider's `Items` to its table, each name answered by the provider's
  `GetValueAsync`. The order of precedence is `AddHandler` (and the live table), then providers, then the built-in
  four, so an application can always replace one key of a library.
- **A name is one provider's.** Two providers that declare one key name fail the start with an error that names both;
  a key is never answered by whichever registered last.
- **A provider is a singleton of its agent**, built by the container (constructor injection) and disposed with it. A
  provider that reads options named after its agent is registered with the factory form, which captures the agent's
  name, as `AddSqlServerItems` does above.
- **This path is AOT-compatible**: `AddItems<TProvider>` annotates `TProvider` for its public constructors, and
  nothing is found by reflection.

### 4. Plugins from a folder: `Community.Zabbix.Agent.Plugins`

A new package, assembly `Community.Zabbix.Agent.Plugins`, marked not AOT-compatible as the EF store is, so the AOT-compatible
packages never reference it:

```csharp
await using ZabbixRunner agent = await ZabbixRunner.StartActiveAgentAsync("web-01", a => a
    .SendTo("zbx-server-01")
    .AddPlugins(configuration.GetSection("Zabbix:ActiveAgents:web-01:Plugins")));
```

```json
"ActiveAgents": {
  "web-01": {
    "Hostname": "web-01",
    "ServerActive": "zbx-server-01",
    "Plugins": {
      "Path": "/opt/zabbix-dotnet/plugins",
      "Load": [ "SqlServer" ],
      "SqlServer": { "ConnectionString": "Server=db-01;…" }
    }
  }
}
```

- **The folder holds one subfolder per plugin**, `<Path>/<Name>/`, the output of `dotnet publish` for the plugin's
  project: its assembly `<Name>.dll`, its `.deps.json` and its dependencies.
- **Only listed plugins load.** `Load` names each plugin, as `LoadModule` lines and `Plugins.<Name>.System.Path`
  parameters do; a file in the folder that nothing lists is never opened.
- **Each plugin has its own load context.** An `AssemblyLoadContext` per plugin resolves its dependencies through an
  `AssemblyDependencyResolver` over its `.deps.json`, so two plugins, or a plugin and the agent, can depend on
  different versions of one package. The shared assemblies (every `Community.Zabbix.*` assembly, and the `Microsoft.Extensions.*`
  abstractions, `Options` and `Primitives`) always resolve from the agent's context, so a plugin's provider is the agent's
  `ZabbixItemProvider`.
- **A plugin names its entry; nothing is scanned.** The plugin assembly carries
  `[assembly: ZabbixPlugin(typeof(SqlServerPlugin))]`, and the entry, a `ZabbixPluginEntry` (an abstract class in
  `Zabbix.Abstractions`), has one method, `Register(IZabbixItemsBuilder items, IConfiguration settings)`, given the
  plugin's own section (`Plugins:SqlServer`). That is the call a library's compile-time registration makes, so one
  library ships both ways.
- **Loading is part of registration.** The plugins load while the agent's registration runs, before the container is
  built and its options validated. A listed plugin that is missing, does not load, names no entry, or was built
  against a later major version of `Zabbix.Abstractions` fails the start, as Zabbix's agent does not start without a
  module it was told to load.
- **Nothing unloads.** The contexts are not collectible: a plugin is updated by restarting the process, as a Zabbix
  module is.
- **A plugin is in the agent's process.** It runs with the agent's account and memory. A handler that throws is
  answered as not supported, as before; a crash on a thread of the plugin's own, or a stack overflow, ends the process.
  That is the trade-off Zabbix's loadable modules make; agent 2's process-per-plugin model is the isolated
  alternative, left for a later decision (below).
- **A plugin other accounts can write is refused.** Beyond the explicit list, on Linux and macOS the loader refuses a
  plugin whose folder or any file in it is writable by its group or by others: such a file is code another account
  chooses, run with the agent's account. On Windows the folder's access list decides who may write, and is left to it.
- **The PowerShell agent sessions load the same plugins.** `Start-ZbxActiveAgent` and `Start-ZbxPassiveAgent` take
  `-Plugin` (the names), `-PluginPath` (the folder) and `-PluginSetting` (each plugin's settings, as a hashtable whose
  nested hashtables are sections), and call `AddPlugins` in the session's own registration.

### 5. The dependency policy holds

`Zabbix.Abstractions` references `Microsoft.Extensions.DependencyInjection.Abstractions` directly (it is already in
its closure, through `Microsoft.Extensions.Options`) and gains `Microsoft.Extensions.Configuration.Abstractions` for
the plugin entry's settings; both are abstractions. `Zabbix.Agent.Plugins` takes `Zabbix.Abstractions` and nothing
else: `AssemblyLoadContext` and `AssemblyDependencyResolver` are in the base class library, and `AddPlugins` is generic
over `IZabbixItemsBuilder`, so it needs neither agent's package (`DesignConformanceTests.Dependencies` holds this).

## Rejected

- **A separate `Community.Zabbix.Agent.Abstractions` package.** A package for five types, beside the package that
  already holds the shared contracts; and `Zabbix.Wire` would reference an agent-named package for the getter's result.
- **Scanning a plugin's assemblies for provider types.** It loads and inspects every type, runs static constructors of
  types nobody asked for, and cannot be trimmed; one assembly attribute names the entry.
- **Loading every assembly in the folder.** A file dropped into the folder would run with the agent's account; the
  configuration lists each plugin.
- **Loading into the default context** (`Assembly.LoadFrom`). Plugins and the agent would share one version of every
  dependency, and the first to load would decide it.
- **MEF** (`System.Composition`). A second composition model beside dependency injection, which already composes the
  agent.
- **Interfaces for the provider and the entry.** An interface cannot gain a member without breaking its implementers;
  an abstract class can.
- **Collectible contexts and reload on change.** A context unloads only when nothing references its types (a running
  check, a timer, a cached delegate), which a long-running agent cannot promise.

## Consequences

- **1.0.0.** §2 moves two types between assemblies. 1.0.0 is unreleased, so they move outright; after a release, a
  move needs a `[TypeForwardedTo]` in the old assembly. The plugins package ships with 1.0.0 too: its surface is in its
  `PublicAPI.Shipped.txt` from the start.
- **Package ids.** [ADR 0002](0002-package-ids.md)'s list gains `Community.Zabbix.Agent.Plugins`, and
  `DesignConformanceTests.Dependencies` gains its row.
- **Tests.**
  - Precedence: `AddHandler` over a provider, a provider over a built-in key.
  - Two providers naming one key fail the start.
  - A provider is asked only for its keys, and is disposed with its agent.
  - A sample plugin, built during the test run against a dependency version the agent does not use, loads beside the
    agent and answers.
  - Refusals: an unlisted file is never opened; a missing plugin, a plugin without an entry, and a plugin built against
    a later major version each fail the start.
  - The AOT probe stays unchanged, since it does not reference the plugins package.
- **Documentation.** The items guide (`docs/guides/items.md`) covers handlers, providers and plugins in C# and
  PowerShell; the configuration guide gains the `Plugins` rows, and the known limitations a note that a plugin shares
  the agent's process.

## Not decided here

- **Process plugins.** A plugin as a process of its own, started by the agent and spoken to over a socket, is agent 2's
  model: language-independent, isolated, and AOT-compatible. It is a later ADR.
- **Command items.** Zabbix's `UserParameter=<key>,<command>` runs a process per check. It is a later ADR, with
  Zabbix's own rules for parameters (`UnsafeUserParameters`) as its starting point.

## The proposal's open questions, answered

1. **The names** are the proposed ones: `ZabbixItemProvider`, `ZabbixItemDefinition`, `IZabbixItemsBuilder`,
   `ZabbixPluginEntry`, `[ZabbixPlugin]` and the package `Community.Zabbix.Agent.Plugins`.
2. **Value types are left out.** A definition carries no value type, and `Initialize-ZbxAgentHost` keeps one
   `-ValueType` for all: the enum is generated into `Zabbix.Api`, which `Zabbix.Abstractions` cannot reference, and a
   later `init` property on the definition adds one without breaking a provider.
3. **The contract covers keys only.** A plugin adds keys to the active and the passive agent; sources for the push
   agent (`AddZabbixAgent`) are its own registration (`AddPoll`, `AddSource`) and stay outside the contract.
4. **The PowerShell sessions load the same plugins** (§4). A folder of `.ps1` handler scripts is not provided; the
   sessions keep their scriptblock handlers (`Register-ZbxAgentHandler`).
5. **The loader refuses a plugin other accounts can write** (§4), on Linux and macOS.

## Amendment (2026-11-03): the folders above a plugin

Section 4 refuses a plugin whose own folder or files other accounts can write. A folder above it that they can write lets
them replace the plugin's folder with their own, whose modes pass, so the loader refuses that too, on Linux and macOS,
unless the folder's sticky bit stops them (`/tmp`). Whether a file's owner is the agent's account or an administrator's is
not checked: the rule stays the file mode's.

## Amendment (2026-11-04): `AddItem`, `Items` and one rule for keys

[ADR 0010](0010-agent-proxy-client-classes.md) §4 replaces the order of precedence in section 3 with one rule. An agent
owns one `ZabbixItemDictionary` as `Items`: `Add` refuses a name held, the indexer replaces, and at start the built-in
keys fill the names left free. `AddHandler` on a builder is `AddItem`, and `UseHandlers` is removed: a key added while an
agent runs goes through `Items.Add`. In a registration, providers are added first and each `AddItem` then replaces. The
samples' `ZabbixRunner` is removed too, and the contract's types are in `Community.Zabbix` (ADR 0010, §7).
