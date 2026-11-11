<!--
This Source Code Form is subject to the terms of the Mozilla Public
License, v. 2.0. Copyright (c) 2026 schroeder247. SPDX-License-Identifier: MPL-2.0
-->
# 0007. Every entry point works without a container

- **Status:** Superseded in part by [0010](0010-agent-proxy-client-classes.md) (§3 to §5); §1 and §2 stand
- **Date:** 2026-11-01

## Context

An application should be able to query a Zabbix server, send it a value or run an agent without composing a
dependency-injection container.

**The clients** were constructed directly, but each asked for more than the task needs:

| To | Before | The friction |
| --- | --- | --- |
| Query the API | `new ZabbixClient(new Uri("https://zabbix.example/api_jsonrpc.php"), new ZabbixApiTokenCredential(token))` | the exact `api_jsonrpc.php` endpoint (the PowerShell module accepted the frontend URL through an internal helper, the SDK did not); a credential type to know by name; no way to give the client a logger |
| Send a value | `new ZabbixSender("zbx-server-01")` | a host only: `zbx-server-01:10051`, as Zabbix's own configuration spells a node, was refused |
| Read an agent item | `new ZabbixGetter("web-01")` | the same |
| Ask the server's status | `new ZabbixServerWire(new ZabbixServerNode("zbx-server-01"))` | a node object for one server name |

Once a client exists, using it is short: `client.Api.Hosts.GetAsync()` takes no required argument, and
`client.Host("web-01").GetLatestAsync()` is one call. The README's quick start nonetheless opened with a
`ServiceCollection`, which teaches the container where none is needed.

**The hosted components**, the agent (`AddZabbixAgent`), the active agent, the passive agent, the trapper and the
active and passive proxy, are registered as `IHostedService`s beside options checked by `ValidateOnStart`, so they
run only inside something that validates the options, starts them and stops them. Their constructors take what a
container supplies (`ILoggerFactory`, `TimeProvider`, item handlers, stores, Kestrel's `IConnectionListenerFactory`).
The PowerShell modules solved this privately: `ZabbixSessionHost` ran the SDK's own registration in a container of its
own, with the generic host's lifecycle and without `Microsoft.Extensions.Hosting`, whose console lifetime would take
the shell's Ctrl+C.

### How other libraries offer this

| Library | Without a container | With a container | Relation |
| --- | --- | --- | --- |
| Azure SDK | `new SecretClient(uri, credential, options)`; the guidelines require "a minimal constructor that takes only the parameters required to connect" | `AddAzureClients` in the separate `Microsoft.Extensions.Azure` | the container path calls the constructors |
| Confluent.Kafka | `new ProducerBuilder<,>(config).Build()` | community and Aspire packages | the container path wraps the builders |
| EF Core | `new DbContext(options)` | `AddDbContext` | the same options either way |
| Microsoft.Extensions.Logging | `LoggerFactory.Create(builder => …)` | `services.AddLogging(…)` | `Create` builds a private `ServiceCollection`, calls `AddLogging(configure)` and returns a factory whose `Dispose` disposes that provider |
| OpenTelemetry .NET | `Sdk.CreateTracerProviderBuilder()…Build()`, disposed at shutdown | `services.AddOpenTelemetry()` | the standalone builder creates its own `ServiceCollection` with the same registrations and owns the provider it builds |
| Quartz.NET 4 | `QuartzSchedulerBuilder.Create(q => …).Build()` returns an `IAsyncDisposable` factory | `AddQuartz(q => …)` + `AddQuartzHostedService` | one `IQuartzBuilder` for both; disposing the factory shuts the scheduler down, then its container |
| Hangfire | `new BackgroundJobServer()`, `Dispose` for a graceful stop | `AddHangfireServer()` | a separate standalone class |
| MassTransit | `Bus.Factory.CreateUsing…`, `StartAsync`/`StopAsync` | `AddMassTransit` | a separate standalone bus |
| NServiceBus | `Endpoint.Start(configuration)` | `AddNServiceBusEndpoint` | self-hosting deprecated in version 11: it duplicated the hosting, logging and DI concerns the generic host owns |
| Serilog | the static `Log.Logger` | `AddSerilog` | global state |

Two patterns cover the SDK. A client is a constructor that asks only for what connecting needs (Azure, Kafka, EF
Core). A long-running component takes the same configuration as its container path and runs it in a private container
the returned object owns and disposes (Logging, OpenTelemetry, Quartz), so the two paths cannot drift apart, the
failure NServiceBus's deprecation records. Microsoft's DI guidance rules out a static facade: "Avoid stateful, static
classes and members."

## Decision

### 1. The principle

Every entry point has a container-free form, and the documentation shows it first. The container registration is the
integration layer for applications that have a host; it calls the same code, never a parallel implementation.

### 2. The clients ask only for what connecting needs

```csharp
// Query the API: the frontend URL and a token.
await using var client = new ZabbixClient("https://zabbix.example/", ZabbixCredential.FromApiToken(token));
IReadOnlyList<ZabbixHost> hosts = await client.Api.Hosts.GetAsync();

// Send a value, read an agent item, ask the server's status: a server name, or host:port.
using var sender = new ZabbixSender("zbx-server-01");
await sender.SendAsync([new ZabbixValue("web-01", "app.requests", "42", DateTimeOffset.UtcNow)], cancellationToken);
using var getter = new ZabbixGetter("web-01:10050");
```

- **The frontend URL is enough.** An address whose path names no `.php` script is the frontend's, and the API is its
  `api_jsonrpc.php`; an address that names a script is used as given. The rule is `ZabbixOptions.GetApiEndpoint`, and
  it applies wherever the SDK reads an endpoint: the constructors, `AddZabbix` and a bound `appsettings.json`.
  `Connect-ZbxServer` calls the same rule. User info written into the URL is dropped, as `ZabbixOptions.Endpoint`
  documents; the module refuses it at the prompt, where a typed password would stay in the shell's history.
- **Credentials have factories:** `ZabbixCredential.FromApiToken(token)` and
  `ZabbixCredential.FromLogin(username, password)` return the existing credential types, beside the existing
  `ZabbixCredential.Anonymous`.
- **A wire client reads one node from text:** `ZabbixSender(string)`, `ZabbixGetter(string)` and
  `ZabbixServerWire(string)` read `host`, `host:port`, an IPv6 address or `[ipv6]:port`, as `ZabbixServerNode.Parse`
  does, each on its peer's default port when the text names none (10051 for a server, 10050 for an agent). The agent and
  proxy builders' `SendTo(string)` read the same. An HA cluster stays the node list's constructor.
- **Logging and the clock without a container:** `ZabbixClient(Uri, ZabbixCredential, ZabbixOptions, ILoggerFactory?,
  TimeProvider?)` puts them in the client's private container; the caller keeps ownership. The wire clients' longest
  constructors already take a logger and a clock. Without one, a client logs nowhere; traces and metrics still flow,
  since activity sources and meters are process-wide.
- **String overloads** for the URL follow the Framework Design Guidelines ("CONSIDER providing string-based overloads
  for most commonly used members with `System.Uri` parameters"): `ZabbixClient(string, ZabbixCredential)` and
  `ZabbixClient(string, ZabbixCredential, ZabbixOptions)`. The `Uri` overloads stay the primary ones.

### 3. The hosted components run in a `ZabbixRunner`

`ZabbixRunner`, in `Zabbix.Agent`, is a sealed `IAsyncDisposable` that runs the SDK's own registration in a container
of its own:

```csharp
await using ZabbixRunner agent = await ZabbixRunner.StartAgentAsync("edge-01", a => a
    .AddPoll(TimeSpan.FromSeconds(30), ct => Cpu.SampleAsync(ct))
    .SendTo("zbx-server-01"));

await using ZabbixRunner active = await ZabbixRunner.StartActiveAgentAsync("web-01", a => a
    .Configure(o => o.Hostname = "web-01")
    .SendTo("zbx-server-01")
    .AddHandler("app.queue.depth", _ => QueueDepth()));

// Any combination of the hosting packages' registrations:
await using ZabbixRunner both = ZabbixRunner.Create(services => services
    .AddZabbixAgent("edge-01", a => a.AddTrapper(10051, o => o.AllowedPeers.Add("10.0.0.0/8")).SendTo("zbx-server-01")));
await both.StartAsync();
```

| Member | What it does |
| --- | --- |
| `static ZabbixRunner Create(Action<IServiceCollection> register, ZabbixRunnerOptions? options = null)` | the general form: composes and builds the container, unstarted |
| `static Task<ZabbixRunner> StartAgentAsync(string name, Action<IZabbixAgentBuilder> configure, ZabbixRunnerOptions? options = null, CancellationToken cancellationToken = default)` | `AddZabbixAgent(name, configure)`, started |
| `static Task<ZabbixRunner> StartActiveAgentAsync(string name, Action<IZabbixActiveAgentBuilder> configure, …)` | `AddZabbixActiveAgent(name, configure)`, started |
| `IServiceProvider Services` | what the registration put in the container (an agent's statuses, its health check, a listener) |
| `Task StartAsync(CancellationToken cancellationToken = default)` | validates and starts, once |
| `Task StopAsync(CancellationToken cancellationToken = default)` | stops and drains, as the generic host does |
| `ValueTask DisposeAsync()` | stops within the stop timeout, then disposes the container; a failure to stop is logged, not thrown; a second call does nothing |

`ZabbixRunnerOptions` carries what a host would supply: `ILoggerFactory? Loggers`, `TimeProvider? Time` and `TimeSpan
StopTimeout` (30 seconds, the generic host's default). A typed starter whose start fails disposes the runner.

**The lifecycle is the generic host's.** The container is built with `ValidateOnBuild` and `ValidateScopes`; the
`ValidateOnStart` rules run first (`IStartupValidator`), so an invalid option throws `OptionsValidationException`
before anything starts; the hosted services start in registration order, `IHostedLifecycleService` phases included.
A start that fails stops what started, in reverse, and rethrows. Stopping runs the phases in reverse within the stop
timeout, asks every service to stop even when one fails, and throws the failures together; an agent drains its store
as it does in a host. The runner takes no process signals: the application owns its lifetime, which lets it embed in a
desktop app, a test or a PowerShell session. Its log events are 3300–3305 in the agent package's block.

**The receiving side** (`Zabbix.Proxy`) adds `StartPassiveAgentAsync`, `StartProxyAsync` and `StartPassiveProxyAsync`
as C# 14 static extension members of `ZabbixRunner` (`ZabbixRunnerProxyExtensions`), so they read as members of one
type when the package is referenced. Each registration brings the socket transport its listener binds through, so a
listener needs no Kestrel setup from the caller. A trapper is an agent's source, started with `StartAgentAsync`.

### 4. One implementation of each rule

The PowerShell module's `ZabbixSessionHost` runs on `ZabbixRunner`, keeping only its logging bridge and session
telemetry. The base module does not carry the agent package, so the agent and proxy modules hand it the runner as a
`ZabbixSessionRunner` (its container, start, stop and disposal). `Connect-ZbxServer` uses the SDK's frontend-URL rule.
Each rule then exists once, in the SDK, and the module's tests exercise it too.

### 5. The documentation leads with the container-free form

The README's quick start and each guide open with the constructor or the runner, and show the container
registration next, as the form for an application with a host. A guide on hosting agents, for example, starts with
`ZabbixRunner.StartAgentAsync` and then shows `AddZabbixAgent`.

### 6. The dependency policy holds

Nothing here adds a package. The runner uses `Microsoft.Extensions.Hosting.Abstractions` and
`Microsoft.Extensions.Options`, which `Zabbix.Agent` already takes, and not `Microsoft.Extensions.Hosting`
([ADR 0006](0006-protocol-split.md), §2).

### 7. The questions the proposal left open

1. **The name is `ZabbixRunner`.** It runs components; "host" is the generic host's word, and an `IHost` the runner
   is not (Rejected).
2. **The receiving side's starters are C# 14 static extension members.** The libraries target `net10.0` only, whose
   language is C# 14; the compiler also emits each as a plain static method of `ZabbixRunnerProxyExtensions`, which a
   caller on another language calls directly.
3. **The general form is public, as `ZabbixRunner.Create`.** It shows `IServiceCollection` only to a caller who
   composes registrations, and it is how a caller resolves something before the start.
4. **`ZabbixClient` has string URL overloads**, per the Framework Design Guidelines (§2).

## Rejected

- **A hand-written constructor with defaults for every host.** It re-implements the wiring each `Add*` call already
  does, and the two paths drift apart; NServiceBus deprecated its equivalent for that reason.
- **The generic host inside the runner** (`Host.CreateEmptyApplicationBuilder`). It adds `Microsoft.Extensions.Hosting`
  to the closure, and its console lifetime takes the process's Ctrl+C, wrong for an embedded component.
- **The runner as an `IHost`.** `IHost` is `IDisposable`, and a runner's synchronous `Dispose` would block on its
  asynchronous stop: a fourth place where async meets a synchronous contract, beyond the three the design register
  allows ("Async meets a synchronous contract at three places"). The hosting abstractions' `RunAsync` would also need an
  `IHostApplicationLifetime` nothing signals without a console lifetime.
- **A static `StartAsync(register, …)` beside the instance `StartAsync`.** Two overloads with optional parameters
  (RS0026), and `Create` with `StartAsync` says the same in two lines.
- **A static facade** (`Zabbix.Default`, or a process-wide agent as Serilog's `Log.Logger`). Global state, which
  Microsoft's DI guidance rules out, and it cannot hold two configurations in one process.
- **A separate type for the container-free case** (a lighter client beside `ZabbixClient`). Two types for one
  server; the constructors are the container-free form.
- **`ZabbixSender(params string[] nodes)`.** One string is one node, read as `ServerActive` reads an element; a
  cluster is the node list's constructor, and two ways to name one node would be one too many.
- **A separate package for the runners.** The types are small and belong beside the registrations they run.

## Consequences

- It ships in 1.0.0, the first release.
- Tests: the frontend-URL rule and the string overloads; the runner's lifecycle (validation before start, start order,
  rollback on a failed start, reverse stop, stop timeout, idempotent disposal); and parity, the same agent
  configuration through a host and through a runner delivering to a trapper, and a runner's passive agent answering a
  getter.
- Documentation: the README's quick start and every guide lead with the container-free form.
