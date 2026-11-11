<!--
This Source Code Form is subject to the terms of the Mozilla Public
License, v. 2.0. Copyright (c) 2026 schroeder247. SPDX-License-Identifier: MPL-2.0
-->
# Architecture

This document describes the shape of the SDK: one root, two planes and one sample, the layers they are built from, and
the behaviour that follows from that shape. It is written for a maintainer. Where the design names something the code
does not do yet, the section says so; [the known limitations](../KNOWN-LIMITATIONS.md) list them for users.

The SDK follows the project's design rules (platform first; a hand-written type needs a recorded reason; native
interfaces; designed errors; full telemetry). [The decisions register](decisions.md) records how those rules apply to
Zabbix, and [the platform survey](platform-survey.md) records the Microsoft libraries the push plane is built on. Where
Zabbix's own shape and a convenient .NET shape disagree, Zabbix wins; where Zabbix does not dictate, the BCL wins.
Where this document disagrees with Microsoft's published guidance, the guidance wins and this document is corrected
([the precedence rule](decisions.md#precedence)).

---

## The one function

**`value = f(host, key, time)`.** That is Zabbix's own data model, stated by its wire and its tables:
a history row is `(itemid → host, key)`, `clock`, `ns`, `value`; a sender frame carries
`host`, `key`, `value`, `clock`, `ns`; `item.lastvalue` is the latest sample of the function;
trends are folds of it over an hour. Every public type in the SDK is a slice or a projection of
that space and earns its surface by being honest about which axes it spans:

| Slice | Axes | Narrowing verbs it therefore carries |
| --- | --- | --- |
| `ZabbixItemKey` | key (schema only) | `Name`, `Parameters[i]` — a key is its parameters |
| `ZabbixValue` | one (host, key) at one instant | — (the sample; a record) |
| `ZabbixItemNode` | key, live on one host | `Watch`, `GetLatestAsync`, `GetLastValueAsync`, `GetHistoryAsync(range)` |
| `ZabbixHostNode` | key set, live on one host | `Item(key)`, enumeration of its items, `ExistsAsync` |
| `ZabbixClient` (root) | host × key, live | `Host(name)`, `Group(name)` (a view) |

Each axis keeps its own verb because each axis has its own geometry. The **host** axis is a flat set
(a host is in many groups, has many templates, is placed on at most one proxy — so groups, templates
and proxies are *selections* over the set, never rungs of a tree). The **key** axis is a set of
structured names per host (`name[param, …]`; no tree, so no subtree verb — `Item(key)` and LINQ's `Where`
are enough). The **time** axis is a stream: `Watch` for live, `GetHistoryAsync(range)` for the record.

The navigation verbs are **methods that mint sub-clients** (`Host(name)`, `Group(name)`, `Item(key)`), not indexers:
minting costs no call, an indexer must not do I/O, and a name's absence is known only when it is read. The first read of
a missing name throws `ZabbixNotFoundException` (a `KeyNotFoundException`); `ExistsAsync` is the Tester-Doer beside it.

Everything else Zabbix has is one of three things about that function, and the SDK says which:

| Zabbix thing | What it is | Where it lives in the SDK |
| --- | --- | --- |
| items, item prototypes, templates, discovery rules, value maps | the **schema** of the function | the entity clients (control plane), and the items a host node enumerates (`GetItemsAsync`) |
| host groups, proxies, proxy groups, maintenance | **placement and selection** | views over the host set; a proxy is also a node on the push path |
| triggers, events, problems, actions, alerts, SLA, reports | **derivations** of the function's stream | the entity clients; a client-side reaction is LINQ over `Watch` |
| users, roles, tokens, media, dashboards, maps, settings | the **server's own configuration** | the entity clients only |

---

## One root, two planes, one sample

```
ZabbixClient                 the root — one API endpoint + one credential; what DI hands out
├── control plane            the Zabbix API: one entity client per API object, generated from the API description
│     client.Hosts / .Items / .Triggers / .Templates / … each with GetAsync(params) · CountAsync · CreateAsync/UpdateAsync/DeleteAsync
└── data plane               the function: value = f(host, key, time)
      pull   server ⊃ AllHosts ⊃ Items every node answers IZabbixReadable + IZabbixWatchable
      push   source → Agent → Proxy → Sender → Server    every stage takes a ZabbixValue; every edge is a bounded channel
```

**One root.** A `ZabbixClient` is modelled on the Azure client triad (`new ZabbixClient(endpoint, credential)`, and
`ZabbixClient.CreateBuilder()` for the rest; immutable, thread-safe, a singleton; sub-clients reached as properties share its
pipeline and cost no call), and it is what `AddZabbix("prod")` registers and `IZabbixClientFactory.Create("prod")`
mints. The constructor and the standalone builder run the same registration on a container of their own
([ADR 0013](../adr/0013-builders-verbs-and-typed-values.md)). The request/reply transport beneath it is the platform's own: `IHttpClientFactory` with one
`SocketsHttpHandler` primary, the `DelegatingHandler` chain, `AddStandardResilienceHandler`, and one
`JsonSerializerContext` for every model. Nothing of the SDK's own mimics them.
`SetHandlerLifetime(Timeout.InfiniteTimeSpan)` keeps one handler, so the connection cap is not fragmented.
The root and the tree's nodes are mockable as the Azure guidelines ask: protected parameterless constructors and
virtual members, and constructors without default parameters ([Azure SDK .NET
guidelines](https://azure.github.io/azure-sdk/dotnet_introduction.html), `dotnet-client-constructor-for-mocking`,
`dotnet-client-constructor-no-default-params`). The generated `ZabbixApi` and its clients are too: `ZabbixApi` has a
protected constructor and a virtual client per object, and every generated client a protected constructor beside the
public one. The builder that `AddZabbix` returns
carries the verbs a container user needs: `WithCredential`, `ConfigureHttpClient` and `ConfigureResilience`. The wire's
real transport is public too: `ZabbixWireLink` connects to a node list and hands out the `ZabbixExchangeCallback` every wire
façade takes, so a caller can decorate it, as an `HttpClient` takes a `DelegatingHandler` over a `SocketsHttpHandler`.

**Two planes, one discipline, deliberately distinct.** Request/reply is a direct `await` through a
handler chain and is never routed through a bounded buffer (a request/reply call waiting on a bounded slot it
also has to free deadlocks). The data plane is a graph of bounded stages. They meet at exactly one seam — the
sender, which is a request/reply client *and* the last stage of the push path — and nowhere else.

**One sample.** `ZabbixValue(Host, Key, Value, Time)` is the record on every edge of both planes:
what a history read yields, what a watcher yields, what an agent's source produces, what a proxy
stores, what the sender frames. It is a `record`; its `Key` is a `ZabbixItemKey`; its `Time` is a `DateTimeOffset`,
over an internal exact instant (seconds and nanoseconds, as the wire carries it); it is converted at the edge that needs another schema
(the sender's frame for the wire, typed columns for EF) and nowhere in between. The only other record that crosses a
plane is the value's schema, because an agent needs the definition to know what to poll: the active agent's
definition is the server's `active checks` answer, `ZabbixItemAssignment` (item id, key, delay, timeout); the pull
plane reads the generated `ZabbixItem` record.

---

## The pull plane: one shape, every level

```
ZabbixClient        contains Hosts          (a set; Groups are views over it)
  ZabbixHostNode    contains Items          (a set keyed by ZabbixItemKey)
    ZabbixItemNode  the leaf: a definition (delay, value type) and the value stream
```

Every node answers the **same two interfaces**:

- **`IZabbixReadable`** — ask it for data: `GetLatestAsync()` (`item.get` with `lastvalue`/`lastclock`,
  filtered to this node) and `GetHistoryAsync(range)` (`history.get`, paged oldest-first by a `(clock, ns)` cursor, a
  saturated second read whole, value types merged in clock order, yielded as they arrive). Every read returns
  `IAsyncEnumerable<ZabbixValue>`: history yields page by page, and the latest values are one `item.get` reply, read
  whole and then yielded (the API has no cursor for `item.get`).
- **`IZabbixWatchable`** — tell it to observe: `Watch(plan)` returns a running
  `IZabbixWatcher : IAsyncEnumerable<ZabbixValue>` (the implementation, `ZabbixWatcher`, is internal). The plan says
  how often; the node says which items. Every node passes the caller's plan through unchanged; a node does not yet
  carry an interval of its own that would override the plan.

The tiers above the leaf also answer a structural contract, because a caller navigating needs what a
host *is* and a leaf never has. That contract is the concrete scope types (`ZabbixHostScope`,
`ZabbixHostNode`, each an `IAsyncEnumerable` of its children); there is no separate scope interface. A group's node
(`ZabbixHostGroupNode`) reads and deletes the group and reaches its hosts as a scope, `HostGroups["x"].Hosts["web-01"]`.

**Therefore the depth you call at is the blast radius**, and it compiles to the wire honestly:

```csharp
client.GetLatestAsync()                                // item.get { output: lastvalue }             — everything
client.HostGroups["Linux servers"].GetLatestAsync()         // item.get { groupids: [...] }               — one group
client.Hosts["web-01"].GetLatestAsync()                 // item.get { hostids: [...] }                — one host
client.Hosts["web-01"].Item("system.cpu.load").GetLatestAsync()  // item.get, this key verbatim         — one item
```

One verb, four depths, no targeting parameter anywhere, and the `.get` filter *is* the node — which
is why the SDK never claims `IQueryable` over the API: the API has equality, IN and substring and
nothing else, so the honest query surface is the node you hold plus `Where` on the sets it exposes,
and, on the control plane, the generated `*GetParameters` record, which is that grammar typed.

**Polling is autonomous; the heartbeat is the SDK's.** `Watch` ticks on its own `PeriodicTimer` over an
injected `TimeProvider`, whether or not anyone is reading, writes `.Current` (the materialised latest state,
a snapshot per tick — what an administrator sees in `Watch-ZbxItem`), and writes into a bounded channel per reader
with the plan's full mode and a counted drop. A slow reader costs dropped samples, never a missed poll.
Each tick reads **history since the last clock it saw**, so every value the items collected between two ticks is
emitted, whatever each item's own `delay` (a `lastvalue` poll would see only the latest of several values collected
between ticks); the item set is refreshed on its own interval, a failed tick is counted and logged, and the watcher
stops with its client. `Watch-ZbxItem` in the PowerShell module is a thin consumer of this node.

**Data travels up.** A host's stream *is* its items' merged; the server's is its hosts' merged — one
bounded channel with one writer per child. The collection is the merge; there is no `WatchAll`.

**Identity is stamped on the way up.** A `ZabbixValue` already carries `Host` and `Key`; a watcher
adds nothing, because the sample is flat by construction.

---

## The push plane: the same sample, pointed the other way

Zabbix's centre of gravity is push: agents and proxies store and forward over the framed TCP
protocol, and the server is fed. So the push plane is a pipeline — sources → store → wire — and each
edge is the platform's bounded channel read as a stream: the store is a `ChannelWriter<T>` on one face and
an `IAsyncEnumerable<T>` on the other, its policy stated once as the channel's options; completion travels
it like an item, so a source ending completes the store and the drain ends when the store is empty.
(TPL Dataflow is not used for this role; [the decisions register](decisions.md#a-store-is-a-channel-not-a-dataflow-block)
has the measurement that decides it.)

```
ZabbixPoll.Every(interval, collect)    a PeriodicTimer over TimeProvider; yields ZabbixValue        ─┐
ZabbixTrapperSource (AddTrapper)       whatever arrives on the trapper port                           ├─▶ ZabbixAgent  (one host's items, merged)
                                                                                                    ─┘        │
                                                                                                              ▼
                                                              ZabbixStore<T>   the delivery buffer: bounded, a named overflow policy, admit/discard predicates
                                                              (in memory, or durable through EF Core — one abstraction, mechanism inside the implementation)
                                                                                                              │
                                                                                                              ▼
                                                              ZabbixSender           request/reply to the trapper (agents); the proxy leases, sends, and acknowledges on the server's success
```

A source is any `IAsyncEnumerable<ZabbixValue>` handed to the agent builder — `AddSource`, `AddPoll` (over
`ZabbixPoll.Every`) or `AddTrapper` (in `Zabbix.Proxy`). The active agent's
per-item delays are its due queue over `ZabbixItemDelay`.

- **An agent is a host node pointed the other way**: it holds items, each producing on its own delay
  (`ZabbixItemDelay`, upstream's flexible and scheduling intervals, on a due queue over `TimeProvider`);
  its output is the merge. The `active checks` refresh and `agent data` are request/reply on the same
  wire; the heartbeat is a **notification** (`ZabbixNotificationCallback`: written, never read), because a Zabbix server
  never answers a successful one. A passive agent is the same item set behind a listener that answers the server's
  request.
- **A proxy is an agent over many hosts plus the store**, uploading `proxy data` and pulling
  `proxy config` on a cadence. The configuration is **merged** by id from the server's deltas and frozen per
  revision (a delta never replaces the snapshot); a value is admitted by Zabbix's own rules (the item's type,
  `trapper_hosts`, the literal key); a page carries the store's sequences as its ids; `upload: disabled` holds the
  page. A passive proxy is the same store behind a listener whose handler owns the **conversation**: the server's
  request, the proxy's answer, the server's acknowledgement, on one connection. The proxy does not run Zabbix's
  preprocessing chain.
- **The store decouples the sources from the wire, and it is not the only buffer.** Each source keeps a bounded
  channel of its own ahead of it, as the channels guidance shapes every producer and consumer hand-off (a named
  `BoundedChannelFullMode` and a counted drop, [System.Threading.Channels](https://learn.microsoft.com/dotnet/core/extensions/channels)):
  `ZabbixPoll.Every` holds `DefaultMaxItems` (4096) values for a reader that has fallen behind and drops the
  oldest, so the poll keeps its cadence; the trapper's edge holds the push agent's `MaxItems` (100,000) values and
  refuses what does not fit, so the sender is told "failed" for those values rather than "processed" for a value that
  was dropped. The store is where values wait for delivery and where durability lives. Its policy is one closed options type (`ZabbixStorePolicy<T>`, a sealed record
  with init properties, handed to the store's constructor) — `MaxItems`, `Overflow` (the BCL's `BoundedChannelFullMode`), `MaxAge` (the offline
  buffer), and two predicates, `Admit` and `Discard` — because "drop the oldest" is a strategy the engine implements
  and "which values are stale" is a selection only the caller can state. It has **two read faces**: the stream (a
  record leaves as it is read) and the **acknowledged face** (`WaitToLeaseAsync`, `TryLease`,
  `TryPeek`, `Acknowledge`, and `Release(sequence)` to rewind the lease cursor; a leased `ZabbixStoreEntry<T>` stays
  held until acknowledged; the agents' send loop and the proxy). Durability is a `ZabbixPersistentStore<T>` subclass supplying four members; on a
  durable store a leased batch survives a crash and is leased again with the same sequences. The promise is **"kept
  until the destination's success"**: a value the server acknowledged and then lost (a demoted HA node) is not kept.
- **The sender is the seam.** `ZabbixWireClient.SendAsync(values)` sends directly, in frames of at most `BatchSize` values
  and `MaxBatchBytes` bytes as `zabbix_sender` does, and it is what the send loop drains into, one loop batch per
  frame. A send stopped after some frames were taken says so (`ZabbixSenderIncompleteException`: what was taken, what
  was not confirmed), so nothing confirmed is sent twice. Delivery is the server's answer: `success` is delivered (a per-value `failed > 0` is
  data), and a `failed` reply or an unreadable `info` is a typed failure. A redirect is followed once per exchange, and
  one that cannot be followed fails. None of them is "delivered".
  A transport failure retains the batch and the store's policy decides what survives an outage.
  The HA node list is walked **as upstream walks it** (`commshigh.c`), not by a retry policy: the next node is tried
  only when a connect or TLS handshake fails, the node that answered stays first, and nothing is re-sent after the
  write. A generic retry cannot tell the connect phase from the reply phase and would re-send written frames. Delay
  and backoff belong to the send loop's retry. Proxy-group redirects are followed by the exchange.

The public seams on this plane are the plainest shapes: a producer is `IAsyncEnumerable<ZabbixValue>`
or a `ChannelWriter<ZabbixValue>` it is handed; a destination is a delegate returning a delivery
status; a durable backing is an abstract class with four members (`ZabbixPersistentStore<T>`). Nothing but the BCL's two
faces shows, so a test double is three lines and the core is usable without learning any framework.

---

## Two directions through one document

**The document is one thing, written four ways.** The document is one configuration graph:
servers (endpoint, credential, resilience, the HA node list), agents (host, items with delays,
where to send), proxies, trappers. It is bound by three composition roots, one per package, each binding the sections its package builds
(`ZabbixConfigurationSections` in `Zabbix.Abstractions` names them): `AddZabbixClients` in `Zabbix.Sdk` binds servers,
`AddZabbixAgents` in `Zabbix.Agent` binds push agents and active agents, and `AddZabbixProxies` in
`Zabbix.Proxy` calls the agent root and adds passive agents, proxies and trappers. A host that also calls the API calls
`AddZabbixClients` beside its hosting root. (`ZabbixOptions` is one client's options, not the document.) A fluent chain writes it, a file
binds to it (`BindConfiguration`, the binder generator, `[OptionsValidator]`, `ValidateOnStart`), a PowerShell
hashtable *is* it, and one composition root turns whichever wrote it into running objects — so the faces cannot drift,
because there is no projection between two shapes to forget. Every named thing (`AddZabbix("prod")`,
`AddZabbixPushAgent("edge-01")`) is a `(Name, Services)` builder whose verbs write named options; keyed DI is the veneer,
a factory is the minting seam. An agent or proxy is a class
([ADR 0010](../adr/0010-agent-proxy-client-classes.md)): its registration calls the constructor from the named options,
resolves the class as a keyed service under the name, and hosts it through an adapter. An agent's standalone builder
(`ZabbixActiveAgent.CreateBuilder("web-01")`, [ADR 0013](../adr/0013-builders-verbs-and-typed-values.md)) makes the same
registration in a container of its own, builds it, and gives the agent that container and what it hosts.

**Commands travel down.** A watch plan passes through server → host → item unchanged (see the pull plane above);
an agent's items each carry their own delay from the item definition, the way Zabbix itself schedules.

**The shell is one consumer.** The session's context holds the connections; a cmdlet mints its client from the
context and holds no state of its own. `Connect-ZbxServer` declares its parameters directly (`-Uri`, `-ApiToken`,
`-Credential`, `-Name`, `-Trapper`, `-NoDefault`, `-PassThru`, …). The CRUD cmdlets are generated from the same API
description the entity clients are generated from, and a `.get` filter is one `-Filter` parameter typed as the
entity's parameters record, not a filter cmdlet per field. The hand-written, administrator-facing commands
(`Watch-ZbxItem`, `Show-ZbxWatch`, `Test-ZbxConfiguration`, `Publish-ZbxAgentHost`) sit on the
same core, and the module adapts to the SDK, never the other way.

---

## Protection

- **In front of the server.** The API is bounded by the connection pool:
  `SocketsHttpHandler.MaxConnectionsPerServer`, from `ZabbixOptions.MaxConnectionsPerServer` (default 100), plus the
  standard resilience handler's own concurrency limiter; requests beyond the pool queue in the handler. A reply is
  bounded too: the body is buffered inside the deadline up to `ZabbixOptions.MaxResponseContentBytes` (1 GiB, the
  wire's `ZBX_MAX_RECV_DATA_SIZE`), and a larger one is `InvalidDataException`, sent once. A per-endpoint
  `PartitionedRateLimiter<Uri>` that would take a permit per request before it is sent is part of the design and not
  built.
- **Beneath the graph.** Every stage is bounded; the store's overflow is a named policy with a counted
  drop; `Wait` exists only on pull-driven paths (a history read paces the reader; a watcher never waits).
- **At the socket.** The framed TCP substrate is one transport on `System.IO.Pipelines` with TLS
  (`SslStream`) and PSK (BouncyCastle, because .NET has no TLS-PSK API) applied once; every protocol client —
  `ZabbixWireClient`, the proxy uplink — is a thin typed façade over it. Listeners (passive agent, passive proxy, trapper) bound
  their accepted connections with a `ConcurrencyLimiter` and a per-peer `PartitionedRateLimiter`, never a
  `SemaphoreSlim`.
- **At the door.** Every listener **fails closed**: `AllowedPeers` is required, parsed as Zabbix's `Server=` grammar
  (`ZabbixAllowedPeers`: addresses, CIDR blocks, names resolved per check), and an empty list fails validation at
  start; "everyone" is written out. `TlsAccept` is a set, enforced per connection. A certificate peer is trusted only
  through the configured CA and pinned by issuer and subject as upstream prints them (`ZabbixCertificateName`). This is
  **stricter than the upstream server's trapper**, which has no peer list at all: an SDK listener that accepts values
  is always told who may send them.

---

## What falls out — and therefore must never be written

| Behaviour | Falls out of | So there is no… |
| --- | --- | --- |
| Fan-in | a host's stream being its items' merged | `WatchAll`, a merge the caller writes |
| Blast radius | choosing which node you call | a `hostids:` parameter on every read verb |
| The `.get` filter | the node compiling itself | a query-builder that lies about `IQueryable` |
| Precedence | the plan passing through nodes with `with` | a config merger |
| Batching on the wire | the sender's frames (`BatchSize`, `MaxBatchBytes`), and the store draining in batches, one frame each | a batch scheduler |
| Back-pressure | the store between an autonomous producer and the sender; `Held` as a delivery outcome | a flow controller, an `upload: disabled` flag |
| Durability | the durable store being a `ZabbixPersistentStore<T>` with an acknowledged face | `InMemory`/`File`/`Sqlite` stores each with their own locks |
| Cancellation | one token threaded through the graph | a shutdown orchestrator |
| Provenance | the sample carrying `Host` and `Key` from birth | a correlation table |
| Hot reload | **does not fall out**: a server's endpoint, token, connection cap and deadlines, and a listener's port and peers, are read once at start; only the resilience settings reload. A restart applies the rest. | — |
| Failover | one node walk that fails over only at connect or handshake, sticky to the node that answered | a `for (node in nodes)` loop with `lastException`; a retry that re-sends after the write |
| Entity clients | the generator over the API description, extracted from the Zabbix source per version (`tools/ZabbixApiExtract`) | hand-written services, interfaces and builders per API object |
| The PowerShell CRUD surface | the same generator | hand-maintained CRUD cmdlets |
| No god object | no node knowing anything but its children | manager, coordinator, engine — at all |

**The design rule:** if a container has to grow a method to coordinate its children, the containment
model has failed. A new capability is a new node type, a new predicate, or a new generated entity —
never a new manager.

---

## The packages: one per protocol

Zabbix speaks three protocols, and each is a package with its own dependency weight
([ADR 0006](../adr/0006-protocol-split.md)): the **JSON-RPC API** is `Zabbix.Sdk` (the server, the clients, the tree;
Microsoft.Extensions and the HTTP resilience pipeline, no TCP, no BouncyCastle) over `Zabbix.Api` (the generated
vocabulary); the **framed TCP wire** is `Zabbix.Wire` (frames, TLS and PSK, `ZabbixWireClient`;
the contracts, BouncyCastle and the telemetry abstractions, nothing of the API); **listening and hosting** is `Zabbix.Agent` (the agents, the send
loop and the store) and `Zabbix.Proxy` (the listeners, the trapper, the proxies), with the durable store
`Zabbix.Agent.Storage.EntityFrameworkCore` beside them. `Zabbix.Abstractions` is the seam all of them share: the
sample, the key, the clock, the options, the credential, the API transport, the errors, the telemetry names, and the
item contract an agent answers keys through ([ADR 0008](../adr/0008-item-providers-and-plugins.md)), which
`Zabbix.Agent.Plugins` loads from a folder with nothing else beneath it.
`DesignConformanceTests.Dependencies` holds the policy.

```
Zabbix.Abstractions ◀── Zabbix.Api ◀── Zabbix.Sdk
        ▲                    ▲
        │                    └─────────────────────────────┐
        ├──────── Zabbix.Wire ◀── Zabbix.Agent ◀── Zabbix.Proxy
        │                              ▲
        │                              └── Zabbix.Agent.Storage.EntityFrameworkCore
        └──────── Zabbix.Agent.Plugins
```

The hosting packages take the wire and not the API client: an agent and a proxy send over the framed TCP wire. The proxy
takes `Zabbix.Api` for two generated words, `ZabbixTlsAcceptModes` and the connector's `ZabbixTag`.

A database view of the same objects, `Community.Zabbix.Data` (read-only, generated per version from the server's
`create/src/schema.tmpl`), is designed and not built ([ADR 0006](../adr/0006-protocol-split.md), section 5).

## The layers

Each layer owns its telemetry: a `{Layer}Telemetry : ZabbixLayerTelemetry` whose one `Start`/`Finish` pair writes the
span and the duration sample together (seconds, `error.type`, `server.*` where there is a peer), and a `{Layer}Log` of
`[LoggerMessage]` events in its own id block. An application subscribes to all of them with
`ZabbixTelemetryNames.Subscription` (`Community.Zabbix.*`).

| Layer | What it is | Source and meter | Log, ids |
| --- | --- | --- | --- |
| API | `ZabbixClient`, `JsonRpcTransport` over `IHttpClientFactory` + the standard resilience handler + `ZabbixResponseBufferingHandler` (the body inside the deadline), the generated clients, `ZabbixSessionCredential` (logs in with `userData: true` and refuses once, with the reason, a user whose group requires MFA), `ZabbixApiErrorKind`, `ZabbixFrontend` (page links by the server's version) | `Community.Zabbix.Api` (`ZabbixApiTelemetry`: `rpc.client.call.duration`, `rpc.system.name`, `zabbix.api.error.code`/`kind`) | `ApiLog`, 40xx |
| Tree | `ZabbixClient`/`ZabbixHostScope`/`ZabbixHostGroupNode`/`ZabbixHostNode`/`ZabbixItemNode`, `ZabbixHistoryRead`, `ZabbixWatcher` | `Community.Zabbix.Tree` (`ZabbixTreeTelemetry`) | `TreeLog`, 50xx |
| Wire | `ZabbixHeader` (upstream's frame rules; a reply is capped at 16 MiB unless `MaxPayloadBytes` says otherwise, a proxy's configuration at upstream's 16 GiB), `ZabbixNodeLink` (the node walk; owns certificate mode's trust and is disposed by whoever built it: a wire client, the agents and proxies), `ZabbixFrames` (`Over` = exchange, `Notify` = notification, over a link nobody disposes), `ZabbixTls` (CA-only trust, `ZabbixCertificateName` pins, `ZabbixRevocationLists`, BouncyCastle PSK), `ZabbixWireClient` (over `ZabbixSender`, `ZabbixGetter` and `ZabbixServerWire`, internal: sends, reads from an agent, and the server's own trapper requests; a span per request named for it) | `Community.Zabbix.Wire` (`ZabbixWireTelemetry`) | `WireLog`, 1000–1399 |
| Store | `ZabbixStore<T>` (a `ChannelWriter<T>`, a stream face and an acknowledged face: `WaitToLeaseAsync`/`TryLease`/`TryPeek`/`Acknowledge`/`Release`), `ZabbixMemoryStore<T>`, `ZabbixPersistentStore<T>`, `ZabbixEntityFrameworkStore<T>` (a table per store). The proxies keep a failed page in memory and re-send it rather than releasing it, because a page re-leased later could map differently against a newer config. An agent's own backing comes through its builder's `UseStore`, `(policy, services) => …` or `(provider, policy, services) => …` (`UseEntityFrameworkStore` is one), and a proxy's through `AddStore`, called once per destination | `Community.Zabbix.Store` (`zabbix.store.dropped`, `.acknowledged`, `.pending`) | `StoreLog`, 20xx, 21xx |
| Agent | `ZabbixSendLoop` (an owned enumerator; drains within one window from the stop), `ZabbixPushAgent`, `ZabbixActiveAgent` (a due queue by `ZabbixItemDelay`, the heartbeat a notification), `ZabbixItemDictionary` (an agent's one table of keys), health; `ZabbixComponent`, the lifetime every agent and proxy shares with no host, and `ZabbixHostedComponent`, the adapter a host runs it through ([ADR 0010](../adr/0010-agent-proxy-client-classes.md)) | `Community.Zabbix.Agent` (`ZabbixAgentTelemetry`) | `AgentLog`, 30xx–33xx |
| Listener | `ZabbixListener` (an own accept loop over `IConnectionListenerFactory`; why not Kestrel's `ConnectionHandler` is written at the type), `ZabbixConversationCallback` (a handler owns the connection), `ZabbixAllowedPeers` (fail closed), `ZabbixTlsAcceptor` (the `TlsAccept` set; the PSK server), the trapper, the passive agent, the connector | `Community.Zabbix.Listener` (`ZabbixListenerTelemetry`: `zabbix.listener.connection.duration`, `.active_connections`, `.rejected_connections`) | `ListenerLog`, 60xx–63xx |
| Proxy | `ZabbixActiveProxy` (active), the passive proxy's conversation, `ZabbixProxyUplink`, `ZabbixProxyConfig` (merged deltas, frozen per revision), `ZabbixProxyAdmission`, `ZabbixProxyProtocol`/`ZabbixComponentVersion` (the version as the server compares it), `ZabbixProxyPaging`, and the **extension-point delegates**: `ZabbixProxyDataCallback` (a page computed at send time, merged by `ZabbixProxyDataProviders` into the page leased from the stores), `ZabbixProxyTaskCallback` (a task from the server's acknowledgement), `ZabbixPassiveProxyConfigCallback` (observe or veto a pushed config), `ZabbixProxyStatsCallback` (`zabbix.stats`). The stores stay the push surface; a delegate is the pull seam, the same shape as `ZabbixConversationCallback` | `Community.Zabbix.Proxy` (`ZabbixProxyTelemetry`; `zabbix.proxy.values` carries a `zabbix.proxy.kind` tag — history, discovery, autoregistration, interface_availability, tasks) | `ProxyLog`, 70xx |
| Shell | `Zabbix.PowerShell`: a per-runspace context (`ConditionalWeakTable<Runspace, ZabbixContext>`) holding the session's connections and long-running sessions, each connection `AddZabbix` and each agent or passive proxy session the SDK's own class, constructed as an application constructs it with the module's logging, clock and meters (`ZabbixSessionHost`); `Connect-ZbxServer`/`Disconnect-ZbxServer`/`Get-ZbxContext`; `ZabbixScriptRunspace`, the in-process bridge that lets a hand-written agent or proxy handler cmdlet run a user's `[scriptblock]` inside the runspace, not a subprocess; the CRUD cmdlets and nested-record builders generated by `Zabbix.Generator` from the same entity contracts as the C# clients; **input binding through PowerShell's own binder**: `ZabbixRecordConverter : PSTypeConverter` is the `<TypeConverter>` of every request, parameters and nested-request record in the generated `Community.Zabbix.types.ps1xml` (loaded by the manifest's `TypesToProcess`), so `-Filter`, record-typed parameters and `[Zabbix.<Record>]@{…}` casts bind with no call site, through `ZabbixJsonBinder` on the SDK's own JSON contract; `-AdditionalProperty` merges last; **output**: `ZabbixAdditionalFieldsAdapter : PSPropertyAdapter` is the `<TypeAdapter>` of every read record, so an unmodelled wire field reads as `$obj.<field>` (`RequiredAssemblies` loads `Community.Zabbix.Api.dll` first), and a generated `Community.Zabbix.format.ps1xml` gives every read record a table, list and wide view; MAML help generated at build time from the cmdlets' XML doc comments (`Zabbix.PowerShell.HelpGen`) plus a build-derived module manifest; one `ILoggerProvider` to the PowerShell streams; `ZabbixReference<T>` (id, name, or a piped entity — the one reference currency) and `ulong : IParsable<ulong>`. [PowerShell binding](powershell-binding.md) has the research behind these seams | `Community.Zabbix.PowerShell` (`ZabbixPowerShellTelemetry`) | 14xxx |

Every agent and proxy is a `ZabbixComponent` with the generic host's phases, which a host runs through the
`IHostedLifecycleService` adapter, `ZabbixHostedComponent`. A trapper is a listener whose handler writes into
a store; an active proxy is that trapper plus the upload loop over the store's acknowledged face and a cadence that
pulls and merges `proxy config`; a passive proxy is the same store behind a conversation; a passive agent is a listener
whose handler answers keys; an active agent is a due queue of items into the store and the send loop pointed at
`agent data`. [The platform survey](platform-survey.md) records why each primitive was chosen.

## The entity surface

The API's objects are not the tree — they are the vocabulary the tree is written in. `ZabbixClient` carries one client per
API object, every one generated at build time from the checked-in description (`schema/zabbix-api.json`, extracted from
the Zabbix 7.0, 7.4 and 8.0 PHP source by `tools/ZabbixApiExtract`): `client.Hosts.GetAsync(parameters)` is
`host.get` and returns `IReadOnlyList<T>`, `CountAsync` is `host.get` with `countOutput`,
`CreateAsync`/`UpdateAsync`/`DeleteAsync` return the ids the server names, and the mass verbs and the object's other
methods are there when the API has them. The records live in `Zabbix.Api`, the clients in `Zabbix.Sdk`; a user who
needs a method no client names has `client.SendAsync` and `ZabbixJson` for every record. The tree's own
reads (`item.get`, `history.get`, `host.get`, `hostgroup.get`) go through the same clients. A method or parameter
missing from the connected version is refused at run time (`ZabbixSupportedAttribute`, `ZabbixVersionException`);
there is no compile-time check against a target version.

## Not built

The design names these and the code does not have them yet:

- `TrendsAsync(range)` on the nodes, and `Templates`/`Proxies` views beside `Group(name)`;
- a per-node interval in a watch plan (every node passes the caller's plan through);
- the per-endpoint `PartitionedRateLimiter<Uri>` in front of the API;
- Zabbix preprocessing on the proxy;
- a compile-time analyzer for the version gate.

Every object id is a `ulong`, in the API's records, the wire's rows, the hosts' assignments and the PowerShell
module alike; `DesignConformanceTests.Ids` holds the rule.

## In one sentence

One root that is the Azure client shape over the platform's own HTTP pipeline, exposing the API as
generated entity clients and the function `value = f(host, key, time)` as a three-level tree whose
nodes all answer *ask me* and *watch me*; the same sample flowing the other way through sources,
agents and proxies through bounded edges into one store and out through the sender; the whole thing described by one
options graph that a chain, a file and a hashtable all write; and a shell that is one consumer of it.
