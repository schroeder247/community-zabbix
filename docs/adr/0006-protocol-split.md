<!--
This Source Code Form is subject to the terms of the Mozilla Public
License, v. 2.0. Copyright (c) 2026 schroeder247. SPDX-License-Identifier: MPL-2.0
-->
# 0006. One package per protocol; one identity, two views; Zabbix.Data reserved; the sources pinned

- **Status:** Accepted (the package split, the dependency policy, the deletions, the source pin). Proposed, not for
  1.0: `Zabbix.Data`. Implemented: the frontend deep links (§7). Corrected 2026-10-07: the `Zabbix.Proxy` row named
  the ASP.NET Core shared framework "its connection layer only"; the connector endpoint uses its HTTP and routing
  assemblies too. Amended 2026-10-12: the names below are the assembly names; each package's id is `Community.` + its
  assembly name (`Community.Zabbix.Sdk`; [0002](0002-package-ids.md), amendment).
- **Date:** 2026-10-05
- **Reversed in part by:** [0012](0012-typed-bcl-fluent-api.md), which brings back `AddMeter` and makes the API fluent. `client.Api` is now `zabbix.Hosts`.
- **Amended by:** [0013](0013-builders-verbs-and-typed-values.md), for §2: `Zabbix.Wire` takes the DI and configuration-binding pieces its builder composes.
- **Amends:** [0002](0002-package-ids.md) (the package table) and [0003](0003-powershell-module-split.md) (the `Zabbix`
  module's references and count).

## Context

A study of the tree before the first release found the SDK had grown sideways. `Zabbix.Sdk`, the package every consumer
takes, carried two protocols: the JSON-RPC API over HTTP and the framed TCP wire, with BouncyCastle for TLS-PSK, so an
application that only calls the API took a TLS stack. The store contract every host buffers in lived in
`Zabbix.Abstractions`, which every package takes, although only the hosts use it. And beside the product had grown
surfaces with no consumer: a second query language over the API, a windowed batch type, a meter source, an HA resolver
on a service-discovery package, a YAML configuration provider, and thirteen PowerShell commands that either fed a path
the proxy uplink does not have or duplicated another command.

Zabbix itself speaks three protocols, and each has its own weight:

| Protocol | What speaks it | What it needs |
| --- | --- | --- |
| The JSON-RPC API (`api_jsonrpc.php`) | the frontend, scripts, this SDK's clients and tree | HTTP, JSON, resilience |
| The framed TCP wire (`ZBXD` header, `sender data`, `agent data`, `active checks`, the server's own requests) | `zabbix_sender`, `zabbix_get`, agents, proxies, the frontend's `CZabbixServer` | sockets, TLS with certificates and PSK |
| Listening and hosting (the trapper, the passive agent, the proxies, the connector) | agents, proxies, servers | the generic host, a connection layer, a store |

## Decision

### 1. One package per protocol

| Package | Protocol | Holds | Depends on | Release DLL | Packages in its closure |
| --- | --- | --- | --- | --- | --- |
| `Zabbix.Abstractions` | the seam all three share | `ZabbixValue`, `ZabbixItemKey`, `ZabbixClock`, `ZabbixComponentVersion`, the options and credentials, `IZabbixTransport`, the exceptions and error kinds, the data classifications, the telemetry names, `IZabbixAdditionalFields`, the JSON dialect | Microsoft.Extensions abstractions | 76 KB | 6 |
| `Zabbix.Api` | JSON-RPC (vocabulary) | the generated records, enums, parameters and requests | `Zabbix.Abstractions` | 2.8 MB | 6 |
| `Zabbix.Sdk` | JSON-RPC | `ZabbixClient`, `AddZabbix`, the transport, the generated clients, the 8.0 mass-update emulations, the tree and its watcher, redaction, options validation | `Zabbix.Api`, Microsoft.Extensions, HTTP resilience | 7.8 MB | 31 |
| `Zabbix.Wire` | framed TCP | the frame, the exchange and its node walk, redirects, TLS (certificates, PSK), the sender, the getter, the server wire, the wire's options and endpoint grammar (`ZabbixServerNode`, `ZabbixServerActive`, `ZabbixTlsOptions`) | `Zabbix.Abstractions`, BouncyCastle, `Microsoft.Extensions.Telemetry.Abstractions` | 203 KB | 9 |
| `Zabbix.Agent` | hosting | the agents, the active agent and its item delays (`ZabbixItemDelay`), the send loop, the poll source, item handlers, health, and the store (`ZabbixStore<T>`, `PersistentStore<T>`, `MemoryStore<T>`, the storage builder, `ZabbixDelivery`) | `Zabbix.Wire`, the generic host's abstractions, health checks, `Microsoft.Extensions.Resilience` (the send loop's retry), logging and metrics | 268 KB | 28 |
| `Zabbix.Proxy` | listening | the listener, the trapper, the passive agent, the proxies, the connector | `Zabbix.Agent`, `Zabbix.Api` (the generated `ZabbixTlsAcceptModes`), the ASP.NET Core shared framework: its connection layer (`Microsoft.AspNetCore.Connections`, Kestrel's socket transport) for the listeners, and `Microsoft.AspNetCore.Http`, `Routing` and `Builder` for the connector endpoint (`MapZabbixConnector`, mapped onto the caller's own web application); no web host is started by the listeners | 528 KB | 12 + the shared framework |
| `Zabbix.Agent.Storage.EntityFrameworkCore` | hosting (a store) | `EfStore<T>`, the durable store; namespace `Zabbix.Storage.EntityFrameworkCore` | `Zabbix.Agent`, EF Core Relational | 18 KB | 35 |

The EF store stays a package of its own (a consumer takes a durable store without the proxy) and is named after the
contract it implements. The tree's interfaces (`IZabbixNode`, `IZabbixReadable`, `IZabbixWatchable`,
`IZabbixWatcher`, `ZabbixWatchPlan`) moved from `Zabbix.Abstractions` to `Zabbix.Sdk`, the only package that implements
them. The sender's `DeliverAsync`, which answers in the store's `ZabbixDelivery`, is an extension in `Zabbix.Agent`
beside `DrainAsync`, so the wire knows nothing of the store. `ZabbixClient` exposes nothing wire-side; the server
wire's spans and durations moved from the tree's telemetry to the wire's (`rpc.method` names the request) and its log
events (1300–1302) to `WireLog`, ids unchanged. `Zabbix.Abstractions`' declared public surface went from 532 lines to
306 with the moves and to 265 with the deletions below.

The PowerShell module `Zabbix` references `Zabbix.Sdk` and `Zabbix.Wire`: `Send-ZbxData` belongs beside `Get-ZbxHost`,
and the wire is small. `Zabbix.Agent` and `Zabbix.Proxy` keep their scope.

### 2. The dependency policy, enforced

- `Zabbix.Sdk` takes Microsoft.Extensions packages (the abstractions, and the DI, logging, options and
  configuration-binding pieces the standalone `ZabbixClient` composes) and the HTTP resilience pipeline. It takes no
  TCP, no BouncyCastle, no generic host (`Microsoft.Extensions.Hosting`), no ASP.NET Core, no EF Core, no service
  discovery, no health checks, no OpenTelemetry SDK and no other third-party package. `Microsoft.Extensions.Hosting.Abstractions`
  is in its closure because dotnet/extensions' telemetry packages bring it; it is an abstraction, not the host.
- `Zabbix.Wire` takes `Zabbix.Abstractions`, BouncyCastle, `Microsoft.Extensions.Telemetry.Abstractions`, and the DI and
  configuration-binding pieces its standalone builder composes ([0013](0013-builders-verbs-and-typed-values.md)), and
  nothing of the API (`Zabbix.Sdk`, `Zabbix.Api`, the HTTP stack, Polly) and not `Microsoft.Extensions.Telemetry`. The
  telemetry abstractions carry the extended `[LoggerMessage]` generator: the getter's events classify the item key, and
  the shared framework's generator would compile the same declarations and log the key in clear text. Every library
  that declares a classified log parameter takes the package, and `DesignConformanceTests.Redaction` proves it by
  logging each classified event through an unredacted logger.
- `Zabbix.Abstractions` takes abstractions only (`*.Abstractions`, Options, Primitives).
- Implementation packages (the generic host, health checks, ASP.NET Core, EF Core and its providers) are taken only by
  the hosting packages (`Zabbix.Agent`, `Zabbix.Proxy`, the EF store).
- The hosting packages take the wire and never `Zabbix.Sdk` or the HTTP pipeline: an agent and a proxy send over the
  framed TCP wire and do not call the API. Each package's document root binds its own sections of the one document
  (`ZabbixDocument`, in `Zabbix.Abstractions`), so a host that also calls the API calls `AddZabbixDocument` beside its
  hosting root.

`DesignConformanceTests.Dependencies` reads each library's resolved closure (`obj/project.assets.json`) and fails on a
package the policy forbids; `ModuleShapeTests` holds the module `Zabbix`'s folder to the same line.

### 3. Deletions

Each is in the maintainer's archive of the development history, which is not published (the public repository starts
at the first release; see [ADR 0005](0005-documentation-set.md)).

| Removed | Why |
| --- | --- |
| The LINQ-style query provider: `IZabbixQuery<T>`, `ZabbixQuery<TModel, TParameters>`, the `ZabbixQuery` statics, `ZabbixQueryException`, the generated `Query()` on every client (and the generator's branch) | a duplicate path: the generated `*GetParameters` record already states the `.get` grammar, typed; the provider compiled it a second way |
| `ZabbixValueBatch` (`For`, `Latest`, `At`, `Diff`, `WindowsAsync`) | no consumer in the SDK, the modules or the samples; LINQ over the history stream groups it |
| `ZabbixMeterSource` and `AddMeter` | no consumer; an `AddPoll` over a `MeterListener` is the same thing in the application |
| The HA active-node resolver on `Microsoft.Extensions.ServiceDiscovery` (`UseActiveNodeResolver`, `ZabbixActiveNodeResolver`, its options, `ResolveWith`, `ZabbixServerTopology`, `ZabbixTopologySource`) and the package | a duplicate path: the wire's own node walk is upstream's (`commshigh.c`: the declared nodes in order, failover at connect or handshake, sticky to the node that answered) |
| `Zabbix.Configuration.Yaml` (and YamlDotNet) | no precedent among Microsoft's configuration providers and no demand; the document binds from any `IConfiguration` source |
| `Register-`, `Unregister-`, `Get-ZbxDiscoverySource`, `Get-ZbxDiscoveryHit`, `ConvertTo-`, `New-ZbxDiscoveryEvent`, `Resolve-ZbxDiscoveryRule`, `Get-`, `Set-ZbxProxyContext`, `ZabbixDiscoverySource` and its store | no consumer: they fed discovery data to a proxy uplink that carries history only |
| `Show-ZbxDashboard`, `Show-ZbxGraph` | the frontend draws dashboards and graphs; the `Get-ZbxDashboard*` CRUD and `Show-ZbxWatch` stay, and `Show-` is kept for the frontend view (below) |
| `Export-ZbxEnvironment`, `Import-ZbxEnvironment` | a duplicate path beside `Export-`, `Import-`, `Compare-` and `Test-ZbxConfiguration` (the batched export stays) |
| `schema/zabbix-api-{7.0,7.4,8.0}.json` in git | regenerable intermediates of the pinned sources (section 6) |

In numbers: about 3,500 hand-written lines of source and 1,800 of tests, 13 cmdlets (272 → 259 in `Zabbix`; 20 and 25
in the siblings unchanged), one package (`Zabbix.Configuration.Yaml`), two third-party or platform dependencies
(YamlDotNet, `Microsoft.Extensions.ServiceDiscovery`), and BouncyCastle from `Zabbix.Sdk`'s closure.

### 4. One identity, two views

An API record and a database row of the same object are two views of one identity: they share its id. What both views
carry is shared once — `ZabbixValue`, `ZabbixItemKey`, `ZabbixClock` and the generated enums — and the shapes differ
and are not forced into one class: the API's `ZabbixHost` is what `host.get` returns (with `select*` relations and
computed fields), a row is what the `hosts` table holds. The bridge between them is the id, never a merged type.

### 5. `Zabbix.Data`, reserved (proposed, not for 1.0)

**Purpose.** An administrator sees Zabbix's database from the PowerShell module instead of installing a database
client, and a developer moves between the API view and the row view of the same object, because it is the same data.

**The model is generated per version** from `create/src/schema.tmpl`, the schema the server is built from, fetched with
the pinned sources (section 6): 203, 207 and 228 tables in 7.0, 7.4 and 8.0, every one with a primary key, and 306, 311
and 338 foreign keys. It is a read-only EF Core model: `ZabbixDatabase`, a `DbContext` with one typed `DbSet` per table
(`db.Hosts`, `db.Items`, `db.HistoryUint`), rows named for the developer (`HostRow`, `ItemRow`).

**One identity, both directions.** The bridge is a generated contract per object kind, `IZabbixHostId { string HostId }`
(and `IZabbixItemId`, `IZabbixTriggerId`, …), implemented by the API record (`ZabbixHost`) and the row (`HostRow`).
Every API or data member that takes a host accepts the contract, so `db.Hosts.FindAsync(host)` (object → row) and
`client.Api.Hosts.GetAsync(row)` (row → object) both work.

**Explicit I/O.** No lazy-loading proxies: a navigation is generated from the schema's foreign keys as an explicit
query, `db.Items.Of(host)`, which reads when it is enumerated and never behind a property. Rows get format views, so a
row table reads like a database grid.

**One connection, two faces.** A server may carry its database: `client.UseDatabase(db)` in C#;
`Connect-ZbxServer -Uri … -ApiToken … -Database '<connection string>'` (or a later `Connect-ZbxDatabase`) in the module,
after which the database cmdlets work without a second handle. The tree gains a database backend for bulk history and
trends reads, where `history.get` pages are the bottleneck.

**The PowerShell face: one verb per view, no per-noun parity.** The API has 64 objects and the schema 207 tables in
7.4, many with no API object (`history_uint`, `trends`, `problem_tag`, `housekeeper`, `ids`, `auditlog`), and Zabbix
allocates ids per table, so a bare id cannot pick its table while a piped object (an API record or a row) always carries
its kind. So the verbs are the views — `Get-` the API, `Read-` the database (the SqlServer module's
`Read-SqlTableData`), `Show-` the frontend — and the database face is two cmdlets, not one per noun:

```powershell
Get-ZbxHost web-01 | Read-ZbxData                      # the row; the table is inferred from the object's kind
Read-ZbxData -Table hosts -Id 10084                    # a bare id names its table
Get-ZbxHost web-01 | Read-ZbxData -Table items         # related rows, through the schema's foreign keys
Read-ZbxData -Table hosts -Where @{ status = 1 }
Read-ZbxData -Table items -Id 23296 | Get-ZbxItem      # back: the row carries table and id; ZabbixReference<T> resolves it
Read-ZbxHistory -Item $item -From (Get-Date).AddDays(-1)   # time window and value type; writes ZabbixValue (Send-ZbxData takes it)
```

`-Table` and the `-Where` keys complete from the generated per-version schema. `Read-ZbxHistory` is the one specialised
reader; no cmdlet changes its output type with a switch. C# keeps the typed `DbSet`s and `Of(...)` navigations; the
generic cmdlets are a thin face over them.

**Writes are excluded.** If ever added, only bulk loads of history and trends, behind an explicit opt-in in C# and
`ConfirmImpact = High` in PowerShell; never configuration, which belongs to the API and its validation.

### 6. The Zabbix sources are pinned

`tools/zabbix-sources.lock` pins, per version, the upstream ref and commit on `https://github.com/zabbix/zabbix`, the
official mirror of git.zabbix.com: 7.0 → tag `7.0.31` (`e7da53ee`), 7.4 → tag `7.4.15` (`bd350f54`), 8.0 → tag
`8.0.0rc1` (`dd0d9c26`; no `8.0.0` tag exists on the mirror yet, and the line moves to it once it does).
`tools/fetch-zabbix-sources.sh` makes a shallow, sparse clone of each pinned commit into the ignored `.zabbix-sources/`
(`ui/include/*.php`, `ui/include/classes/`, and `create/src/schema.tmpl` for section 5) and refuses a commit or tag that
does not match. The model's `sources` names each version's ref and commit beside the API version its source declares
(8.0.0rc1 declares `8.0.0`).
`tools/regenerate-schema.sh` fetches, extracts, merges and verifies; it reproduces `schema/zabbix-api.json` byte for byte.
The `schema-drift` workflow (weekly and on demand) fails when the pinned sources no longer give the committed model, so
a moved pin is a reviewable diff.

### 7. Frontend deep links (implemented; independent of `Zabbix.Data`)

`ZabbixFrontend` in `Zabbix.Sdk` maps an object — host, host group, template, template group, item, trigger, graph,
discovery rule, web scenario, dashboard, map, problem, event, maintenance, user, user group, proxy, network discovery
rule, action, media type, script, service, SLA, and later a `Zabbix.Data` row — to its page in the frontend. The base is
the API endpoint without `api_jsonrpc.php`, or `ZabbixOptions.FrontendUri` (`Connect-ZbxServer -FrontendUri`) where the
frontend is elsewhere; the route is the server's version's. `client.GetFrontendAsync()` pairs the base with the version
the server reports, and `UriFor(target, view)` takes one currency, `ZabbixPageTarget`, to which every API record with a
page converts implicitly (`UriFor(host)`, `UriFor(item, ZabbixPageView.Graph)`; a bare id is
`new ZabbixPageTarget(kind, id)`). The routes are a table in C#, one entry per (kind, view) and version, read from the
pinned frontend sources (`CRouter.php`, `CControllerPopup.php`, the controllers' inputs, the legacy pages'
`check_fields`) and opened in each version's signed-in frontend by `LiveFrontendTests`. What the sources showed:

- 7.4 and 8.0 address the configuration dialogs directly (`zabbix.php?action=popup&popup=host.edit&hostid=…`); 7.0
  serves the same forms only as JSON its lists open by script, so a 7.0 link to a template, item, trigger,
  maintenance, proxy, network discovery rule, action, media type, script or SLA form is its list filtered to the
  object's name (and host). The host, host group, template group, user and user group forms are pages in 7.0.
- Items, triggers, graphs, discovery rules and web scenarios carry the frontend's `context` (host or template), read
  from the record's `selectHosts` (a template is a hosts row with status 3); an action carries its event source; a
  problem or event page is `tr_events.php`, the trigger's.
- Legacy pages that remain: `history.php` (an item's graph and values), `tr_events.php`, `sysmap.php` (the map editor),
  `httpconf.php` (web scenarios, all three), `graphs.php` (7.0), `host_discovery.php` (discovery rules, 7.0 and 7.4;
  8.0 has `lldrule.edit`).

In the module, one generic command: `Get-ZbxHost web-01 | Show-ZbxPage` opens the page in the browser (the piped
object's kind picks the page; the URL is handed to the shell as `Start-Process` does, and a session with no browser
gets a warning and the URL); `-View` picks another page, `-Kind` and `-Id` name an object without a record, and
`-PassThru` writes the URL instead of opening it, so no `Get-ZbxPageUrl` and no `Show-` per noun.

## Consequences

- A consumer of the API takes `Zabbix.Sdk` (no TLS stack); a sender or getter takes `Zabbix.Wire` (no HTTP pipeline);
  a host takes `Zabbix.Agent`, which brings the wire and not the API client. `Zabbix.Sdk.dll` went from 8,492,032 to 8,125,440 bytes (Release),
  its direct package references from 10 to 9.
- Seven packages ship: `Zabbix.Abstractions`, `Zabbix.Api`, `Zabbix.Sdk`, `Zabbix.Wire`, `Zabbix.Agent`,
  `Zabbix.Proxy`, `Zabbix.Agent.Storage.EntityFrameworkCore`. All public surfaces are unshipped, so the moves and
  deletions break nothing released; the changelog names every removed type and cmdlet. (That changelog is in the
  maintainer's archive; the published CHANGELOG.md starts at the first release, [ADR 0005](0005-documentation-set.md),
  amendment.)
- The model's provenance is a file in the repository, not prose: updating Zabbix is a one-line lock change, a
  regeneration and a reviewed model diff in one commit.
- The `Read-` and `Show-` verbs are reserved for the database and frontend views; no current cmdlet uses `Read-` except
  `Read-ZbxConnectorStream` (a stream reader in `Zabbix.Proxy`), and `Show-ZbxWatch` is the one console view left.
