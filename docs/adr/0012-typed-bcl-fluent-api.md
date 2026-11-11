<!--
This Source Code Form is subject to the terms of the Mozilla Public
License, v. 2.0. Copyright (c) 2026 schroeder247. SPDX-License-Identifier: MPL-2.0
-->
# 0012. The public API is typed, built on the BCL, and fluent

- **Status:** Accepted
- **Date:** 2026-11-05
- **Reverses:** [0006](0006-protocol-split.md) §3, for `AddMeter`. Its reason for removing the query provider stands in part (see Rejected).
- **Amends:** [0010](0010-agent-proxy-client-classes.md) §5, which named delegates with `Callback`.
- **Amended by:** [0013](0013-builders-verbs-and-typed-values.md), for §1 (`ZabbixItem<T>` is `ZabbixItemKey<T>`), §3 and §5.

## Context

Written out, the SDK's main scenarios read badly, and the maintainer called them ugly and unintuitive:

```csharp
await wire.SendAsync([new ZabbixValue("web-01", "app.requests", 42L, DateTimeOffset.UtcNow)]);

ZabbixValue? last = await client.Host("web-01").Item("app.requests").GetLastValueAsync();
long count = long.Parse(last!.Value);

await client.Api.Hosts.CreateAsync(new ZabbixHostCreateRequest
{
    TechnicalName = "web-01",
    Groups = [new ZabbixHostGroupRequest { GroupId = groupId }],
});
```

### Where it went wrong

- **Only the API was typed.** The API's records are generated from Zabbix's source. Item keys have no schema, so the protocol's shape, a text key and a text value, became the public shape, and nobody designed a typed layer over it.
- **The SDK threw types away.** Watchers and history reads know each item's value type, then drop it when they build a `ZabbixValue`. A guide told readers to call `double.TryParse(last.Value, ...)`.
- **The records mirrored the JSON.** Generating one record per JSON object is complete and correct, but the design started from the schema and not from the code a caller writes. 92 request records hold one property.
- **ADR 0006 deleted the better shapes.** It removed `AddMeter` for having "no consumer" before anything was released, so nothing could have had one.
- **ADR 0010 missed it.** It changed which classes you construct, not what you pass them. It also renamed delegates to end in `Callback` when the same guidelines say to use `Func` and `Action`.
- **The checks couldn't see it.** A conformance test checks shape, and a mistyped `"system.cpu.laod"` compiles.

## What the sources say

| Source | Rule | What it decides |
| --- | --- | --- |
| [Framework design digest](https://github.com/dotnet/runtime/blob/main/docs/coding-guidelines/framework-design-guidelines-digest.md) | "DO NOT require users of your APIs to instantiate multiple objects in main scenarios"; if sample code "gets longer than several lines, you need to redesign" | No `new` wrappers in main scenarios |
| [Creating metrics](https://learn.microsoft.com/dotnet/core/diagnostics/metrics-instrumentation) | `meter.CreateCounter<int>("hatco.store.hats_sold")`: a name is declared once, with its value type | `ZabbixItem<T>`, and `AddMeter` |
| [`HttpRequestOptionsKey<TValue>`](https://learn.microsoft.com/dotnet/api/system.net.http.httprequestoptionskey-1) | A typed key over a string name, in the BCL | The shape of `ZabbixItem<T>` |
| [Collecting metrics](https://learn.microsoft.com/dotnet/core/diagnostics/metrics-collection) | `MeterListener` observes a meter in-process; OpenTelemetry's builder is `.AddMeter("HatCo.HatStore")` | `AddMeter` |
| [Microsoft Graph SDK](https://learn.microsoft.com/graph/sdks/create-requests) | Microsoft's own generated client: `client.Me.Messages[id].GetAsync()`, and `GetAsync(config => config.QueryParameters.Select = [...])` | The generated API's shape |
| [Azure SDK guidelines](https://azure.github.io/azure-sdk/dotnet_introduction.html) | Options for complex methods, and "consider adding an overload with a parameter list representing just this subset" | Short overloads beside full requests |
| [SignalR client](https://learn.microsoft.com/aspnet/core/signalr/dotnet-client) | `new HubConnectionBuilder().WithUrl(...).WithAutomaticReconnect().Build()` | Builders for setup with many parts |
| [Events and callbacks](https://learn.microsoft.com/dotnet/standard/design-guidelines/events-and-callbacks) | "DO use the new `Func<...>`, `Action<...>`... instead of custom delegates" | No public delegates |
| [Exceptions and performance](https://learn.microsoft.com/dotnet/standard/design-guidelines/exceptions-and-performance) | Provide a throwing member beside a non-throwing one | `GetLastAsync` and `FindLastAsync` |

The guidelines say nothing about fluent chains, and the Azure page has no rule either way. Graph and SignalR are the precedents, and they're examples, not rules.

## Decision

### 1. Items are declared once, with their type

`ZabbixItem<T>` pairs a key with a value type, constrained to `ISpanParsable<T>`, so a value parses without reflection and holds under native AOT. `ZabbixItems` is the typed catalogue of Zabbix's own keys.

```csharp
static readonly ZabbixItem<long> Requests = new("app.requests");

await wire.SendAsync("web-01", Requests, 42);            // passing "ok" doesn't compile
long last = await zabbix.Hosts["web-01"].GetLastAsync(Requests);

double load = await zabbix.Hosts["web-01"].GetLastAsync(ZabbixItems.Cpu.Load());
long free   = await wire.GetAgentItemAsync("web-01", ZabbixItems.Disk.FreeBytes("/"));
```

`ZabbixValue` stays for what mixes items: a host's latest values, a store, a send loop. It gains `As<T>()`.

### 2. An application's own metrics use .NET's

```csharp
Meter meter = new("MyApp");
Counter<long> requests = meter.CreateCounter<long>("app.requests");

await using var agent = new ZabbixPushAgent("zbx-server-01", "web-01").AddMeter("MyApp");
await agent.StartAsync();

requests.Add(1);
```

An instrument's name is the item key, and a measurement's tags are the key's parameters. A counter records increases and Zabbix stores whole values, so the agent sends a running total. A histogram has no one value to send, so it's left out, with a warning in the log.

### 3. The API is fluent, and generated that way

```csharp
await using var zabbix = new ZabbixClient("https://zabbix.example/", token);

var hosts = await zabbix.Hosts.GetAsync();
var host  = await zabbix.Hosts["web-01"].GetAsync();

var web = await zabbix.Hosts.GetAsync(q =>
{
    q.Search.VisibleName = "web";
    q.Limit = 10;
});

await zabbix.Hosts.CreateAsync("web-02", [groupId]);
await zabbix.Hosts["web-02"].DeleteAsync();
await zabbix.Items[itemId].DeleteAsync();
```

- `ZabbixClient` holds each object's client directly, with no `.Api`. `ZabbixApi` is internal.
- Indexers reach one object, and make no call until they're asked. `Hosts` and `HostGroups` take a name, and every client with a read
  by id takes an id. These replace `client.Host()` and `client.Group()`.
- `GetAsync` and `CountAsync` take a lambda over their parameters, so nested filters need no `new`. `Filter` and `Search` are never
  `null`, only what is set is sent, and the indexer sets a field the SDK doesn't name: `q.Filter["a_new_field"] = "x"`.
- A get's parameters have setters, so the lambda can fill them. The full records stay, for a caller that builds them first.
  `GetAsync(new() { ... })` no longer resolves, because a target-typed `new` fits the lambda's overload too.
- `CreateAsync` has a short overload from the request's required fields, when there are at most five and none is a secret. A list goes
  last, so `CreateAsync("web-02", [groupId])` reads in the order a caller says it.
- A record of one id converts from that id, so `Groups = [groupId]` needs no wrapper. The full requests stay for the optional fields, as
  Graph's `new Calendar { ... }` does.

### 4. BCL types at the surface

| Was | Is |
| --- | --- |
| `ZabbixId` | `ulong` |
| `ZabbixClock` in public signatures | `DateTimeOffset`, with `ZabbixClock` kept internally for exact nanoseconds |
| `ZabbixServerNode`, `ZabbixServerActive` | `ServerActive` text, or `DnsEndPoint`. `ZabbixEndPoint` reads and writes Zabbix's spelling, which `DnsEndPoint` has no parser for |
| `ZabbixCredential.FromApiToken(token)` | A `token` argument, and `NetworkCredential` for a login; `ZabbixCredential` stays for a vault |
| `ZabbixSenderItem` | `ZabbixValue`, which a partial failure's `Unconfirmed` also holds |
| `ZabbixExchangeCallback` and the other delegates | `Func<...>` |

### 5. Chains, and builders where setup has parts

Agents chain: `new ZabbixActiveAgent(...).AddItem(...).AddItem(...)`. A client with much to set has a builder, `ZabbixWireClientBuilder` or `ZabbixClientBuilder`, beside the constructor a simple call uses, as `HubConnection` has both:

```csharp
using var wire = new ZabbixWireClientBuilder()
    .WithServer("zbx-1", "zbx-2")
    .WithPsk("web-01", key)
    .WithTimeout(TimeSpan.FromSeconds(5))
    .Build();
```

## Limits

- **Nanoseconds:** `DateTimeOffset` has 100 ns ticks and Zabbix stores nanoseconds, so public times round, including PowerShell's `-Clock` and `-Ns`. The SDK keeps the exact instant where history's `(itemid, clock, ns)` key needs it: reading and watching history, forwarding a value, and a value in an Entity Framework store.
- **PSK:** .NET has no TLS-PSK, so BouncyCastle stays. Certificate mode can take `X509Certificate2`.
- **Value mismatches:** if an item holds text on the server, `ZabbixItem<long>` fails when you read it, with `FormatException`.
- **Types by mode:** `vfs.fs.size[/,free]` is a whole number and `vfs.fs.size[/,pfree]` a decimal, so the catalogue has an entry for each.

## Rejected

- **`IQueryable` over the API.** The API has equality, IN, and substring and nothing else, so `Where` would promise what the server can't do. ADR 0006's reason holds for this. The lambda has no such problem.
- **Fluent builders for every request.** They'd be a second copy of each record's fields, and Graph doesn't do it.
- **Dropping all string keys.** A key the application invents, or reads from configuration, has to start as a string. It's declared once, and `agent.Items.Add(string, ...)` stays for keys only known at run time.
- **A rewrite of the engine.** Framing, TLS, failover, stores, the send loop, and the listeners aren't what's wrong, and their tests are the SDK's value.
- **Shorter overloads alone.** They'd leave string keys, string values, and every BCL gap in place.

## Consequences

- It ships in 1.0.0, the first release, so these breaks cost no one a migration.
- The documents and samples move to the new surface in the same work.
- The modules keep their commands, and build on the same records.
