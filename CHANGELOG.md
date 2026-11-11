<!--
This Source Code Form is subject to the terms of the Mozilla Public
License, v. 2.0. Copyright (c) 2026 schroeder247. SPDX-License-Identifier: MPL-2.0
-->
# Changelog

All notable changes to Community Zabbix, the .NET SDK and the PowerShell modules, are documented in this file.

The format is based on [Keep a Changelog](https://keepachangelog.com/en/1.1.0/), and this project adheres to
[Semantic Versioning](https://semver.org/spec/v2.0.0.html).

## [Unreleased]

The first release of the SDK and the modules: a .NET 10 SDK and three PowerShell 7.6 modules for Zabbix 7.0, 7.4
and 8.0. The packages are published as `Community.Zabbix.*`, community software for Zabbix; each assembly is named after
its package (`Community.Zabbix.Sdk.dll`), and the namespaces and telemetry names are `Community.Zabbix.*`.

### Added

- **The API client** (`Community.Zabbix.Sdk`) ([ADR 0013](docs/adr/0013-builders-verbs-and-typed-values.md)).
  `new ZabbixClient("https://zabbix.example/", token)` takes an address and a token, a `NetworkCredential` or a
  `ZabbixCredential`; `ZabbixClient.CreateBuilder()` sets more (`WithEndpoint`, `WithToken`, `WithLogin`, `WithCredential`,
  `WithTimeout`, `ConfigureResilience`, `ConfigureHttpClientBuilder`, `Configure`, `Bind`, `UseLogging`, `UseTimeProvider`) and
  validates it in `Build()`; and `AddZabbixClient("prod")` registers a named client and returns an `IZabbixClientBuilder` with
  the same verbs, written once for both builders, which `IZabbixClientFactory` returns. A client from the container or the
  factory is the container's: disposing it where it's used leaves it running, and the container disposes it. The
  constructors, the builder and the registration build the client through one registration, so its HTTP pipeline and
  resilience are the same whichever way it's made. One generated client per API
  object (`zabbix.Hosts`, `zabbix.Items`, …); the JSON-RPC transport over `IHttpClientFactory` and the standard resilience
  handler (a read is retried on a transient failure, a write never is); API tokens or a session login that renews itself,
  which `LogOffAsync` ends (`user.logout`, as `Users.LogOffAsync`; `user.login` is `Users.LogOnAsync`); the client's options
  are `ZabbixClientOptions`, and a verb and the option it sets share a name (`WithMaxConnectionsPerServer`,
  `WithMaxResponseContentBytes`);
  `ZabbixApiException` with a classified `Kind`.
- **A per-version API model** (`Community.Zabbix.Api`). Every object record, enum, parameter set and request, generated at build
  time from a description extracted from the Zabbix 7.0, 7.4 and 8.0 sources. A member carries the versions that have
  it, and a call the connected server's version does not support throws `ZabbixVersionException` before anything is
  sent. On 8.0 the removed `*.massupdate` methods and `hostinterface.replacehostinterfaces` are emulated.
- **Typed items** ([ADR 0012](docs/adr/0012-typed-bcl-fluent-api.md)). `ZabbixItemKey<T>` declares a key with its type, and
  `ZabbixItemKeys` is the typed catalogue of Zabbix's own keys, whose choices are enums of their own (`ZabbixAverageWindow`,
  `ZabbixCpuLoadScope`, `ZabbixFileTypes`, `ZabbixProcessState`, `ZabbixTcpService`, `ZabbixDnsRecordType`, …). Sends, reads,
  polls and agents' answers take them.
- **A fluent API** ([ADR 0012](docs/adr/0012-typed-bcl-fluent-api.md)). `zabbix.Hosts.GetAsync(q => q.Search.VisibleName = "web")`,
  `zabbix.Hosts.CreateAsync("web-02", [groupId])`, `zabbix.Items[id].DeleteAsync()`, `zabbix.Hosts["web-01"].GetAsync()` and
  `zabbix.HostGroups["Linux"].Hosts["web-01"]`. Writes take a lambda, as reads do
  ([ADR 0013](docs/adr/0013-builders-verbs-and-typed-values.md)): a create from the fields it requires also takes one for the
  rest, `zabbix.Hosts.CreateAsync("web-02", [groupId], h => h.Tags = [("env", "prod")])`, and every node of an object the API
  updates, by name or by id, has `UpdateAsync(h => h.Status = ZabbixHostStatus.Disabled)`, which fills in the object's id. A
  host's and a host group's node is one type whether its id or its name reached it (`zabbix.Hosts[10084]`,
  `zabbix.Hosts["web-01"]`). A
  request's required fields are `required` members, which its constructor takes, so `CreateAsync(new("web-02", [groupId]) { … })`
  works too, and its other fields have setters; an update's id is required, `graph.update`'s and `graphprototype.update`'s
  included. Nested values need no `new`: a tag or a user macro converts from a pair, and `ZabbixInterface.Agent`, `Snmp`, `Ipmi`
  and `Jmx` make a host's interfaces from an `IPAddress` or a DNS name, the first of each type the main one.
  `zabbix.HostInterfaces.CreateAsync(hostId, ZabbixInterface.Snmp(address))` adds one to a host that exists. Every
  asynchronous member takes an optional `CancellationToken` last, on every overload: `zabbix.Hosts.GetAsync()`,
  `GetAsync(stoppingToken)` and `GetAsync(q => q.Limit = 10, stoppingToken)`
  ([ADR 0013](docs/adr/0013-builders-verbs-and-typed-values.md)). Public signatures use BCL types: `ulong` ids,
  `DateTimeOffset` times, `DnsEndPoint` nodes, `Func` callbacks, and `NetworkCredential` for a login.
- **Typed values where the API takes text** ([ADR 0013](docs/adr/0013-builders-verbs-and-typed-values.md)). A get's `Output`, its `Select*`
  options and its `Sort` take the fields of the record they return, a `ZabbixField<TRecord>`, so
  `q.Output = [ZabbixHostFields.HostId]` doesn't compile in an item's query; `ZabbixOutput.Extend` fits every output and
  select, `new ZabbixField<ZabbixHost>("a_new_field")` names a field the SDK doesn't know, and a
  `Zabbix*Fields` class holds the fields of every record an output, a select or a sort names. The sort is one list, each field
  ascending unless it says otherwise, `q.Sort = [ZabbixHostFields.VisibleName.Descending(), ZabbixHostFields.HostId]`, sent as
  `sortfield` and `sortorder`. A get's reply is always its records: its parameters leave out what would change its shape
  (`countOutput`, `groupCount`, `groupBy`, `preservekeys`, a select's `count`), and `CountAsync` counts. An item's, an item
  prototype's and a discovery rule's update interval is a `ZabbixInterval`,
  which converts from a `TimeSpan` (`Delay = TimeSpan.FromMinutes(5)` sends `5m`) and parses macros and flexible and
  scheduling intervals; so is the interval an active agent is assigned (`ZabbixItemAssignment.Delay`) and the one a proxy's
  configuration holds (`ZabbixProxyConfigItem.Delay`), and `GetNextCheck` answers when an agent checks it. An item's history,
  trends and timeout, and a discovery rule's timeout, are a `ZabbixDuration`, from a `TimeSpan` (`History = TimeSpan.FromDays(90)`
  sends `90d`) or a macro. An interface's port is a `ZabbixPort`, from an `int` or a macro. A trapper item's allowed hosts are
  a list of `ZabbixPeer`, each from an `IPAddress`, an `IPNetwork`, a DNS name or a macro, sent as Zabbix's comma-separated
  text, and `ZabbixListenerOptions.ParseAllowedPeers()` reads a listener's list as the same entries. Each reads back what the
  server sends. The PowerShell modules keep their text parameters (`-Delay 5m`, `-History 90d`, `-InterfacePort 10050`,
  `-TrapperHosts '10.0.0.0/8,::1'`, `-SortField` and `-SortOrder`) and parse them.
- **The tree and the watcher.** A host, `zabbix.Hosts["web-01"]` or `zabbix.Hosts[10084]`, its items,
  `host.Items["system.uptime"]`, and host groups answer `GetLatestAsync`, `GetHistoryAsync` and `Watch`; a group's node
  (`zabbix.HostGroups["Linux"]`) also reads, updates and deletes the group, and reaches its hosts by name; a typed read takes
  its key, `host.GetLastAsync(ZabbixItemKeys.Cpu.Load())`; every `Get…Async` throws `ZabbixNotFoundException` when there's
  nothing to return, and its `Find…Async` returns `null`; `IZabbixWatcher` streams the history collected since its last read.
- **The framed-TCP wire** (`Community.Zabbix.Wire`, a package of its own: an API consumer takes no TLS stack). `ZabbixWireClient`
  sends values, reads an item from an agent and asks a server, with PSK and certificate TLS and the HA node walk.
  `new ZabbixWireClient("zbx-server-01")` takes a server; `ZabbixWireClient.CreateBuilder()` sets more (`WithServer` for the one
  server or HA cluster, which a later call replaces, `WithToken`, `WithPsk` with the key as bytes, `UseLogging`) and validates it
  in `Build()`; and `AddZabbixWireClient("prod")` registers a named client with the same verbs, written once for both builders,
  which `IZabbixWireClientFactory` returns ([ADR 0013](docs/adr/0013-builders-verbs-and-typed-values.md)). A client keeps its own
  copy of the options it's built with. A client from the container or the factory is the container's: disposing it where it's
  used leaves it running, and the container disposes it. `ZabbixWireLink` is
  the connection beneath it, a node list and its TLS shared across clients, whose exchange a caller can wrap or use for a request no
  client models (`AsExchange(ZabbixExchangeOptions.AcceptLargeReplies)` for a proxy's configuration). An agent's reason for a
  refusal is `Error`, on `ZabbixAgentRefusedException` and `ZabbixItemNotSupportedException` as on `ZabbixAgentAnswer`, and
  `ZabbixItemKey.Parse("app.disk.free[/]")` reads a key. Every call takes an optional token last, and the clients' virtual
  members let an application fake them in its tests.
- **Agents** (`Community.Zabbix.Agent`, and `ZabbixPassiveAgent` in `Community.Zabbix.Proxy`)
  ([ADR 0013](docs/adr/0013-builders-verbs-and-typed-values.md)). `ZabbixPushAgent.CreateBuilder("web-01")`,
  `ZabbixActiveAgent.CreateBuilder("web-01")` and `ZabbixPassiveAgent.CreateBuilder("web-01")` build an agent on a container of
  its own, validated in `Build()`, and `AddZabbixPushAgent("web-01")`, `AddZabbixActiveAgent("web-01")` and
  `AddZabbixPassiveAgent("web-01")` register the same classes as hosted services and return a builder with the same verbs,
  written once for both. The name is the agent's identity and its host name unless `Configure` or `Bind` sets another.
  `AddServer` adds a destination, the nodes of one call (`"zbx-1;zbx-2"`, or `DnsEndPoint`s) being one HA cluster;
  `AddPoll(key, interval, read)` and `AddItem(key, answer)` take a delegate whose shape says what it needs (a value at hand,
  the key's parameters, a read that waits on I/O, or a service from the agent's container, resolved in a scope of its own), the
  key's type fixing the value's; `AllowPeer` takes an `IPAddress`, an `IPNetwork` or a DNS name; `UseStore`,
  `UseMemoryStore` and `UseEntityFrameworkStore` choose the store; `AddHealthCheck` is on all three. `UseLogging`,
  `UseTimeProvider` and `UseMeterFactory` are on the standalone builders. An active and a passive agent of one name answer
  the same items. A send loop with a store between collection and delivery, Zabbix's `ServerActive` grammar with HA
  failover, flexible and scheduling item intervals (`ZabbixInterval`). The classes keep the `()`, `(options)` and
  `(options, loggers, time, meters)` constructors for an application that sets the options itself.
- **Listeners** (`Community.Zabbix.Proxy`) on Kestrel's socket transport: the trapper, the passive agent
  (`ZabbixPassiveAgent`), the active and passive proxy (`ZabbixActiveProxy`, `ZabbixPassiveProxy`), and the connector
  endpoint (`MapZabbixConnector`). Every listener requires an explicit `AllowedPeers` list.
  `ZabbixListener.CreateBuilder()` builds a listener of your own on the socket transport (`UseSocketTransport(port)` or an
  `IPEndPoint`), with `OnRequest` for a handler or a conversation, and validates it in `Build()`; its constructors take a
  transport of your own.
- **Proxies** ([ADR 0013](docs/adr/0013-builders-verbs-and-typed-values.md)). `ZabbixActiveProxy.CreateBuilder("proxy-01")` and
  `ZabbixPassiveProxy.CreateBuilder("dmz-proxy")` build a proxy on a container of its own, validated in `Build()`, and
  `AddZabbixActiveProxy("proxy-01")` and `AddZabbixPassiveProxy("dmz-proxy")` register the same classes and return a builder
  with the same verbs. `WithServer` sets the one server or HA cluster an active proxy reports to, as Zabbix's `Server` does
  (`ZabbixProxyOptions.Server`, Zabbix's spelling): a later call replaces it, and a comma is refused, as Zabbix refuses one;
  `WithVersion` sets the version announced, a `System.Version` or Zabbix's spelling (`"8.0.0rc1"`), which a passive proxy
  requires; `WithPsk` sets the proxy's own key as bytes; `OnTask`, `OnConfig` (a `ZabbixProxyConfigVerdict` accepts or refuses
  a configuration) and `OnStats` set what answers the server, and `AddProvider` adds a page of the application's own values;
  `AddHealthCheck` checks either proxy. The verbs a passive agent, a push agent or a listener has mean the same on a proxy and
  are written once for all of them: `AllowPeer` and `AllowPsk` on every builder of a listener, `UseStore`, `UseMemoryStore` and
  `UseEntityFrameworkStore` on a push agent's and a proxy's, and `AddTrapper(port, configure)` taking `ZabbixListenerOptions`
  on both; a push agent's trapper intake holds the agent's `MaxItems`. The proxies keep the `()`, `(options)` and
  `(options, loggers, time, meters)` constructors for an application that sets the options itself. `ZabbixTrapper` and
  `ZabbixTrapperSource` take `(…, loggers, time, meters)`, and the listener's and the trapper source's lifecycle methods take
  an optional token. A proxy's records take the API's enums (`ZabbixHostStatus`, `ZabbixItemStatus`, `ZabbixItemType`,
  `ZabbixItemValueType`, `ZabbixSeverity`), and a configuration row's `Mode` chooses an active or a passive proxy.
  `ZabbixProxyUplink.UploadAsync(page, backlog, proxyDelay)` takes a `ZabbixProxyBacklog` and a `TimeSpan`.
- **Every entry point without a container** ([ADR 0007](docs/adr/0007-container-free-runners.md),
  [ADR 0010](docs/adr/0010-agent-proxy-client-classes.md)).
  `new ZabbixClient("https://zabbix.example/", token)` takes the frontend's address
  (`ZabbixClientOptions.GetApiEndpoint` appends `api_jsonrpc.php`, in `AddZabbixClient` and a bound configuration too), and
  `ZabbixClient.CreateBuilder()` takes a logger factory and a clock (`UseLogging`, `UseTimeProvider`); a `NetworkCredential`
  logs in with a session.
  `ZabbixWireClient`, the agents' `AddServer`, and the wire client's and the active proxy's `WithServer` read `host` or
  `host:port`. Every agent and proxy derives from `ZabbixComponent`, one lifetime with no host: `StartAsync`, `StopAsync`,
  `RunAsync`, `DisposeAsync` and `StopTimeout`. An agent or a proxy from the container is the container's: a caller's
  `DisposeAsync` leaves it running, and the container stops and disposes it. A constructor only captures and `Options` is read at start, where
  `StartAsync` throws `InvalidOperationException` saying what to set; the `(options, loggers, time, meters)` constructor
  takes the application's logging, clock and meters. An agent's builder starts what its container hosts, such as a
  trapper, after the agent and stops it before. The PowerShell modules' agent sessions build the same classes with their
  builders.
- **Your application's metrics.** `AddMeter` sends what a `System.Diagnostics.Metrics` meter records: an instrument's name is the item
  key, a counter sends its running total, and a gauge and an observable instrument send what they last said.
- **Item providers and plugins** ([ADR 0008](docs/adr/0008-item-providers-and-plugins.md),
  [ADR 0010](docs/adr/0010-agent-proxy-client-classes.md)). `ZabbixItemProvider` in
  `Community.Zabbix.Abstractions` lists a set of keys and answers them, so a library implements keys against the
  contracts alone. The active and the passive agent each own one table of keys, `Items`, a `ZabbixItemDictionary`: `Add`
  takes an answer that gives text, a typed key's delegate, or a provider, and refuses a name held; the indexer
  replaces; and at start the built-in keys fill the names left free. `Items.Add`, `Items.Register` and `Items.AddPlugins`
  return `Items`. A key name is one provider's. The builders' `AddItem` and `AddItems` register the same, written once for
  both agents over `IZabbixItemsBuilder`, and `AddItem` replaces a provider's key. `Community.Zabbix.Agent.Plugins` loads providers
  from a folder the configuration names (`AddPlugins`): only listed plugins, each in a load context of its own,
  refused when another account can write its files; the agent module's sessions take `-Plugin`, `-PluginPath` and
  `-PluginSetting`. `ZabbixItemDictionary.AnswerAsync` answers a key as an agent would, without a server, as
  `Test-ZbxAgentItem` does.
- **Bundled items** ([ADR 0009](docs/adr/0009-bundled-items.md)). Every active and passive agent answers Zabbix's own
  keys with Zabbix's parameters and values, from the .NET libraries, on Linux the kernel's own files, and a short
  list of operating system calls: the system's names, time, processors, memory, swap and kernel limits, processes, file
  systems, their inodes and block devices, files, their owners and types, and directories, network interfaces, sockets
  and service checks, DNS queries of any record type to any server, `zabbix.stats`, web pages and
  `web.certificate.get`, in public providers by family (`Community.Zabbix.Items`), below every other key
  ([docs/guides/bundled-items.md](docs/guides/bundled-items.md) lists each key's platforms). `system.cpu.util`,
  `proc.cpu.util` and the per-second `vfs.dev` types are sampled once a second, from a key's first request until it goes
  unrequested. `KeyRules` on both agents'
  options are Zabbix's `AllowKey` and `DenyKey` lines (`ZabbixKeyRules`), `system.run` denied unless allowed, and
  `BundledItems = false` leaves the bundled keys out; the agent module's sessions take `-KeyRule` and `-NoBundledItems`.
- **Stores** (`Community.Zabbix.Agent`). `ZabbixStore<T>` is a `ChannelWriter<T>` and an `IAsyncEnumerable<T>` with an
  acknowledged face; `ZabbixMemoryStore<T>`, and `ZabbixEntityFrameworkStore<T>` on EF Core (`Community.Zabbix.Agent.Storage.EntityFrameworkCore`, tested on
  SQLite and PostgreSQL, and chosen with `UseEntityFrameworkStore`), which keeps a leased batch until the server acknowledges
  it. `DrainAsync` and `ZabbixSendLoop.RunAsync` run the send loop without an agent, each with an optional token. A
  `ZabbixSendOutcome` says what became of a batch, and a `ZabbixStoreDropReason` says why a store didn't keep a
  record, as the `zabbix.store.reason` tag of `zabbix.store.dropped` spells it.
- **Configuration.** `AddZabbixClients`, `AddZabbixAgents` and `AddZabbixProxies` bind servers, agents and
  listeners from any configuration source.
- **Telemetry.** Activity sources and meters named under `Community.Zabbix.` (one subscription, `Community.Zabbix.*`), durations in
  seconds, the OpenTelemetry RPC conventions; every log event a `[LoggerMessage]` with a fixed id; secrets and item keys
  (whose parameters can carry a credential, `ZabbixDataClassifications.ItemKey`) classified for `AddZabbixRedaction`.
- **The `Community.Zabbix` module.** 267 cmdlets over `Community.Zabbix.Sdk` and `Community.Zabbix.Wire`: a per-runspace connection context (`Connect-ZbxServer`,
  `Get-ZbxContext`), generated `Get-`/`New-`/`Set-`/`Remove-` cmdlets for every API object, `-Filter` taking the typed
  `.get` parameters, `-AdditionalProperty` on every `New-`/`Set-`, hashtables that bind to any request record, name wildcards,
  `ShouldProcess` on every change, the `Send-ZbxData`, `Get-ZbxAgentItem`, history, watch, export and import commands, format views, and
  help with examples and six `about_` topics (and `about_Community.Zabbix.Agent` in the agent module). The
  modules program against the SDK's public API, as an application does; beyond it they share only the telemetry and
  error-type plumbing of `Community.Zabbix.Diagnostics`.
- **Frontend page links.** `ZabbixFrontend` (`client.GetFrontendAsync()`) gives the web frontend's page of any API
  object (`UriFor(host)`, `UriFor(item, ZabbixPageView.Graph)`, `UriFor(new ZabbixPageTarget(kind, id))`) by the
  routes of the server's own version, read from the 7.0, 7.4 and 8.0 frontend sources and opened in each version's
  frontend by a live test; `ZabbixClientOptions.FrontendUri` names a frontend that is not at the API's address.
  `Show-ZbxPage` opens the page of each piped object in the browser, or writes its address (`-PassThru`).
- **A model with a pinned provenance.** `tools/zabbix-sources.lock` pins the Zabbix commits the API model is extracted
  from (the tags 7.0.31, 7.4.15 and 8.0.0rc1, the latest 8.0 tag when the model was extracted); `tools/regenerate-schema.sh`
  reproduces `schema/zabbix-api.json` from them byte for byte, and the `schema-drift` workflow checks it weekly and on
  every pull request that changes the model, the extractor or the pin.
- **TLS on every wire command.** `Send-ZbxData`, `Get-ZbxAgentItem`, `Get-ZbxQueue`, `Get-ZbxServerStatus`,
  `Test-ZbxItem`, `Test-ZbxExpression`, `Test-ZbxMediaType`, `Send-ZbxProxyData`, `Receive-ZbxProxyConfig`,
  `Start-ZbxActiveProxy` and `Start-ZbxActiveAgent` take one parameter set named after Zabbix's
  configuration: `-Tls` (`TLSConnect`), `-PskIdentity`, `-Psk` (a `SecureString` or a key file), `-Certificate`,
  `-CertificateKey`, `-CertificateAuthority`, `-CertificateRevocationList`, `-ServerIssuer` and `-ServerSubject`; the
  listeners `Start-ZbxPassiveAgent` and `Start-ZbxPassiveProxy` take the same credentials with `-TlsAccept`, which
  defaults to the kinds the credentials given allow. `Connect-ZbxServer` takes the same set for its trapper
  (`-TrapperTls`, `-TrapperPsk`, …), used by every command that reaches the trapper and is given none.
- **`Disconnect-ZbxServer` logs a login session out** (`user.logout`) before it returns.
- **The `Community.Zabbix.Agent` module** (requires the same version of `Community.Zabbix`): active and passive agent sessions with scriptblock
  item handlers, and agent-host provisioning.
- **The `Community.Zabbix.Proxy` module** (requires the same version of `Community.Zabbix`): active and passive proxy sessions, the proxy protocol
  builders and `Read-ZbxConnectorStream`. Both listener modules carry Kestrel's socket transport for every platform pwsh
  7.6 is released for (Windows, Linux and macOS on x64, x86 and Arm), so their listeners start on each.
- **Property-based tests.** The item key, the frame header, `ServerActive`, the update interval, `ZabbixClock`, the
  JSON records and the PSK reader are held to their round-trip and ordering laws over generated inputs (CsCheck), and
  the frame reader is fuzzed with mutated frames and random bytes (`docs/TESTING.md`, "Property tests").
- **No continuation callbacks.** `ContinueWith` (every overload on `Task` and `Task<TResult>`) is a banned symbol in
  library code: a continuation is an `await` in an async method, so it never runs on `TaskScheduler.Current` and its
  fault is observed.
- **Tests wait on signals.** The test projects have their own banned-symbol list (no sleeps, no polling delays, no
  local-clock reads), and both suites run under a hang dump.
- **Build gates.** Every asynchronous member takes an optional cancellation token last, on every overload (ADR 0013,
  section 6), so the libraries suppress RS0026 and RS0027 for it, with that reason; the conformance tests hold the rest of
  what the two rules held, generated members and hand-written ones alike: no other parameter of an overload is optional unless
  it's on the one longest overload, and a public constructor takes no default argument, its shorter forms being overloads of
  their own that call the longest.
  CA1000, CA1720 and CA1812 are errors, answered by a `[SuppressMessage]` with its reason on each symbol a
  framework instantiates or Zabbix names; CA1307 is an error, and the CA1806/IDE0051/IDE0052 results rule covers every
  package.
  `Task.Run` and `BlockingCollection<T>` are banned in every library (RS0030); the module's recorded cmdlet and
  completer bridges and the session login are the only suppressions, each pointing at its decision.
- **A Native AOT publish gate.** CI publishes a console that composes the API client, an agent with a trapper
  listener and the wire client, constructed, registered and built, with `PublishAot` and fails on any trim or AOT warning,
  then runs it.
- **Three operating systems in CI.** CI's `build` job builds the solution and runs both test suites on Linux, Windows
  and macOS.
- **A complexity limit.** `CodeMetricsConfig.txt` sets CA1502 to 15, and the rule is an error in the files of
  `ZabbixProxyConfig.Merge`, `ZabbixItemKey.TryParse` and `ZabbixListenerOptions`'s validation, which are kept below it.
- **A reproducible build.** `global.json` pins the SDK exactly (10.0.403, no roll-forward: the lock files record the SDK's implicit packages, so CI's locked restore needs the very same patch), `LangVersion` is 14 and
  `AnalysisLevel` is `10.0-all`, and the Kestrel files the listener modules carry come from the runtime packs of one
  pinned version (`Directory.Packages.props`), so a newer SDK cannot change what a tagged commit builds to.
- **A compatibility policy for the generated surface** (`docs/design/decisions.md`): what adding or dropping a Zabbix
  version, a `words.json` rename or an enum change costs in SemVer after 1.0.
- **A major-only `AssemblyVersion`** (1.0.0.0 for every 1.x, the full version in `FileVersion`), so modules built
  against different MINORs bind the one copy PowerShell loads; the assemblies are not strong-named (net10.0 only).
- **A dependency floor policy**: a floor moves only for a fix or an API the code uses, never for a newer patch alone.
- **Supply-chain gates.** Every project restores into a committed `packages.lock.json` and CI restores in locked
  mode; `nuget.config` clears machine sources and maps every package to nuget.org; NuGet audit covers transitive
  packages at the lowest severity.
  Every workflow action is pinned to a commit SHA; the release builds with read-only permissions in one job and
  publishes from another, with a build-provenance attestation per package and module zip and nuget.org Trusted
  Publishing instead of a stored API key.
  Each package carries an SPDX 2.2 SBOM (`Microsoft.Sbom.Targets`), and the release attaches it beside the package
  under the same attestation; transitive packages are locked, not pinned (`docs/design/decisions.md`).
  The package-validation baseline restores from nuget.org: `ZabbixPackageValidationBaseline` names the release to
  diff against from the first change after one is published.
- **Trademark and licence notices.** Every package README and description, every module manifest's description,
  `about_Community.Zabbix`, the README, CONTRIBUTING, SECURITY and the documentation site's footer carry Zabbix's trademark
  sentence and the affiliation statement, worded identically (`DesignConformanceTrademarkTests`).
  `THIRD-PARTY-NOTICES.md`, packed into every package and copied into every module folder, credits the Zabbix sources
  the API model is extracted from and reproduces the licences of BouncyCastle, the Microsoft libraries, OpenTelemetry
  and Polly, which the modules redistribute. The model's source notes cite the Zabbix files by line in the project's
  own words instead of quoting their PHP. `docs/design/decisions.md` ("Zabbix's trademark and licences") records the
  position and the decisions on the names and the icon.

### Known limitations

- Zabbix's Windows-only sources (registry, performance counters, services, WMI, event logs), its log file keys and a few
  keys with no source in the framework are not bundled; a template that uses them gets those keys' reasons
  (`docs/guides/bundled-items.md`).
The main ones; [docs/KNOWN-LIMITATIONS.md](docs/KNOWN-LIMITATIONS.md) lists them all and explains each.

- Periods of the week stay seconds (an SLA schedule's periods); Zabbix's `0` for "not
  yet" reads as `ZabbixClock.UnixEpoch`.
- Fourteen API fields stay untyped `JsonElement?` (free-form JSON whose shape Zabbix does not
  fix); `ZabbixWireClient.GetStatsAsync`/`GetStatusAsync` also answer `JsonElement`.
- Version gating is at run time only (`ZabbixVersionException`); there is no compile-time version analyzer.
- The 8.0 emulation of the removed mass-update methods is not atomic (reads, then one batched write).
- The passive proxy answers every `active checks` request in full; agent sessions are in memory and do not survive a
  restart.
- A durable store's `TryWrite` is accepted, not committed: `WriteAsync` waits for the commit, which the store's own loop
  makes asynchronously, batching what was written meanwhile ([KNOWN-LIMITATIONS](docs/KNOWN-LIMITATIONS.md)).
- The tree has no trends read (`TrendsAsync`) and no template or proxy nodes.
- `Copy-ZbxHost` retargets graph items and trigger expressions, but not a calculated item's formula that names the
  source host.
- The modules' TLS parameters take certificate, key, CA and CRL files only, not an `X509Certificate2` or a
  certificate store.
- Requires .NET 10 (`net10.0` only) and PowerShell 7.6 or later.
- The 8.0 model is extracted from Zabbix's 8.0.0rc1 release (the latest 8.0 tag when it was extracted) and verified live against a
  master build with that API.
- The APM objects (`apmlog`, `apmmetric`, `apmspan`, `apmtrace`) have SDK clients and no cmdlets.

[Unreleased]: https://github.com/schroeder247/community-zabbix/commits/main
