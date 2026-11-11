<!--
This Source Code Form is subject to the terms of the Mozilla Public
License, v. 2.0. Copyright (c) 2026 schroeder247. SPDX-License-Identifier: MPL-2.0
-->
# 0010. Clients, agents and proxies are classes you construct

- **Status:** Accepted
- **Date:** 2026-11-04
- **Supersedes:** [0007](0007-container-free-runners.md) §3 to §5 (`ZabbixRunner`). Its §1 and §2 stand.
- **Amended by:** [0012](0012-typed-bcl-fluent-api.md), which replaces the `Callback` delegates of §5 with `Func` and `Action`;
  [0013](0013-builders-verbs-and-typed-values.md), for §2: the positional constructors and the instance `Add…` methods go, and every component has a builder.
- **Amends:** [0008](0008-item-providers-and-plugins.md) (`AddHandler`, `UseHandlers`, precedence) and
  [0009](0009-bundled-items.md) (where the bundled keys sit).

## Context

A reader who wanted an agent had no agent class to find. `ZabbixRunner.StartActiveAgentAsync` returned a `ZabbixRunner`,
and the types that did the work were named for how they get hosted: `ZabbixActiveAgentHost`, `ZabbixProxyHost`. The
names `ZabbixAgent<T>` and `ZabbixPassiveAgent` were taken by a pump and a request handler. The wire had three classes
named after Zabbix's tools rather than one client, and they required a cancellation token that no other client required.

### Where it went wrong

ADR 0007 asked the right question, how to run an agent without a container, and answered it in the wrong direction. The
components' constructors took what a container supplies, because the components had been written as hosted services
first. The ADR kept that and added a runner, which builds a private container and drives it, so the container stayed
the primary path and a caller reached it through a lambda.

- It rejected "a hand-written constructor" because two paths drift apart. That holds only while the container is the
  primary path. When the registration calls the constructor there is one path, which is what its own Azure row says
  ("the container path calls the constructors").
- Its comparison was of registries (Logging, OpenTelemetry, Quartz) and buses. It left out the closest neighbours, the
  long-running components in Microsoft's SDKs and the type shapes the Framework Design Guidelines describe.
- It named the mechanism. The ADR says "it runs components", and every signature then carried that word where the
  reader was looking for an agent.

The builders' own summary gives the root cause: "the container holds the state". An agent was a named set of options
in a container, so there was no object to construct.

### What the documents say

These decide the design, and [precedence](../design/decisions.md#precedence) makes them win over ADR 0007. Their
Framework Design Guidelines pages reprint the 2008 edition and say some of it may be out of date, so where modern .NET
has plainly moved on, this ADR says so.

| Source | Rule | What it decides here |
| --- | --- | --- |
| [Framework design digest](https://github.com/dotnet/runtime/blob/main/docs/coding-guidelines/framework-design-guidelines-digest.md) (`dotnet/runtime`) | Start from scenario code and keep it to a few lines. The first type of a scenario is an aggregate component. Model real-world concepts. Prefer constructors over factories. Support Create-Set-Call, with properties settable in any order and errors that say what to do | Agent and proxy classes, read at start (§2) |
| [Constructor design](https://learn.microsoft.com/en-us/dotnet/standard/design-guidelines/constructor) | Parameters are shortcuts for setting main properties, with no difference in semantics, and take the property's name. Do minimal work | Constructors only capture (§2) |
| [Member overloading](https://learn.microsoft.com/en-us/dotnet/standard/design-guidelines/member-overloading) | Overloads rather than default arguments; allow `null` for optional arguments; shorter overloads call the longest, and only it is virtual | Constructor overloads (§2) |
| [TAP](https://learn.microsoft.com/en-us/dotnet/standard/asynchronous-programming-patterns/task-based-asynchronous-pattern-tap) | A method that can be cancelled has an overload that takes `cancellationToken`, usually beside one that does not. Throw directly only for usage errors | Client overloads (§6), `StartAsync` (§3) |
| [Sealing](https://learn.microsoft.com/en-us/dotnet/standard/design-guidelines/sealing) | Do not seal without a good reason, and being unable to think of a scenario is not one | Unsealed classes (§2) |
| [Abstract classes](https://learn.microsoft.com/en-us/dotnet/standard/design-guidelines/abstract-class) | No public constructor on an abstract type; ship a concrete one | `ZabbixComponent` (§3) |
| [Standard exceptions](https://learn.microsoft.com/en-us/dotnet/standard/design-guidelines/using-standard-exception-types) | `InvalidOperationException` for inappropriate state, `ArgumentException` for bad arguments | §3, §4 |
| [Collections](https://learn.microsoft.com/en-us/dotnet/standard/design-guidelines/guidelines-for-collections) | No settable collection properties, no snapshots from properties, the `Dictionary` suffix on an `IDictionary` | `Items`, `Statuses` (§4) |
| [Names of common types](https://learn.microsoft.com/en-us/dotnet/standard/design-guidelines/names-of-classes-structs-and-interfaces) | Classes are nouns. A delegate other than an event handler ends in `Callback`. Consider ending a derived class's name with its base's | §5 |
| [Implementing `DisposeAsync`](https://learn.microsoft.com/en-us/dotnet/standard/garbage-collection/implementing-disposeasync) | An unsealed class has `protected virtual DisposeAsyncCore` and `Dispose(bool)` | §3 |
| [Logging for libraries](https://learn.microsoft.com/en-us/dotnet/core/extensions/logging/library-guidance) | Public entry points that create helpers take `ILoggerFactory`; default to null logging | §2 |
| [Options for libraries](https://learn.microsoft.com/en-us/dotnet/core/extensions/options-library-authors) | `Add{Service}`, taking a named configuration section, or an action | §5 |
| [DI guidelines](https://learn.microsoft.com/en-us/dotnet/core/extensions/dependency-injection/guidelines) | Factories fast and synchronous; a container disposes what it creates; avoid `BuildServiceProvider` while configuring | §2, §3 |
| [Azure SDK guidelines](https://azure.github.io/azure-sdk/dotnet_introduction.html) | A `Client` suffix, a client in the root namespace, virtual members for mocking | §6, §7 |
| [Azure SDK general design](https://azure.github.io/azure-sdk/general_design.html) | Keep the number of clients low for a clear starting point. Split only for distinct use cases unlikely to be used together. Give multiple clients descriptive names, and do not give similar names to clients that do different things | Two clients (§6) |
| [`private protected`](https://learn.microsoft.com/en-us/dotnet/csharp/language-reference/keywords/private-protected) (C# reference) | `InternalsVisibleTo` extends it to derived classes in the named assembly | The Proxy package's classes derive from `ZabbixComponent` |

### What open SDKs do

Read from their source, to check the shapes above against code people use.

| Type | Construction | Handlers | Lifetime | Hosted through |
| --- | --- | --- | --- | --- |
| `HttpListener` (`dotnet/runtime`) | default constructor, then set | `Prefixes.Add` | `Start`, `Stop`, `Close` | |
| `TcpListener` (`dotnet/runtime`) | public constructors | | `Start`, `Stop`, `Dispose` | |
| `HubConnection` (`dotnet/aspnetcore`) | public constructors, or a builder returning the named type | `On`, `Remove` | `StartAsync(ct = default)`, `StopAsync(ct = default)`, `DisposeAsync` | |
| `ServiceBusProcessor`, `EventProcessorClient` (Azure SDK) | from a client, or public constructors over a base `EventProcessor<T>` | events | `StartProcessingAsync`, `StopProcessingAsync` | |
| `Server` (gRPC) | default constructor, then collections | `Services.Add`, `Ports.Add` | `Start`, `ShutdownAsync` | |
| `MetricServer`, `MetricPusher` (prometheus-net) | minimal, or options, over a base `MetricHandler` | | `Start`, `StopAsync`, `Dispose` | |
| `MqttServer` (MQTTnet) | options and logger | events | `StartAsync`, `StopAsync`, `Dispose` | |
| `BackgroundJobServer` (Hangfire) | default, options and storage overloads | | `Start`, `Stop`, `Dispose` | a separate `BackgroundJobServerHostedService` |

None of these classes implements `IHostedService` itself; where one is hosted, an adapter does it. Most are unsealed.
`EventHubProducerClient` is a sender constructed directly, with the `Client` suffix, a protected constructor and virtual
members.

Two more decide how to group a client's calls. `ServiceBusClient` and `ServiceBusAdministrationClient` are one package's two
entry points, because the second speaks a different protocol to the same service. `HttpClient` is named after its protocol and
is not bound to one host: `BaseAddress` is optional, a request can name any host, and it is set before the first request.

## Decision

### 1. Three families

| You want to | Family | Classes |
| --- | --- | --- |
| Call Zabbix | Clients | `ZabbixClient`, `ZabbixWireClient` |
| Be an agent | Agents | `ZabbixActiveAgent`, `ZabbixPassiveAgent`, `ZabbixPushAgent` |
| Be a proxy | Proxies | `ZabbixActiveProxy`, `ZabbixPassiveProxy` |

A client is named after the protocol it speaks (§6). An agent or proxy class is the thing itself.

```csharp
await using var agent = new ZabbixActiveAgent("zbx-server-01", "web-01");
agent.Items.Add("app.version", _ => "2.4.1");
agent.Items.Add("app.disk.free", key => new DriveInfo(key[0]).AvailableFreeSpace);
await agent.StartAsync();
```

### 2. The class is primary, and the registration calls its constructor

Each agent and proxy supports Create-Set-Call, and is unsealed with no virtual members beyond the dispose pattern:

| Constructor | For |
| --- | --- |
| `()` | set everything through `Options` |
| `(serverActive, hostname)`, and its kin | the main properties, as a shortcut |
| `(options)` | options the application already has |
| `(options, loggers, time, meters)` | the application's logging, clock and meters; `null` takes the default |

A constructor only captures. `Options` is read at start, so what a component needs can be set in any order. No
constructor builds a container. `AddZabbixActiveAgent` and the other registrations call an internal constructor from named
options, resolve the class as a keyed service under the name, and host it through an adapter.

`ZabbixRunner`, `ZabbixRunnerOptions` and the static starters are removed. An application with several components has a
host, or starts each one.

### 3. One lifecycle

The agents and proxies derive from `ZabbixComponent`, whose phases are internal, so only this SDK derives from it directly.

- `StartAsync` checks what was set, and throws `InvalidOperationException` saying what to set, with the
  `OptionsValidationException` inside. Nothing has started then, so it can be called again. It then runs the starting,
  start and started phases. A start that fails on the way stops what started and rethrows. A component starts once.
- `StopAsync` runs the stopping, stop and stopped phases within `StopTimeout` (30 seconds, the generic host's default),
  running every phase even when one fails.
- `RunAsync` starts, waits for its token or a stop, and stops, as `WebApplication.RunAsync` does.
- `DisposeAsync` stops a running component first, so `await using` drains it, and logs a failure to stop. `Dispose` is
  the synchronous form for a container disposed synchronously, and closes at once. Both follow the dispose pattern.
- A component is not a hosted service. An internal adapter, `ZabbixHostedComponent`, runs its phases in a host, which has
  already run `ValidateOnStart`.

### 4. One table of keys

`ZabbixItemHandlers` becomes `ZabbixItemDictionary`, and an agent that answers keys owns one as `Items`, shared by all of
its destinations.

- It behaves as a dictionary does: `Add` refuses a name held, and the indexer replaces.
- When the agent starts, it adds the four keys every agent answers and, unless `BundledItems` is off, the bundled keys,
  under every name left free. A key you added first answers instead of Zabbix's.
- `Add` takes an answer giving `string`, `long` or `double`, each also asynchronous, or a `ZabbixItemCallback`. Numbers are
  formatted for the invariant culture, so a handler no longer does it.
- `Add(ZabbixItemProvider)` adds a provider's keys under the same rule, all or none, and refuses with `ArgumentException`.
  A provider added as an instance stays the caller's.
- `Register` runs a library's registration or a plugin's entry for an agent with no host, through
  `IZabbixItemRegistry`. It is the one place a small container is built, because those registrations are written against
  `IServiceCollection`, and it goes with the agent.
- `UseHandlers` is removed. To add a key while an agent runs, call `Items.Add`, on the agent the application constructed
  or resolved. An agent's table is its own, so two agents cannot overwrite each other's key rules, and bundled keys
  cannot outrank a provider: the two defects the review before this ADR found.

The precedence levels of ADR 0008 §3 collapse into that one rule. In a registration, providers are added first and each
`AddItem` then replaces, so the application's own answer wins whichever order they were registered in.

### 5. The names

| Was | Is |
| --- | --- |
| `ZabbixSender`, `ZabbixGetter`, `ZabbixServerWire` | `ZabbixWireClient`; the three stay internal behind it |
| `ZabbixActiveAgentHost` | `ZabbixActiveAgent`; one destination's loop is internal, `ZabbixActiveAgentDestination` |
| `ZabbixPassiveAgentHost` | `ZabbixPassiveAgent` |
| `ZabbixPassiveAgent` (the request handler) | `ZabbixPassiveCheckResponder` |
| `ZabbixAgentHost`, `AddZabbixAgent`, `ZabbixAgentOptions`, `IZabbixAgentBuilder` | `ZabbixPushAgent`, `AddZabbixPushAgent`, `ZabbixPushAgentOptions`, `IZabbixPushAgentBuilder` |
| `ZabbixAgent<T>` (the pump) | internal, `ZabbixSourcePump<T>` |
| `ZabbixProxyHost`, `AddZabbixProxy` | `ZabbixActiveProxy`, `AddZabbixActiveProxy` |
| `ZabbixPassiveProxyHost` | `ZabbixPassiveProxy` |
| `AddHandler` on a builder | `AddItem` |
| `AddZabbixDocument`, `AddZabbixAgentDocument`, `AddZabbixProxyDocument` | `AddZabbixClients`, `AddZabbixAgents`, `AddZabbixProxies` |
| `ZabbixDocument` | `ZabbixConfigurationSections` |
| the `Agents` configuration section | `PushAgents` |
| `ZabbixItemHandler`, `ZabbixRequestHandler`, `ZabbixDeliveryHandler<T>`, `ZabbixProxyTaskHandler`, `ZabbixPassiveProxyConfigHandler` | the same with `Callback` |
| `ZabbixExchange`, `ZabbixNotification`, `ZabbixConversation`, `ZabbixPeerResolver`, `ZabbixProxyDataProvider`, `ZabbixProxyStatsProvider` | `ZabbixExchangeCallback`, `ZabbixNotificationCallback`, `ZabbixConversationCallback`, `ZabbixPeerResolverCallback`, `ZabbixProxyDataCallback`, `ZabbixProxyStatsCallback` |

`Destinations` on an agent becomes `Statuses`, which names what it holds, and holds the same list between starts.

### 6. Two clients, one per protocol

Zabbix speaks two protocols, and the SDK has a client for each. The difference between them is the first thing a reader must
know, so the names say it:

| Client | Protocol | Reaches |
| --- | --- | --- |
| `ZabbixClient` | JSON-RPC over HTTP | The frontend |
| `ZabbixWireClient` | The Zabbix protocol: framed messages over TCP | A server, a proxy or an agent |

```csharp
using var wire = new ZabbixWireClient("zbx-server-01");
await wire.SendAsync([new ZabbixValue("web-01", "app.requests", 42L, DateTimeOffset.UtcNow)]);
JsonElement status = await wire.GetStatusAsync();
ZabbixAgentAnswer uname = await wire.GetAgentItemAsync("web-01", "system.uname");
```

`ZabbixWireClient` is one aggregate component, the digest's starting point for a feature area. It belongs to the server you
give it, as an `HttpClient` belongs to its `BaseAddress`, and a read from an agent names the agent, as `zabbix_get` does and as
an `HttpClient` request can name another host. So the same client reads from many agents, and one that only reads from agents
needs no server.

It follows §2's Create-Set-Call: `()` to set everything through `Options`, `(server)` and `(server, sessionId)` as shortcuts,
`(options)`, and `(options, loggers, time)`. `Options` is read at the first request, which is where options that cannot be
used, a certificate file that cannot be read among them, throw `InvalidOperationException`, as does a request to the server
when none is set. TAP lets a usage error throw at the call, and these are one, so the forwarding methods stay plain. A `server` passed to a constructor
is an argument, so it is checked there. `(exchange)` and `(exchange, options, loggers, time)` run it over a transport the
caller owns, as `new HttpClient(handler)` does.

Both clients have virtual members, so an application can fake them in its tests. A call that can be cancelled is an overload
pair, `SendAsync(values)` and `SendAsync(values, cancellationToken)`, because they already overload on the values' type and a
second optional parameter would break the API analyzer's rule; only the longest is virtual. Where a method has a single form,
`CancellationToken cancellationToken = default` stands: the Framework Design Guidelines predate it, but `IHost.StartAsync`,
`HubConnection` and the Azure SDK all use it.

The classes that build each protocol's messages, `ZabbixSender`, `ZabbixGetter` and `ZabbixServerWire`, stay internal. The
client's package stays `Community.Zabbix.Wire`, which names the protocol it speaks.

### 7. What a reader names is in the root namespace

The clients, agents, proxies, their options and answers, item providers and item definitions move to `Community.Zabbix`,
so one `using` serves a program. `Community.Zabbix.Protocol`, `Hosting`, `Listening` and `Storage` keep what a reader
composes with on purpose.

### 8. The modules are built the same way

A PowerShell session constructs the SDK's class with the module's logging, clock and meters, and its scriptblocks go
into `Items`, so the modules program against the SDK as an application does.

## Rejected

- **Keeping `ZabbixRunner` beside the classes.** Two ways to do one thing, and the reader still meets the word.
- **Renaming the runner `ZabbixAgent`.** A proxy and three kinds of agent would be one type, which is the reason the
  reader could not find them.
- **Reading options at construction.** It catches a bad option one line sooner, and it breaks Create-Set-Call: set-up
  would depend on order, and a default constructor could not exist.
- **`OptionsValidationException` from `StartAsync`.** It is the type `ValidateOnStart` throws in a host, where nobody
  called a method with bad state. A caller who constructed the class has an object in the wrong state, which the
  guidelines name.
- **Built-in keys in `Items` from construction.** It needs an exception to the dictionary's rule, so that `Add` could
  replace them, and it reads `BundledItems` before the caller set it.
- **Implementing `IHostedService` on the classes.** None of the SDKs read does, and it would put host phases on every
  reader's completion list.
- **Sealing the classes.** The guidelines ask for a reason, and none applies. Their members stay non-virtual, so a derived
  class adds without being able to break them.
- **Abstract `ZabbixAgent` and `ZabbixProxy` over the families.** They would add nothing beyond `ZabbixComponent`, and the
  push agent shares no keys with the others. They can be added later without a break.
- **A public client per peer: `ZabbixSenderClient`, `ZabbixServerClient`, `ZabbixAgentClient`.** Azure's guidelines ask for
  few clients, and split only for use cases unlikely to be used together, while sending values and asking a server for its
  status go to the same server over the same protocol. It also broke the rule against similar names: `ZabbixServerClient` read
  as a sibling of `ZabbixClient`, when the one difference a reader needs, the protocol, was nowhere in the name.
- **`ZabbixServerWireClient` and a `Community.Zabbix.Wire` namespace.** The names would say the protocol, but three clients
  remain to choose from, and a namespace is one more thing to learn before the first call.
- **`ZabbixProtocolClient`.** JSON-RPC is a protocol too, so the name does not separate the two clients. `Wire`, the package's
  own word, does.
- **A second client for agents, `ZabbixAgentClient`.** An agent is one more peer of the same protocol. It would keep a similar
  name to the `Agent` classes, which do the opposite, and `HttpClient` shows a client need not be bound to one peer.
- **Moving the passive agent out of `Community.Zabbix.Proxy` here.** It needs the listener, so it needs its own decision
  about Kestrel's closure. Left for a later change.

## Consequences

- It ships in 1.0.0, the first release, so these breaks cost no one a migration.
- Tests: `ZabbixComponent`'s lifecycle replaces the runner's, and parity tests compare a class constructed directly with
  the same class in a host.
- Documentation: the guides lead with the constructor. A later change restructures them.
- `Wire` logs all carry the category `Community.Zabbix.ZabbixWireClient`, since the classes beneath it are internal.
