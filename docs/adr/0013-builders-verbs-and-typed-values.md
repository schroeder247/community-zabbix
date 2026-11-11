<!--
This Source Code Form is subject to the terms of the Mozilla Public
License, v. 2.0. Copyright (c) 2026 schroeder247. SPDX-License-Identifier: MPL-2.0
-->
# 0013. One builder shape, one meaning per verb, and types for what was text

- **Status:** Accepted
- **Date:** 2026-11-10
- **Amends:** [0012](0012-typed-bcl-fluent-api.md) §1 (the typed key's name), §3 (writes take a lambda, as reads do) and §5 (every component has a builder); [0010](0010-agent-proxy-client-classes.md) §2 (the positional constructors go); [0006](0006-protocol-split.md) §2 (`Zabbix.Wire`'s dependencies); [0008](0008-item-providers-and-plugins.md) (the items builder, and items keyed by the agent's name).

## Context

0012 typed the API and gave the two clients builders. The maintainer then asked for the whole SDK to be fluent and built with builders, shaped as .NET's own libraries are, with lambdas, delegates and generics, and with code that reads well. An inventory of the public surface found where it falls short:

- **Only the clients have builders.** Agents, proxies and the listener are constructed and then changed through `Options`. Positional constructors order their arguments differently: `ZabbixActiveAgent(serverActive, hostname)`, but `ZabbixPassiveAgent(hostname, peers)`.
- **Two copies of every verb.** An agent's instance methods and its DI builder's extensions were written separately and drifted. The typed async `AddPoll<T>` exists only on the instance, and `AddSource` takes a value on one and a factory on the other.
- **Chains break.** `AddProvider`, `Items.Register` and `AddPlugins` return `void`. `AddItems<TProvider>()` returns the base interface, so each builder adds its own copy.
- **One verb, two meanings.** `SendTo` adds a server on an agent and replaces it on a proxy, and `AddStore` replaces. `WithServer` and `SendTo` name one thing, and `WithToken` and `WithSession` take one token.
- **Text where .NET has types.** Allowed peers are `"10.0.0.0/8,::1"`, an item's delay and an interface's port are text, and output and sort fields are strings that any object's query accepts.
- **Writes nest `new`.** A host's interface or tag is a `new` inside the request's `new`.
- **A name clash.** `ZabbixItem<T>`, the typed key, and `ZabbixItem`, the API's item record, share a name.
- **Cancellation varies.** Some methods take a token through an overload, some through a default, and `GetHistoryAsync<T>(item, window)` through neither.

## What the sources say

| Source | Rule | What it decides |
| --- | --- | --- |
| [IHttpClientFactory](https://learn.microsoft.com/dotnet/core/extensions/httpclient-factory) | `AddHttpClient` returns `IHttpClientBuilder`, which exposes only `Name` and `Services`; an extension registers through `builder.Services` and returns the builder | The builder interface, and verbs written as extensions |
| [Generic host](https://learn.microsoft.com/dotnet/core/extensions/generic-host) | `Host.CreateApplicationBuilder()` gives a builder with `Services`, and `Build()` runs once | `CreateBuilder()`, and a standalone builder over its own `Services` |
| [Options for library authors](https://learn.microsoft.com/dotnet/core/extensions/options-library-authors) | `Add{Service}` on `IServiceCollection`; options are classes set by `Action<T>` or bound from configuration, and validated at start | `Configure`, `Bind`, and validation in `Build()` |
| [Azure SDK guidelines](https://azure.github.io/azure-sdk/dotnet_introduction.html) | Clients are immutable, and configuration changed after a client is built doesn't change it; required values go in the constructor and optional ones are settable; methods take an optional `CancellationToken` | Fixed once built; the shape of a request; cancellation |
| [Azure client registration](https://learn.microsoft.com/dotnet/azure/sdk/dependency-injection) | `WithName` and `WithCredential` set one attribute of a registration; `UseCredential` picks the one credential | `With…` and `Use…` |
| [`IHttpClientBuilder`](https://learn.microsoft.com/dotnet/api/microsoft.extensions.dependencyinjection.ihttpclientbuilder) | `AddHttpMessageHandler` appends, `UseSocketsHttpHandler` picks the handler, and `ConfigureHttpClient` adds a delegate | `Add…`, `Use…` and `Configure` |
| [`HttpRequestOptionsKey<TValue>`](https://learn.microsoft.com/dotnet/api/system.net.http.httprequestoptionskey-1) | A typed key over a string name | `ZabbixItemKey<T>` |
| [`IPNetwork`](https://learn.microsoft.com/dotnet/api/system.net.ipnetwork) | The BCL's CIDR block | Allowed peers |
| [`required` members](https://learn.microsoft.com/dotnet/csharp/language-reference/keywords/required) | The compiler reports a required member that an initializer leaves out | A request's required fields |

No source has a rule on a lambda for a request's body, or on a builder for each request.

## Decision

### 1. One builder shape for every component

`ZabbixClient`, `ZabbixWireClient`, the three agents, the two proxies and `ZabbixListener` each have the same ways in, and share one set of verbs:

- **`CreateBuilder(...)`**, a static method that takes what identifies the component and returns its builder. `Build()` returns the component.
- **`services.AddZabbix…(name)`**, which returns the same kind of builder over the application's container.
- **A constructor**, only where one input is enough: `new ZabbixClient(endpoint, token)` and `new ZabbixWireClient(server)`.

A builder interface has two members, as `IHttpClientBuilder` does:

```csharp
public interface IZabbixPushAgentBuilder
{
    string Name { get; }
    IServiceCollection Services { get; }
}
```

The standalone builder implements the interface over a container of its own. `Build()` builds that container and resolves the component through the registration that `AddZabbix…` makes, and the component owns the container and disposes it. This is [0007](0007-container-free-runners.md)'s private container, as `LoggerFactory.Create`, OpenTelemetry's standalone builders and Quartz have one. It doesn't bring back what [0010](0010-agent-proxy-client-classes.md) removed it for: the builder returns the component itself, which its own constructor builds from the options the builder set, and the class still works without a builder.

Each verb is written once, as an extension of the interface with a generic receiver, so the standalone builder keeps its own type through a chain and still has `Build()` at the end:

```csharp
extension<TBuilder>(TBuilder builder) where TBuilder : IZabbixPushAgentBuilder
{
    public TBuilder AddServer(DnsEndPoint server) { /* register through builder.Services */ return builder; }
}
```

- `Build()` validates the options with the validator that DI uses, and runs once, as `HostApplicationBuilder.Build()` does.
- A built component's settings don't change. What a component exists to change at run time, such as an agent's items, keeps its own members.
- The name is the component's identity: an agent's host name, or a proxy's name. `Configure` can set a different one.
- A component from the container, or from a factory by its name, is shared: the container owns it and disposes it, and a caller's `Dispose` leaves it running, as disposing an `HttpClient` from `IHttpClientFactory` leaves its handler.
- The classes keep 0010's constructors `()`, `(options)` and `(options, loggers, time, meters)`, for an application that sets `Options` itself. The positional ones go, and so do the instance `Add…` methods: the builders are the fluent path, so there's one copy of each verb.

### 2. One meaning per verb

| Verb | Means | Examples |
| --- | --- | --- |
| `Add…` | One more, never a replacement | `AddServer` (an agent's destinations), `AddPoll`, `AddItem`, `AddItems<T>`, `AddSource`, `AddMeter`, `AddTrapper`, `AddPlugins`, `AddHealthCheck` |
| `Use…` | The one component of its kind; a later call replaces an earlier one | `UseStore`, `UseMemoryStore`, `UseEntityFrameworkStore`, `UseSocketTransport`, `UseLogging`, `UseTimeProvider` |
| `With…` | One attribute of what's built | `WithServer` (the one server of the wire client or an active proxy), `WithEndpoint`, `WithToken`, `WithCredential`, `WithPsk`, `WithCertificate`, `WithTimeout`, `WithCompression`, `WithVersion` |
| `Allow…` | Who may connect | `AllowPeer`, `AllowPsk`, `AllowUnencryptedCredentials` |
| `On…` | What answers a request the component receives | `OnTask`, `OnConfig`, `OnStats`, `OnRequest` |
| `Configure` | Any option, through a delegate, applied in order | `Configure(o => o.Send.BatchSize = 100)` |
| `Bind` | Options from configuration | `Bind(configuration.GetSection("Zabbix:Agent"))` |

| Was | Is |
| --- | --- |
| `WithServer(...)`, `SendTo(...)` | `AddServer(...)` on an agent, which sends to every server it has; `WithServer(...)` on the wire client and an active proxy, which have one. Several nodes in one call are one HA cluster either way |
| `AddStore(...)` | `UseStore(...)`, `UseMemoryStore()` or `UseEntityFrameworkStore(db => ...)` |
| `WithSession(token)` | `WithToken(token)` |
| `WithLogging(...)`, `WithTimeProvider(...)` | `UseLogging(...)`, `UseTimeProvider(...)` |
| `proxy.TaskHandler = ...` and the other handler properties | `OnTask(...)`, `OnConfig(...)` |
| `AddPoll(interval, key, read)` | `AddPoll(key, interval, read)`: the key first, as in `AddItem`, and the delegate last |
| `AddProvider`, `Items.Register` and `AddPlugins`, returning `void` | Each returns its builder |

### 3. Delegates answer items, and generics carry the types

A typed key is `ZabbixItemKey<T>`, beside the untyped `ZabbixItemKey` as `Task<T>` stands beside `Task`, and the catalogue of Zabbix's own keys is `ZabbixItemKeys`. A delegate answers an item, its shape says what it needs, and the key's `T` fixes the type of the value it returns:

| Delegate | For |
| --- | --- |
| `Func<T>` | A value at hand: `() => queue.Count` |
| `Func<ZabbixItemKey, T>` | A key with parameters: `key => orders.Count(key[0])` |
| `Func<ZabbixItemKey, CancellationToken, ValueTask<T>>` | A read that waits on I/O |
| `Func<TService, T>` | A service from the container: `(IOrderQueue queue) => queue.Count` |
| `Func<TService, CancellationToken, ValueTask<T>>` | A service, and a read that waits on I/O |

A standalone builder resolves a service from its own `Services`, where the application can register one, as with `AddZabbix…`.

`AddPoll` takes the same shapes without the key, since a poll reads the key it was given. The API's item key is a `ZabbixItemKey` too, and an item created from a typed key takes its value type from `T`: `zabbix.Items.CreateAsync(hostId, Pending, ZabbixItemType.Trapper, i => i.Name = "Pending")`. The active and the passive agent's builders are both `IZabbixItemsBuilder`, the plugin contract, so each `AddItem` is written once for both. Items belong to the agent's name: an active and a passive agent of one name answer the same items, as one Zabbix agent answers both kinds of check. More than one service, or state kept between reads, is a `ZabbixItemProvider` class, added with `AddItems<TProvider>()`.

### 4. Writes take a lambda, as reads do

```csharp
ulong id = await zabbix.Hosts.CreateAsync("web-02", [linux], h =>
{
    h.Interfaces = [ZabbixInterface.Agent(IPAddress.Parse("10.0.0.2"))];
    h.Tags = [("env", "prod")];
});

await zabbix.Hosts["web-02"].UpdateAsync(h => h.Status = ZabbixHostStatus.Disabled);
```

- A create with a short overload also takes a lambda for the optional fields. A node's `UpdateAsync` takes one, and fills in the id.
- A request's required fields are `required`, and its constructor takes them. Its optional fields have setters, as the Azure guidelines have them, so `CreateAsync(new("web-02", [linux]) { Tags = [("env", "prod")] })` works too.
- Nested values have factories and conversions: `ZabbixInterface.Agent(address)`, a tag from a `(string, string)` tuple, and a group or template from its id.
- A group's node reaches its hosts by indexer, `zabbix.HostGroups["Linux"].Hosts["web-01"]`, and a host's node its items, `host.Items["system.uptime"]`. A typed read takes its key, `host.GetHistoryAsync(ZabbixItemKeys.Cpu.Load(), TimeSpan.FromHours(1))`, because C# has no generic indexers. A host reached by id or by name is the same node type.
- `Get…` throws when there's nothing to return and `Find…` returns `null`, everywhere.
- A get's parameters leave out what changes the shape of the reply (`countOutput`, `preservekeys`): `CountAsync` counts.

### 5. Types for what was text

| Was | Is |
| --- | --- |
| A server as text: `WithServer("zbx-1:10051")` | `AddServer(DnsEndPoint)`, or `AddServer("zbx-1:10051")` in Zabbix's spelling |
| Allowed peers as text: `"10.0.0.0/8,::1"` | `AllowPeer(IPNetwork)`, `AllowPeer(IPAddress)`, or `AllowPeer("monitor.example")` for a host name |
| A pre-shared key as hex text | `WithPsk(identity, key)`, with the key as bytes |
| An item's trapper hosts as text | A list of `ZabbixPeer`, which converts from an `IPAddress` or an `IPNetwork`; `ZabbixPeer.Parse` takes a host name or a macro |
| An item's delay as text | `ZabbixInterval`, which converts from a `TimeSpan`; `ZabbixInterval.Parse` takes a macro or a flexible interval |
| An interface's port as text | `ZabbixPort`, which converts from an `int`; `ZabbixPort.Parse` takes a macro |
| Output and sort fields as strings | `ZabbixField<TRecord>`, so a host's field doesn't compile in an item's query; a sort is one list of fields with their directions |
| History, trends and timeouts as text | `ZabbixDuration`, which converts from a `TimeSpan` |
| A reply as `JsonElement` where its schema is known | A model type |
| An API method named by a string: `WithTimeout("configuration.import", ...)` | A `ZabbixApiMethod` value |
| A certificate as file paths only | An `X509Certificate2`, or the paths |

- Options keep the text that configuration spells, because the configuration binder fills them from `appsettings.json` and can't make a `DnsEndPoint` or an `IPNetwork`. The verbs take the .NET types, and write that text.
- A value in a request that Zabbix also accepts as a macro converts implicitly from its .NET type, and its `Parse` takes Zabbix's spelling.

### 6. Every async member can be cancelled

A `CancellationToken` is the last parameter of every async member, and it's optional on every overload (`cancellationToken = default`), as the Azure guidelines and Microsoft.Extensions.AI have it. RS0026 is suppressed for these overloads: an extra overload without the token for each shape doubled the surface, six `GetAsync` overloads where three say the same.

### 7. One spelling per concept

- **Names:** `HostName`, `UserName`, `LogOutAsync`, and member names without abbreviations (`DiscoveryRuleId`, not `DruleId`).
- **Registrations:** `AddZabbixClient` and `IZabbixClientBuilder`, beside `AddZabbixWireClient` and `IZabbixWireClientBuilder`.
- **Types:** a Zabbix version is a `System.Version`; a package uses the API's enums rather than copies of them; a reason is an enum, not a string constant.
- **Parameters:** public constructors take `(options, loggers, time, meters)`, with an `ILoggerFactory`; two or more booleans are an enum.
- **Interfaces:** no public interface is empty: a builder capability carries `Name` and `Services`.

### The SDK, end to end

```csharp
static readonly ZabbixItemKey<long> Pending = new("orders.pending");
static readonly ZabbixItemKey<long> ByStatus = new("orders.count");

// The API
await using var zabbix = new ZabbixClient(new Uri("https://zabbix.example/"), token);

var hosts = await zabbix.Hosts.GetAsync(q =>
{
    q.Filter.Status = [ZabbixHostStatus.Enabled];
    q.Search.VisibleName = "web";
    q.Output = [ZabbixHostFields.HostId, ZabbixHostFields.VisibleName];
});

await foreach (var (time, load) in zabbix.Hosts["web-01"].GetHistoryAsync(ZabbixItemKeys.Cpu.Load(), TimeSpan.FromHours(1)))
    Console.WriteLine($"{time:t}  {load:F2}");

// Sending a value
await using var wire = new ZabbixWireClient("zbx-server-01");
await wire.SendAsync("web-01", Pending, 42);

// An agent on its own
await using var agent = ZabbixPushAgent.CreateBuilder("web-01")
    .AddServer("zbx-server-01")
    .AddPoll(Pending, TimeSpan.FromMinutes(1), () => queue.Count)
    .AddMeter("MyApp.Orders")
    .Build();
await agent.StartAsync();

// An agent and a proxy in a host
var builder = Host.CreateApplicationBuilder(args);

builder.Services.AddZabbixActiveAgent("web-01")
    .AddServer("zbx-server-01")
    .AddItem(Pending, (IOrderQueue queue) => queue.Count)
    .AddItem(ByStatus, key => orders.Count(key[0]))
    .AddItems<DiskItems>()
    .AddHealthCheck();

builder.Services.AddZabbixPassiveProxy("proxy-dmz")
    .WithVersion("7.4")
    .AllowPeer(IPNetwork.Parse("10.0.0.0/8"))
    .WithPsk("proxy-dmz", psk)
    .UseEntityFrameworkStore(db => db.UseSqlite("Data Source=proxy.db"))
    .OnTask((task, ct) => tasks.RunAsync(task, ct));

await builder.Build().RunAsync();
```

## Limits

- **One service per delegate.** Two or more is a `ZabbixItemProvider` class, whose constructor takes them.
- **Typed reads are methods.** C# has no generic indexers, so `zabbix.Hosts["web-01"]` is an indexer and the typed read beside it is a method.
- **`AddItems<TProvider>()` has a copy per builder.** C# doesn't infer an extension block's type argument when the caller names the method's own, so `builder.AddItems<DiskItems>()` needs a non-generic receiver for each builder type.
- **`Build()` runs once.** A second call throws `InvalidOperationException`. A second component needs a second builder.
- **A standalone component carries a container.** Disposing the component disposes it.

## Rejected

- **A `Delegate` overload bound at compile time, as minimal APIs bind a route's handler.** It would bind a key's parameters and any number of services by name and type, but it needs a source generator with interceptors. The five shapes above cover the main cases without one. Revisit it when callers need more.
- **Separate standalone builders, and instance verbs beside the builders.** Two implementations of each verb drifted once. The standalone builder runs the container path on a container of its own, so there's only one.
- **Positional constructors for agents and proxies.** Siblings ordered their arguments differently, and `CreateBuilder` names what it takes.
- **A fluent builder for each request, and `IQueryable`.** 0012's reasons stand: a second copy of every record's fields, and a query shape the server can't honour.

## Consequences

- These changes break every package before 1.0.0, the first release, so no caller has to migrate.
- Each step moves its tests, documents, samples and the modules with it, in this order: the typed key's name, the wire client, the agents, the proxies and the listener, the API client, and the requests.
- `Zabbix.Wire` takes the DI and configuration-binding packages its standalone builder composes, as `Zabbix.Sdk` already does for its client.
- The modules keep their commands.
