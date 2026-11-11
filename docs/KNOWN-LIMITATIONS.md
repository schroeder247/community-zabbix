<!--
This Source Code Form is subject to the terms of the Mozilla Public
License, v. 2.0. Copyright (c) 2026 schroeder247. SPDX-License-Identifier: MPL-2.0
-->
# Known limitations

These are the known gaps and deliberate limits of the alpha, before 1.0.0.

## Platform

- **.NET 10 and PowerShell 7.6 or later.** The libraries target `net10.0` only. The modules need pwsh 7.6, the first
  PowerShell on .NET 10. Windows PowerShell 5.1 is not supported.
- **Zabbix 7.0, 7.4 and 8.0.** The API model is extracted from these three versions' sources, at the tags
  `tools/zabbix-sources.lock` pins: 7.0.31, 7.4.15 and 8.0.0rc1. The 8.0 model comes from Zabbix's 8.0.0rc1 release,
  as no 8.0.0 tag exists yet; the weekly `schema-drift` workflow reports any API change when the pin moves to a later
  8.0 release. The live tests run 8.0 on a master build from after the rc1 tag, with its API
  ([TESTING.md](TESTING.md)), as no 8.0.0rc1 image is published. Older servers are not supported.
- **Certificate mode on macOS needs an RSA certificate.** .NET on macOS stops at TLS 1.2, and Zabbix's default TLS 1.2 ciphers
  are RSA only, so an ECDSA certificate fails the handshake there. Zabbix accepts one over TLS 1.3, which .NET uses on Linux and Windows.

## The API model

- **Periods of the week are seconds.** Every instant is a `DateTimeOffset` and every time of day (a maintenance period's
  and a report's `start_time`) a `TimeOnly`, but an SLA schedule's `period_from`/`period_to` (seconds of the
  week) and a time-unit text (`delay`, `history`, `ttl`) keep the API's form. Zabbix writes `0` for an instant that has
  not happened (an item's `lastclock` before its first value, a token's `expires_at` that never comes): that reads as
  `DateTimeOffset.UnixEpoch`, not as `null`.
- **Times round to 100 ns.** Zabbix stores nanoseconds and a `DateTimeOffset` holds 100 ns ticks, so a public time, a
  PowerShell `-Clock` and `-Ns` round to that. The SDK keeps the exact instant when it reads history, watches, and forwards a value.
- **Sixteen fields are untyped JSON.** Fields whose shape Zabbix does not fix (for example free-form parameter bags and
  fields whose content depends on another field's value) are `JsonElement?`; they are the fields marked
  `"kind": "json"` in `schema/zabbix-api.json`. Two of them hold an entity of `configuration.importcompare`'s answer
  (`ZabbixConfigurationChange.Before` and `After`): it is a piece of the import format, not an API object.
- **Some statistics stay as the server sent them.** `ZabbixServerStatistics` types what a server and a proxy of every
  supported version answer alike. A server's HA nodes and proxies, its values-per-second limit and trend cache, and a
  proxy's buffer are in its `AdditionalFields`, because they come whole from another component or change between
  versions.
- **The wire's requests number the API's enumerations.** `Community.Zabbix.Wire` doesn't reference the API's records, so
  `ZabbixItemTestItem.ItemType`, `ZabbixReportTestRequest.Period` and their kind are the numbers Zabbix uses:
  `(int)ZabbixItemType.Calculated`.
- **Version gating is at run time.** A method or field that the connected server's version does not have throws
  `ZabbixVersionException` before the request is sent. There is no analyzer that reports such a call at compile time,
  and the exception is not counted in the API's metrics, because no request was made.
- **A read record names what its get returns.** A column of the object's table that the get never returns for that
  object (a host's write-only pre-shared key, the hosts table's host-only columns on a template) is not a member; a
  field the server sends that a record does not name is still readable through its `AdditionalFields`.
- **No database view.** The SDK reads Zabbix through the API and the wire only. A read-only database model
  (`Community.Zabbix.Data`, generated from the server's schema) is designed and not built
  ([ADR 0006](adr/0006-protocol-split.md)).
- **A record is only as immutable as its lists.** The generated records compare their lists, dictionaries and JSON by
  content, and their list members are `IReadOnlyList<T>`: a record built over a list its caller keeps and changes
  (`HostGroups = groups`, then `groups.Add(…)`) changes with it, its equality and hash included. Build a record over a
  list nobody changes afterwards (a collection expression copies), or copy it in.

## Bundled items

- **Not every Zabbix key is answered.** Windows' registry, performance counter, service, WMI and event log keys, the log
  file keys, and a few with no source in the framework are not supported unless a handler, a provider or a plugin answers
  them, so a Zabbix template that uses them gets those keys' reasons. [The bundled items guide](guides/bundled-items.md)
  lists them, and each key's platforms.
- **Windows reports processor use only in total.** `system.cpu.util` answers `all` and `system` there, and
  `system.cpu.load` is not answered.
- **A handler that blocks its thread cannot be cut off.** An agent stops waiting for a handler at the check's deadline
  when the handler awaits something; one that never returns from a synchronous call holds that check until it does.

## Behaviour

- **A server can drop a proxied value for a brand-new item.** A proxy can receive an item before the server's configuration cache
  holds it. Until the cache's next `CacheUpdateFrequency` refresh, the server drops values for that item and answers `success`, so
  the proxy can't tell or send them again. Measured on 7.0.31, 7.4.15 and the lab's 8.0 build
  ([details](guides/proxies.md)).
- **The 8.0 mass-update emulation is not atomic.** Zabbix 8.0 removed `host.massupdate`, `template.massupdate`,
  `hostgroup.massupdate`, `templategroup.massupdate` and `hostinterface.replacehostinterfaces`. The SDK emulates the
  mass updates with reads followed by one batched write, so a concurrent change between the read and the write can be
  overwritten; `replacehostinterfaces` is one `host.update` followed by a read of the interfaces' ids.
- **The tree has no trends read.** `ZabbixHostNode` and `ZabbixItemNode` read latest values and history, not trends;
  call `client.Trends.GetAsync` directly. There are no template or proxy nodes.
- **Certificate files are read once.** A wire client reads its CA, certificate, key and CRL files at its first request to
  each peer, and a listener, an agent or a proxy when it starts, as upstream reads them at start; a renewed certificate or
  CRL needs a new one. The private key
  read from the key file lives as long as that object and is released when it is disposed; a PowerShell wire command
  releases it when its call ends, and an active proxy session when it stops.
- **TLS checks what upstream checks.** No host-name check and no online revocation in certificate mode
  ([SECURITY.md](../SECURITY.md)).
- **Node deadlines are per node.** A framed-TCP request to an HA pair applies its timeout to each node it tries, so the
  worst case before a failure is the number of nodes times `ZabbixConnectionOptions.Timeout`.
- **A reply is capped at 16 MiB.** A wire client or an active agent reads a reply of at most
  `ZabbixConnectionOptions.DefaultMaxPayloadBytes` (16 MiB, the listeners' own default), declared or inflated, where upstream
  reads up to 1 GiB; a proxy's configuration keeps upstream's 16 GiB. The SDK runs inside an application, so a peer
  that declares a gigabyte would cost the application that much memory. Set `MaxPayloadBytes` to 1073741824 for upstream's
  bound.
- **A poll's unread records go with it.** At stop, records a poll collected and its reader never took are dropped,
  counted and logged (event 3007); the value the agent's pump holds and what a trapper's edge still holds when the drain
  window closes are counted abandoned (event 3008).
- **A trapper is handed a writer that says what it kept.** The trapper answers `failed` for a value its writer did not
  keep. A store says so, and so does a channel in `Wait` mode (full is `false`); a channel in a drop mode answers `true`
  for a value it drops, so a trapper over one would report a lost value as processed.
- **A durable store's `TryWrite` means accepted, not committed.** `ZabbixPersistentStore<T>` (and so `ZabbixEntityFrameworkStore<T>`) makes every
  backend call on its own loop, never on a caller's thread or under a lock, and commits what was written meanwhile as
  one transaction. `WriteAsync` returns when the record is committed and throws the backend's fault; `TryWrite`
  (synchronous by the `ChannelWriter<T>` contract) returns once the policy accepted the record, and one whose commit then
  fails is dropped and counted `faulted`. A proxy's trapper writes durably: it answers `processed` only for committed values. A push agent's trapper
  (`AddTrapper`) writes to an in-memory intake buffer first, so it can answer `processed` for a value a crash then loses.
  An acknowledgement leaves the count at once and the table on the loop's next turn, so one acknowledged just before a
  crash is leased again by the next process. A proxy's upload carries ids the server de-duplicates by `last_id`; an
  agent's sender values carry none, so the server may receive that value twice. After a restart the lease face
  is empty until the loop has opened the table and loaded ahead of the cursor (`WaitToLeaseAsync` waits for it), and
  `Count` counts only this process's writes until then.
- **A refusal a peer can repeat is logged once an hour.** A listener logs a refused connection (6003) once per peer and
  reason, a failed one (6002) once per peer and failure kind, and a proxy a refused value (7020) once per host, key and
  reason, each per hour (past 1,024 or 4,096 distinct ones, at the 1st, 10th, 100th, …); every occurrence is still counted
  on `zabbix.listener.rejected_connections` (a failed connection on `zabbix.listener.connection.duration`, by its
  `error.type`) and `zabbix.proxy.values`. A dashboard on the counters sees all of them; the
  log shows each distinct problem.
- **Item keys are kept out of logs by default.** A logged item key is `ZabbixDataClassifications.ItemKey`: a plain logger
  drops it, and the SDK's redactor (`SetZabbixRedactors`) keeps its name and masks its parameters (`web.page.get[***]`)
  until the application sets another redactor for that class (a pass-through on a trusted sink); a key's bare name
  (event 3211) is not classified. `SetZabbixRedactors` is in `Community.Zabbix.Abstractions`, so every package's
  application sets it the same way, and it reads a value with the logger's discriminator on or off.
- **A peer's text is marked, not shown.** A server's, proxy's or agent's own explanation (an API error's `data`, a
  refusal's `info`) is `ZabbixDataClassifications.PeerText`, because Zabbix echoes the request into it: under
  `SetZabbixRedactors` it reads `[redacted]` (`ZabbixPeerTextRedactor`). A pass-through redactor for the class shows it
  on a trusted sink.
- **A listener refuses credentials it would not use.** A pre-shared key or certificate set on a listener whose `TlsAccept`
  does not include it fails validation at start, which upstream allows when `TLSAccept` is present.
- **One `ZabbixEntityFrameworkStore<T>` per table.** Two stores (or two processes) on one table lease the same rows, and one's
  acknowledgement removes rows the other still holds: a store owns its table.
- **A memory store's cap counts what is not yet leased.** `ZabbixMemoryStore<T>`'s bound is its channel's, so a leased page
  that is not yet acknowledged is held on top of `MaxItems`; a durable store counts it.
- **A login session ends on `LogOffAsync` or an asynchronous disposal.** A `ZabbixClient` with a
  `ZabbixSessionCredential` sends `user.logout` from `LogOffAsync` (a failure is thrown) and from `DisposeAsync` (best
  effort: a failure is logged at Debug, event 4007). The synchronous `Dispose` sends nothing, and the session stays on
  the server until its idle timeout ends it. A client from a container is the container's: its session ends when the
  container is disposed.
- **Proxy and agent sessions are in memory.** The proxy answers every `active checks` request in full instead of
  tracking each agent's configuration revision (upstream answers an unchanged revision with an empty reply), and the
  agent sessions of the proxy and of the active agent do not survive a restart: each `ZabbixActiveAgent` starts a
  new random session id, so a restart starts a new session on the server. A passive proxy requires
  `ZabbixProxyOptions.Version`, because the server never tells a passive proxy its own version.
- **A proxy forwards raw values.** The proxies carry what agents and senders send them to the server as it arrived:
  they do no preprocessing, poll nothing themselves (no interface checks, low-level discovery or web scenarios), and
  keep a value's item, time, value and state only. A log item's read position is not kept (an agent's active checks
  are answered with `lastlogsize` and `mtime` 0, so an agent that reconnects reads its log from the start), and a log
  value's metadata (its source, severity and event id) is not forwarded.
- **No rate limiter, and one interval per watch.** Nothing in front of the API limits the request rate per endpoint;
  an application that needs one adds it to the server's pipeline (`AddZabbixClient(...).ConfigureHttpClientBuilder(...)`). The
  standard resilience handler's concurrency limiter admits 1,000 calls at once and queues none: the next call fails at
  once with an `HttpRequestException` whose inner exception is Polly's `RateLimiterRejectedException`
  (`ConfigureResilience(r => r.RateLimiter.DefaultRateLimiterOptions.QueueLimit = …)` changes it). A watch
  plan's interval applies to every node it watches; a node cannot poll at its own interval.
- **A proxy uplink learns the server's version from `proxy config`.** A refused `proxy data` carries no version, so a
  `ZabbixActiveProxy` pulls configuration first; `Send-ZbxProxyData -Version` sets the version for a one-off send.
- **A plugin shares the agent's process and stays loaded.** `Community.Zabbix.Agent.Plugins` loads each plugin into the
  agent's process, with its account, as Zabbix's loadable modules are: a handler that throws answers not supported,
  but a crash on a thread of the plugin's own ends the process. A plugin is not unloaded; replacing one means
  restarting the process. The check that refuses a plugin other accounts can write reads the file mode, so it runs on
  Linux and macOS; on Windows the folder's access list is what keeps it the agent's. Plugins as processes of their own,
  and command items (Zabbix's `UserParameter`), are not provided. Loading plugins rules out native AOT
  ([items.md](guides/plugins.md)).

## PowerShell

- **`Copy-ZbxHost` does not rewrite a calculated item's formula.** The copy is a configuration export and import under
  the new name; its graph items and trigger expressions are retargeted, but a calculated item whose formula names the
  source host (`last(/web-01/key)`) still names it.
- **A network discovery check holds its SNMPv3 passphrases as text.** `New-ZbxNetworkDiscoveryCheck` takes them as
  `SecureString` and writes the SDK's `ZabbixNetworkDiscoveryCheckRequest`, whose passphrase fields are strings (the
  value has to reach the wire). The default view masks them; `$check.Snmpv3AuthenticationPassphrase` reads them.
- **A few enumeration-like parameters are numbers.** The model keeps these fields as integers, so their parameters
  take the number and their help lists the values: `-AuthenticationType` on items, item prototypes, discovery rules and their
  prototypes (Zabbix gives one number two meanings, by item type: 0 password or 1 public key for SSH, 0 none to 4
  digest for HTTP), `New-`/`Set-ZbxConnector -ItemValueType` and `New-`/`Set-ZbxMediaType -TokensStatus` (bit sets no
  define names bit by bit) and `New-`/`Set-ZbxItem -InventoryLink` (the number of a host inventory field).
- **No commands for the APM objects.** `apmlog.get`, `apmmetric.get`, `apmspan.get` and `apmtrace.get` (8.0) have
  SDK clients (`client.ApmLogs`, …) and no cmdlets: a log record and a metric point carry no id, and a span's or
  trace's id is a hexadecimal OpenTelemetry id, not the decimal id the commands' `-Id` takes.
- **Audit records and HA nodes have CUID ids, not numbers.** Zabbix keys `auditlog` and `ha_node` by 25-character
  CUID strings (`ZBX_TYPE_CUID`), so the model types `AuditId`, `RecordSetId` and `HANodeId` as `string`.
  `Get-ZbxHaNode -Id` takes those strings. `Get-ZbxAuditLog` has no `-Id`: it selects by `-User`, `-From`/`-Till` and
  `-Filter` (`-Filter @{ auditids = @('ckr8jg5sa0000k6y5q11vcd2h') }` reads one record).
- **Web scenario commands take ids.** `Remove-`, `Set-ZbxHttpTest` take `-Id` (or a piped object's `HttpTestId`), not
  a name. `httptest.get` has every common get option the SDK resolves names with except `searchWildcardsEnabled`, so
  the model gives the entity none of them, and its commands take only ids.
- **TLS credentials are files.** The wire commands' `-Certificate`, `-CertificateKey`, `-CertificateAuthority` and
  `-CertificateRevocationList` take PEM files, as Zabbix's own `TLSCertFile`/`TLSKeyFile`/`TLSCAFile`/`TLSCRLFile` do;
  an `X509Certificate2` or a certificate store is not taken, because `ZabbixTlsOptions` names files. A key given as
  `-Psk` is held as text by the connection's options for as long as the connection (or session) lives.
- **`-AdditionalProperty` with a `$null` value takes the field out of the request**, whatever a named parameter set it to; it
  never sends the API's `null`.
- **A hand-written change command matches piped properties by name.** A `Get-` reference filter and a generated
  `New-` or `Set-` take from a piped Zabbix record only what is its own (its own object's references, its own id), but
  the hand-written commands still bind any piped property whose name matches a parameter (`Get-ZbxEvent |
  Remove-ZbxTag` binds the event's `Name`). Pipe the object the command changes, or name the parameters.
- **A piped hashtable binds nothing.** PowerShell binds by property name from an object's properties, and a
  hashtable's keys are not properties: `@{ Name = 'h1' } | New-ZbxHost` is refused (`InputObjectNotBound`); pipe
  `[pscustomobject]@{ Name = 'h1' }`, or splat the hashtable (`New-ZbxHost @h`).
- **`-Name` brackets are literal.** `Get-* -Name` takes `*` and `?` wildcards; `[` and `]` match themselves, because
  Zabbix names (item keys, `Proxy [name]` triggers) contain them.
- **Dependency conflicts with other modules.** The module loads `Microsoft.Extensions.*` 10.0 into the default load
  context, because those types are part of the SDK's public surface. If another module has already loaded an older copy
  of one of them in the same session, `Import-Module Community.Zabbix` fails with a `FileLoadException`. Importing `Community.Zabbix` first
  helps a module that only *references* the older version, not one that loads its own copy by path; see "Shared
  assemblies across modules, measured" below. Otherwise use a separate session.

### Shared assemblies across modules, measured

Measured in `pwsh -NoProfile` 7.6.6 (.NET 10) with the three modules staged by `tools/Stage-ZbxModules.ps1` and found
through `PSModulePath`; each row is a test in `tests/Zabbix.PowerShell.Tests/ProcessTests.cs`. The older copy is
`Microsoft.Extensions.Logging.Abstractions` from the 8.0.2 package (assembly version 8.0.0.0); the module carries
10.0.0.0.

| Scenario | Outcome |
| --- | --- |
| The older copy loaded first (`Add-Type -Path`), then `Import-Module Community.Zabbix` | Fails: `FileLoadException: Could not load file or assembly 'Microsoft.Extensions.Logging.Abstractions, Version=10.0.0.0, …'. The located assembly's manifest definition does not match the assembly reference.` Every `Zabbix` command then reports that the module could not be loaded. |
| `Import-Module Community.Zabbix`, then an assembly compiled against 8.0.0.0 | Works: its reference to 8.0.0.0 binds to the 10.0.0.0 already loaded. |
| `Import-Module Community.Zabbix`, then the older copy loaded by path (`Add-Type -Path`, as a module's `RequiredAssemblies` does) | Fails: `FileLoadException: … Version=8.0.0.0 …. Assembly with same name is already loaded`. The default load context holds one copy of a name, whatever the versions. |
| An older BouncyCastle 2.x (2.4.0) loaded first, then `Import-Module Community.Zabbix` | Refused: `FileLoadException: The module needs BouncyCastle.Cryptography 2.7.0 or newer, but the session already loaded BouncyCastle.Cryptography 2.4.0 under the same assembly version 2.0.0.0. …`. Every BouncyCastle 2.x is assembly version 2.0.0.0, so without the check the runtime would bind the module to the older release and fail later on a member it lacks. The import compares file versions of every assembly the module ships that is already loaded under the same assembly version; a patch-level difference is accepted. |
| `Import-Module Community.Zabbix.Agent` (or `Community.Zabbix.Proxy`) without importing `Community.Zabbix` | Works: `RequiredModules` imports `Community.Zabbix` first, and the sibling's references to the shared assemblies resolve from the `Zabbix` folder. |
| `Community.Zabbix.Agent` and `Community.Zabbix.Proxy` in one session | Works: both import over the one `Community.Zabbix`, and each runs its commands. |
| `Remove-Module Community.Zabbix` with a sibling imported | Refused, as a non-terminating error `Modules_ModuleIsRequired`; nothing is removed and the sibling's sessions keep running. |
| `Remove-Module Community.Zabbix -Force` with a sibling imported | `Community.Zabbix` is removed and its context closed, which stops the sibling's sessions too; the sibling stays imported, and `Import-Module Community.Zabbix` afterwards gives it a fresh context. |

The `Resolving` handler `Community.Zabbix` registers for its siblings is process-wide, so it also sees other modules' unresolved
references. It answers only for an assembly of its own closure (`Community.Zabbix.PowerShell.deps.json`) and only at a version at
least the one requested: a module built against a newer copy than `Community.Zabbix` ships is not handed the older one.
