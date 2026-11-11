<!--
This Source Code Form is subject to the terms of the Mozilla Public
License, v. 2.0. Copyright (c) 2026 schroeder247. SPDX-License-Identifier: MPL-2.0
-->
# Design decisions

The SDK's design rules are: platform first (the BCL, Microsoft libraries and .NET extension points before owning
anything); a hand-written type needs a recorded reason; native interfaces implemented all the way down; designed errors;
full telemetry; test-driven. This file records how they apply to Zabbix: the vocabulary, the map from each Zabbix
concept to a .NET type, the decisions taken with their reasons, the shapes tried and rejected, and the measured facts
the decisions rest on. Record a new decision here so it is not re-argued; [the architecture](architecture.md) shows the
resulting shape.

## Precedence

Where a decision, a rejected shape or a convention in this repository's documents disagrees with Microsoft's published
guidance (on Microsoft Learn: the Framework Design Guidelines, the library guidance, the Microsoft.Extensions
documentation, the PowerShell cmdlet development guidelines) or with the shape of the Microsoft libraries that guidance
names, Microsoft's guidance wins and the document is corrected, with the source cited in the corrected passage. The
order is Microsoft's guidance, then this register, the architecture, the binding survey, the platform survey and the
ADRs: a lower document may add to a higher one and never contradicts it. Where the code still differs from a corrected document, the difference is listed under
[Documented divergences, to be closed](#documented-divergences-to-be-closed) until a change closes it.

---

## Vocabulary

- **Zabbix noun first, .NET role on top.** *host*, *item*, *key*, *value*, *clock*/*ns*, *trigger*, *template*,
  *host group*, *proxy*, *proxy group*, *trapper*, *sender*, *active check*/*passive check*, *history*, *trend*,
  *event*, *problem*, *maintenance*, *discovery* (LLD), *autoregistration* keep Zabbix's spelling, then PascalCase /
  `I` / `Async`. Never *device*. Never HTTP words for the framed protocol: it is the *Zabbix protocol header*
  (`ZabbixHeader`), a *frame*, a *request*. *Proxy* is Zabbix's proxy, never a .NET or HTTP proxy in an identifier.
- **Type prefix `Zabbix`; shell noun prefix `Zbx`.** `ZabbixItem`, `ZabbixItemKey`, `ZabbixClient`; `Get-ZbxHost`,
  `Connect-ZbxServer` (shell users know the short noun; a shell noun is not a type name). Every generated type is
  `Zabbix`-prefixed (`ZabbixHost`, `ZabbixItemGetParameters`, `ZabbixHostCreateRequest`). The Framework Design
  Guidelines ask for exactly this qualification of generic names ("DO NOT introduce generic type names such as
  `Element`, `Node`, `Log`, and `Message` ... qualify the generic type names", [names of
  namespaces](https://learn.microsoft.com/dotnet/standard/design-guidelines/names-of-namespaces)). The same page asks
  for a prefix on the namespace, and the root namespace is `Community.Zabbix`: the word the package ids, the
  assemblies and the modules carry ("Zabbix's trademark and licences"), so `Zabbix` alone stays free for the vendor and
  for other libraries, and a developer types the name they installed (`using Community.Zabbix;`).
- **The sample is `ZabbixValue(Host, Key, Value, Time)`.** `Time` is a `DateTimeOffset`, the BCL's instant. The record
  keeps the wire's seconds and nanoseconds internally, because a `DateTimeOffset` has 100 ns ticks and two values 50 ns
  apart would collide on history's `(itemid, clock, ns)` key when the SDK forwards them. Not `Sample` or `Reading`.
- **The root is `ZabbixClient`**: one endpoint and one credential, the client of one Zabbix server. The Azure SDK
  guidelines name the root a caller talks to a service through: "DO name service client types with the Client suffix"
  ([.NET Azure SDK guidelines](https://azure.github.io/azure-sdk/dotnet_introduction.html)), and
  `IZabbixClientFactory` keeps the `IHttpClientFactory` shape. Not `ZabbixServer`: it reads as the server itself, which
  this library never runs.
- **`Watch` is the one word for continuous observation** (`node.Watch(plan)`, `Watch-ZbxItem`). **`Send` is the
  verb on the push seam** (`zabbix_sender` is the tool, `sender data` the request). A proxy *uploads* (`proxy data`).
  A store is **drained** by the sender and **pumped** into by an agent; "block", "link", "post" and "offer" are not in
  the vocabulary.
- **`GetAsync(parameters)` reads, `FindAsync(id)` reads one object or `null` (as `DbSet<T>.FindAsync` does),
  `CreateAsync`/`UpdateAsync`/`DeleteAsync` write; one request answers one id, several answer the list.** The parameters are the wire's own
  words (`Filter`, `Search`, `SortField`, `Output`, `Limit`): `Output` is not `Select`, because the API returns fields,
  it does not project. There is no LINQ-style query provider over them (ADR 0006): it would compile the same small
  grammar a second way, and the typed parameters already serve every consumer.
- **`As*` is lazy, `To*` materialises, `Try*` never throws.**
- **Push-plane nouns:** a **listener** accepts (`ZabbixListener`), a **trapper** takes values, an **uplink** is a
  proxy's side of the server conversation (`ZabbixProxyUplink`), a **feed** is the connector's stream, a **wire** is
  the server's own request set (`ZabbixWireClient`). `ZabbixProxyClient`, `ZabbixConnector` and `ZabbixHostClient` are
  generated API clients, so a protocol façade never ends in `Client` and never takes an API object's bare name.
- **Two TLS vocabularies, two types.** `ZabbixTlsMode` is how the SDK's sender connects (one mode);
  `ZabbixTlsAcceptModes` is the generated `[Flags]` enum of Zabbix's 1/2/4 bits, so a listener's `TlsAccept` is a set
  (`psk,cert` is `Psk | Certificate`), enforced per connection.
- **A set is plural, a choice is singular.** A `[Flags]` enum is named in the plural (`ZabbixTlsAcceptModes`,
  `ZabbixPasswordCheckRules`, `ZabbixEventActions`, `ZabbixDiscoveryKinds`) and has `None = 0` where the API accepts
  the empty set (`passwd_check_rules` 0; `tls_accept` 0 is refused, so `ZabbixTlsAcceptModes` has none); a single-code
  enum is singular (`ZabbixHostPrototypeInterfaceMode`, not `...CustomInterfaces`). Every flags member is one bit or a
  union of the others (ZBX0008 otherwise).

## The domain-to-type map

**Mated**: the platform type is the model. **Owned**: no platform type fits, and the reason is written at the type.

| Zabbix object (source) | Type | Status |
| --- | --- | --- |
| ZBXD frame (`comms.c`) | `ZabbixHeader` on `PipeReader`/`IBufferWriter`, `BinaryPrimitives`, `ZLibStream`; upstream's validity rules; a reply capped at 16 MiB by default (upstream: 1 GiB) | mated |
| server node `host[:port]`, `[v6]:port` (`ip.c`) | `DnsEndPoint`, which has no parser, so `ZabbixEndPoint.Parse` and `Format` read and write the grammar | BCL |
| HA node list (`commshigh.c`) | `ZabbixNodeLink`: connect-phase failover, sticky node, walked over the declared node list as upstream walks it | owned |
| `;` HA nodes versus `,` destinations (`cfg.c`) | Text, as Zabbix writes it; `ZabbixEndPoint.ParseServerActive` reads it | BCL |
| item key `name[p,"q",[a,b]]` (`expr.c`) | `ZabbixItemKey`: the verbatim text is the identity, parameters a view | owned |
| item delay, flexible and scheduling (`interval.c`) | `ZabbixItemDelay` + a due `PriorityQueue` on `TimeProvider` | owned |
| clock + ns | `DateTimeOffset` in every public signature, with an internal exact instant behind it; every `(clock, ns)` pair of a record is one member and every Unix time a `DateTimeOffset` of its second (`DesignConformanceTests.Clocks`) | BCL |
| a date (`API_DATE`, `YYYY-MM-DD`) | `DateOnly` | mated |
| a time of day (seconds after midnight, a `start_time`) | `TimeOnly` | mated |
| object id (`bigint unsigned`; the API's decimal string, the framed protocol's number; `0` for none) | `ulong(ulong)`: `ISpanParsable`, `IUtf8SpanParsable`, `ISpanFormattable`, `IUtf8SpanFormattable`; one type on the API's records, the wire's rows and the hosts' assignments; `0UL` is `0`; a CUID id (`auditid`, `recordsetid`, `ha_nodeid`: a 25-character string, `ZBX_TYPE_CUID`) is a `string`, never a `ulong` | owned (a `ulong` carries the number but neither JSON form nor the one name across packages) |
| API version | `System.Version`; methods gated by `ZabbixSupportedAttribute`, refused with `ZabbixVersionException` | mated |
| proxy and server component version | `ZabbixComponentVersion` over `System.Version`, upstream's parse rules | owned parse, mated value |
| allowed peers / `Server=` / `trapper_hosts` | `ZabbixAllowedPeers`: `IPNetwork` for blocks, names resolved per check, empty refused | owned |
| TLS certificate mode (`tls_openssl.c`) | `SslStream` + `CertificateChainPolicy` + `CustomRootTrust`; revocation from `TLSCRLFile` only | mated |
| certificate issuer/subject pins | `ZabbixCertificateName` over `EnumerateRelativeDistinguishedNames` and `AsnReader` | owned |
| TLS-PSK | BouncyCastle: upstream's TLS 1.3 pair and six TLS 1.2 suites, P-256 only; `ZabbixTlsAcceptor` server side | owned (no PSK in .NET) |
| JSON-RPC envelope (`CJsonRpc.php`) | `JsonRpcTransport` writing `Utf8JsonWriter` | owned |
| JSON-RPC error `{code, message, data}` | `ZabbixApiException` + `ZabbixApiErrorKind` | mated |
| the API model | generated from `schema/zabbix-api.json`, extracted from the 7.0/7.4/8.0 PHP source | mated |
| enum values (`defines.inc.php`), 0/1/2 fields | per-object enums read by `ZabbixEnumConverter<TEnum>` | owned converter |
| `.get` filter/search grammar (`dbFilter`) | the generated `*GetParameters` records (`Filter`, `Search`, `Output`) | generated (`IQueryable` and an expression walker rejected) |
| history paging (`time_from` inclusive; `clock,ns`) | `ZabbixHistoryRead`: a second cursor, saturated seconds read whole, k-way merge | owned |
| trapper frontend requests (`queue.get`, `status.get`) | `ZabbixWireClient`; stats and status as `JsonElement` | mated shape |
| active heartbeat (one-way) | `ZabbixNotificationCallback`, a delegate beside `ZabbixExchangeCallback` | owned |
| passive proxy (conversations) | `ZabbixConversationCallback` owning the connection | owned |
| proxy config (tables, deltas) | `ZabbixProxyConfig`: merged by id, a `FrozenDictionary` per revision | mated |
| a value computed at `proxy data` time; a task; a pushed config; `zabbix.stats` | `ZabbixProxyDataCallback`, `ZabbixProxyTaskCallback`, `ZabbixPassiveProxyConfigCallback`, `ZabbixProxyStatsCallback` delegates | mated |
| upload state | `ZabbixDelivery.Held` | owned |
| store / durable store | `ZabbixStore<T> : ChannelWriter<T>, IAsyncEnumerable<T>` / `ZabbixEntityFrameworkStore<T>` (a table per store) | mated |
| telemetry | `ZabbixLayerTelemetry` per layer on `ActivitySource`/`IMeterFactory`; `[LoggerMessage]` per layer; `DataClassification` + the redactor | mated |
| lifecycle / limits / health / cadence | `IHostedLifecycleService` / `RateLimiting` / `IHealthCheck` / `PeriodicTimer` over `TimeProvider` | mated |
| shell value → SDK record | `ZabbixRecordConverter : PSTypeConverter`, registered by the generated `Community.Zabbix.types.ps1xml` | mated |
| unmodelled wire field on a read record | `ZabbixAdditionalFieldsAdapter : PSPropertyAdapter` | mated |
| the module's reference (an id, a name, a piped object) | `ZabbixReference<TEntity>` over the SDK's `ulong` | owned |

## Decisions

### Groups, templates and proxies are selections, not rungs

A host is in many groups and has many templates, so neither can be a container in a tree without lying about
cardinality. `client.HostGroups["x"].Hosts` is a view over the host set; narrowing preserves the type. *Consequence:* the tree has
three tiers (server, host, item) and no subtree axis.

### The `.get` grammar is the whole query language

`filter` is equality/IN, `search` is substring (a field may take a list of patterns), and `searchByAny` turns both from
AND across fields into OR. There are no comparison operators. *Consequence:* the SDK never returns `IQueryable<T>` over
the API; the typed `*GetParameters` record is the query, and a field it does not name goes nowhere. The field names are
typed too, from the same description: `Zabbix{Object}GetFilter` holds one list per field `filter` accepts, typed as the
read record types the field (`Status = [ZabbixHostStatus.Enabled]`), `Zabbix{Object}GetSearch` one text per field
`search` accepts, and `Zabbix{Record}Fields` the wire name of every read field under its C# name, for `output` and
`sortfield`. *Why a filter is a dictionary:* `Filter` and `Search` stay `IReadOnlyDictionary<string, ZabbixFieldValue>`,
which the typed records implement, so a typed filter is assigned where a dictionary was, and a field the record does not
type (one the API accepts but the description cannot place) is still sent through a dictionary.

### The Zabbix source is the model's authority

`tools/ZabbixApiExtract` evaluates the PHP API's `$api_input_rules`, `defines.inc.php`, `ACCESS_RULES` and
`CApiServiceFactory` per version (7.0, 7.4, 8.0) into `schema/zabbix-api.json`, the generator's one input. *Why:* a
model taken from a previous SDK recompiles its defects faithfully (swapped enums, 0/1/2 fields as `bool`, fields
dropped on 8.0). *Consequence:* a 0/1/2 field is an enum, availability per version is data, and the reason to re-open
is Zabbix publishing a machine-readable schema.

### A read record is what its get returns, named as the frontend names it

A read record's members are the fields its object's get returns: the output rule's or `OUTPUT_FIELDS`, the computed
ones, and under `output=extend` the table's columns the object's own writes take, less what the get takes out of every
object and the columns another object sharing the table owns. *Why:* reading every column of a shared table would give a
template a host's `ProxyId` and `TlsPsk`, a discovery rule an item's `History`, a host a template's `VendorName`: members
that are always empty or never sent, typed and documented as if they meant something. A field the server sends that a
record does not name is still in `AdditionalFields`. Where the wire's word misleads, the member takes the frontend's:
a trigger's `description` is its `Name` and its `comments` its `Description` (`New-ZbxTrigger -Name`, as `Get-ZbxTrigger
-Name` selects by `description`), and a template's `templates` are `ChildTemplates` beside `ParentTemplates`,
while a request's `LinkedTemplates` are the templates it links. *Consequence:* the wire name is on every member
(`JsonPropertyName`) and in its sentence, so the API reference still finds it.

### A field whose meaning depends on another field stays a documented number

An item's `authtype` (and an item prototype's, a discovery rule's and its prototype's) is one wire field with two
vocabularies chosen by the item's `type`: `ITEM_AUTHTYPE_PASSWORD`/`_PUBLICKEY` (0, 1) for an SSH item and
`ZBX_HTTP_AUTH_NONE` to `_DIGEST` (0 to 4) for an HTTP agent item. One enum over both would give 0 and 1 two names each,
which CA1069 forbids ("Enums should not have duplicate values",
[CA1069](https://learn.microsoft.com/dotnet/fundamentals/code-analysis/quality-rules/ca1069)); two typed properties
over one wire field would be two members writing the same JSON name. The field stays an `int`; its documentation and
the cmdlets' help list both vocabularies, and the two enums the API has for them (`ZabbixItemAuthType` on a script,
`ZabbixHttpAuthentication` on a connector and a web scenario) name the values for a caller who casts. A time of day
is not such a field: a maintenance period's and a report's `start_time` are seconds after midnight whatever else the
record says, so they are `TimeOnly` (the `timeofday` kind, `ZabbixTimeOfDayConverter`; `ZabbixTimeOfDayAttribute`
reads a number as seconds in the shell).

### One generator, at build time

One Roslyn incremental generator over the API description emits the records, the clients, the JSON context and the
PowerShell CRUD cmdlets. A source generator's output is invisible to other generators in the same compilation, so the
records live in `Zabbix.Api` and the clients in `Zabbix.Sdk`, and the System.Text.Json context is real source that the
same emitter writes. *Consequence:* no hand-kept manifests, models, builders or CRUD cmdlets: what the generator
cannot derive from the model is reviewed data in `schema/overlay.json`, carried into the description and checked
against it (the object an id refers to, a field required only under a condition, and the cmdlets' own shape: the
objects left to hand-written commands, the builders, the switches, the objects whose visible name sits beside a
technical one, and the property a field takes where its own name would hide a `PSCmdlet` member). Three choices stay
in the generator as code: the `PSCmdlet` members a parameter would hide (a fact of PowerShell), the field a record is
named by (`ZabbixCmdletEmitter.EntityNameField`: a user's `username`, the `host` of the objects in the hosts table,
else `name`), and the order of the objects a `Get-` help example prefers to scope by. A field whose name would hide a
member and that the overlay does not name takes its name followed by `Value`, a rule.

### A list read is one reply

A list `GetAsync` answers `Task<IReadOnlyList<T>>`, read whole and bounded by `ZabbixOptions.MaxResponseContentBytes`;
there is no `IAsyncEnumerable<T>` or pageable form. *Why:* paging needs a cursor, and the `.get` grammar has `limit` but
no offset and no comparison operator, so neither an offset nor an id cursor (`id > last`) can be built over it; a pager
that re-reads with a growing `limit` costs the square of the result. History, the one unbounded read, is paged by the
tree already (by time, `time_from`/`time_till`). *Consequence:* every generated list read says so in its remarks
(`GeneratedApiTests.Every_list_read_says_it_is_one_reply_and_why`); the reason to re-open is the API gaining an offset
or a comparison filter.

### Method sub-clients, not indexers

`client.Hosts["web-01"]` and `host.Item(key)` mint sub-clients (the Azure `GetBlobContainerClient` shape). An indexer must
not do I/O, and minting costs no call. *Consequence:* the first read of a missing name throws
`ZabbixNotFoundException : KeyNotFoundException`, and `ExistsAsync` is the Tester-Doer beside it, at every depth.

### One raw door; the plumbing under it is internal

`ZabbixClient.SendAsync` is the one raw JSON-RPC door. The transport under it, the clients' transport constructors,
the scalar-result readers the generated clients call and the API-error classifier are internal ("do not expose
implementation details", [type design](https://learn.microsoft.com/dotnet/standard/design-guidelines/type)); a test
substitutes a client through its protected constructor and virtual calls, never by handing it a transport.
`ZabbixHttp` stays public: its request-option keys and `IsIdempotent` are what a handler added through
`ConfigureHttpClient` reads to tell a read from a write and see a deadline, the extension point the HTTP pipeline offers.
*Consequence:* `DesignConformanceTests.No_public_member_of_the_api_client_exposes_the_transport`.

### A store is a channel, not a Dataflow block

The push plane is one line, sources → store → wire, with one consumer per edge and no predicate-routed link. A store is
a buffer with a policy, and the platform's buffer with a policy is `Channel<T>`. Both shapes were built to the same
guarantees and measured on the same harness:

| | Dataflow | Channels + `IAsyncEnumerable` |
| --- | --- | --- |
| store, 1,000,000 values in and out | 716–836 ms, 81–99 MB allocated | 252–313 ms, 8.0 MB |
| send, 20,000 batches of 50 | 125–174 ms, 13.6 MB | 177–381 ms, 9.0 MB |
| loss / back-pressure | counted drops; `Wait` postpones | the channel's own `itemDropped` and `FullMode` |
| the public contract | `IPropagatorBlock<T,T>`, a Dataflow type on the Abstractions surface | `ChannelWriter<T>, IAsyncEnumerable<T>`, the BCL's two faces |

The send loop pays a fraction of a microsecond per value for reading one item at a time, below measurement at an
agent's rates. *Consequence:* `ZabbixStore<T>` is a `ChannelWriter<T>` and an `IAsyncEnumerable<T>`; the policy's
`Overflow` is `BoundedChannelFullMode` itself; no project references Dataflow.

### Failover walks the node list as upstream does

`ZabbixNodeLink` fails over only at connect or TLS handshake, keeps the node that answered first, and never re-sends
after the write (`commshigh.c`). *Why:* a retry policy sees only the exception and cannot tell a connect-phase failure
from a reply-phase one; a Polly retry re-sends frames the server has already received (measured: three sends reached
both nodes three times). *Consequence:* delay and backoff stay in the send loop's retry.

### Classify API errors once

`ZabbixApiErrorKind` is derived from the JSON-RPC code and the `data` prefix in one place (`ZabbixApiException`), with
`IsTransient` for the retry gate. *Why:* API mode always answers in English (`initLocales('en_us')`), so the prefix is
exact; classifying once beats loose substring matches in each caller. *Consequence:* a read is retried on a transient
kind; a write never is. `error.type` on the API span is the RPC status code itself, per the RPC semantic conventions.

### Refusals and failures are two families

A caller must tell "the peer said no" from "the peer could not be asked" without knowing every type, so every
exception the SDK throws for a Zabbix outcome is in one of two families. *Why:* without the two bases a caller has to know
every type, and a refusal thrown as an `InvalidDataException` is indistinguishable from a broken transport.

| Type | Base | Thrown when |
| --- | --- | --- |
| `ZabbixRefusedException` (abstract, `Zabbix.Abstractions`) | `Exception` | the base of every refusal: a peer read the request and declined it |
| `ZabbixApiException` | `ZabbixRefusedException` | the API answered with an error object (`Kind` classifies it) |
| `ZabbixTrapperRefusedException` | `ZabbixRefusedException` | the trapper did not answer a counted `success` |
| `ZabbixServerRefusedException` | `ZabbixRefusedException` | the server answered a wire request `failed` |
| `ZabbixAgentRefusedException` | `ZabbixRefusedException` | a passive agent refused the request as a whole |
| `ZabbixProxyRefusedException` | `ZabbixRefusedException` | the server refused a proxy's `proxy config` or `proxy data` |
| `ZabbixWireException` (abstract, `Zabbix.Wire`) | `IOException` | the base of the wire's own exchange failures, which name the endpoint or the redirect |
| `ZabbixSenderIncompleteException` | `IOException` | a send of several frames stopped at a failed one after earlier frames were taken; it carries what was taken and what was not confirmed, the failed frame's exception inside |
| `ZabbixSenderCanceledException` | `OperationCanceledException` | the caller cancelled a send of several frames after earlier frames were taken; it carries the same as the incomplete send, and its `CancellationToken` is the caller's |
| `ZabbixVersionException` | `NotSupportedException` | the server's version does not have the method or field; nothing was sent |
| `ZabbixNotFoundException` | `KeyNotFoundException` | a sub-client's name or key matched nothing |

Everything else is the most specific standard type: `SocketException` (no node reachable), `IOException` (a lost
connection), `AuthenticationException` (a refused TLS handshake), `TimeoutException` (the deadline),
`InvalidDataException` (a reply that is not the protocol), `ArgumentException` and its kin, `ObjectDisposedException`.
Every wire request throws the same type for the same failure: `ZabbixWireClient` rethrows the exchange's own exception,
and a request to the server adds the request and the endpoint to its `Data` (`Zabbix.Request`,
`Zabbix.Node`), which its message does not name ([`Exception.Data`](https://learn.microsoft.com/dotnet/api/system.exception.data)).
The API plane does the same for its resilience pipeline: Polly's `TimeoutRejectedException` surfaces as a
`TimeoutException`, and its `BrokenCircuitException` and `RateLimiterRejectedException` as an `HttpRequestException`,
each with Polly's exception inside, so one `catch (TimeoutException)` serves both planes.
*Consequence:* every concrete public exception is sealed, has the three standard constructors and names its own
`error.type` (`IZabbixErrorTyped`); no library exception derives from `Exception` itself except the refusal base; a
message is a sentence and carries no personal or secret value. `DesignConformanceTests` holds each rule. The family
follows Microsoft's exception guidance: derive from `Exception` or a common base, never from `ApplicationException`, and
use an existing type where it carries the meaning ([using standard exception
types](https://learn.microsoft.com/dotnet/standard/design-guidelines/using-standard-exception-types)); three common
constructors, and messages that are grammatically correct sentences with ending punctuation ([best practices for
exceptions](https://learn.microsoft.com/dotnet/standard/exceptions/best-practices-for-exceptions)). Sealing is the one
rule the guidelines question: "DO NOT seal classes without having a good reason to do so"
([sealing](https://learn.microsoft.com/dotnet/standard/design-guidelines/sealing)). The reason: a concrete exception
here is a classification, not a base. Its type is what a caller's `catch` and the `error.type` on a span both key on,
and both are fixed by the library (`IZabbixErrorTyped`), so a type derived outside would carry the library's
`error.type` for a failure the library never raised and be caught as one. The extension points are the abstract bases
(`ZabbixRefusedException`, and the BCL types the failures derive from), which stay open; nothing the library throws is
ever a derived concrete type, so leaving a concrete one open would offer a caller nothing they can rely on.

### The sender frames as zabbix_sender does

`ZabbixWireClient.SendAsync` sends at most `BatchSize` values (250, upstream's `VALUES_MAX`) and `MaxBatchBytes` bytes a frame,
in order, each when the one before was answered, and stops at the first frame that fails, as `zabbix_sender` stops at a
failed batch. *Why:* ten thousand values in one frame take long enough to hit the deadline, and a partial failure must be
honest: the trapper protocol has no per-call id, so a caller that sent everything again would store the taken frames
twice. *Consequence:* a first frame that fails throws its own exception (nothing was taken); a later one throws
`ZabbixSenderIncompleteException` with the answer for the frames taken and the items not confirmed; a cancellation after a
frame was taken is a `ZabbixSenderCanceledException` that carries the same, still an `OperationCanceledException` as
`TaskCanceledException` is. A send loop's batch goes as one frame (`DeliverAsync`), because its retry resends the whole
batch.

### The connection owns the certificate trust

Certificate mode reads a private key and certificates once, into a trust held by the node link (`ZabbixNodeLink`) or a
listener's acceptor, and whoever builds that connection disposes it: a wire client built from nodes, the agents and
proxies for their links, a listener for its acceptor. A façade over an exchange it was
handed owns nothing. *Why:* the key's handle otherwise lives until the collector finds it. *Consequence:* `ZabbixWireClient` is
`IDisposable`; an exchange taken from a disposed link throws `ObjectDisposedException`.
`ZabbixFrames.Over` still builds a link nobody disposes, for callers that take a delegate per call.

### One host per name

`AddZabbix(name)`, `AddZabbixPushAgent(name)` and their siblings are `(Name, Services)` builders whose verbs write named
options, with one instance of the class, a keyed service, and a hosted service per name. *Why:* registering an unnamed singleton makes a second agent silently run the
first one's registration.

### Each layer owns its log and telemetry

Each layer has a `{Layer}Log` of `[LoggerMessage]` events in an id block recorded on `ZabbixTelemetryNames`, and a
`{Layer}Telemetry` whose one `Start`/`Finish` pair writes the span and the duration together. *Why:* a central catalogue
of events nobody emits is not logging; a conformance test checks that every catalogued event is emitted.

Three rules hold the signals to what a consumer can use. A duration histogram carries `InstrumentAdvice` bucket
boundaries in seconds (5 ms to 60 s, the OpenTelemetry request set plus the wire's 30 s deadline): without them an
OpenTelemetry SDK applies its millisecond defaults and every sub-five-second duration lands in one bucket ([advice for
histograms](https://learn.microsoft.com/dotnet/core/diagnostics/metrics-instrumentation#using-advice-to-customize-histogram-instruments)).
A failure whose exception is handed back to the caller is logged at Debug, because the caller decides what it means and
logs it once; Error is kept for an outcome the library swallows to keep a loop, a listener or a store going ([log
level](https://learn.microsoft.com/dotnet/core/extensions/logging/overview#log-level)); `DesignConformanceTests.EventIds`
lists those events. A peer's free text (an API error's `data`, a trapper's or agent's `info`) echoes the request's
values, so it is classified `ZabbixDataClassifications.PeerText` and written as `[redacted]` (`ZabbixPeerTextRedactor`;
erased by the fallback when no policy names it), and a failed span's
status says the error type, never the message: a span is exported unredacted ([data
redaction](https://learn.microsoft.com/dotnet/core/extensions/data-redaction)). An item key's parse failure names the rule
and the position, never the text.

### A credential never crosses the network in clear

A credential given to a server (an `ApiToken`, a `ZabbixCredential` given to the builder or the constructor) and a
plain `http` endpoint on another host fail validation unless `ZabbixOptions.AllowUnencryptedCredentials` is set; a
loopback endpoint is allowed, as the W3C Secure Contexts specification treats it as trustworthy, and an anonymous client
may use `http`. *Why:* the token or the `user.login` password would cross the network in clear on every call; the
Azure SDK's bearer policy refuses the same. One credential per client: options that carry an `ApiToken` beside a
credential given in code fail, rather than one of the two being dropped.

### Each package owns a block of event ids

An operator filters logs by event id, so an id says which package wrote it and a new event lands beside its package's
others. *Why:* blocks assigned by layer interleave packages (the send loop's events would sit inside the wire's block), so
no range could be filtered to one package. The logging generator requires an id to be unique
within its assembly ([SYSLIB1006](https://learn.microsoft.com/dotnet/fundamentals/syslib-diagnostics/syslib1006)); the
blocks extend that to the family, and an id, once released, keeps its meaning.

| Package | Block | Classes |
| --- | --- | --- |
| `Zabbix.Wire` | 1000–1999 | `WireLog`: 10xx sends, 11xx the exchange, 12xx reads from an agent, 13xx the server's requests |
| `Zabbix.Agent` | 2000–3999 | `StoreLog` 20xx–21xx; `AgentLog` 30xx–33xx (33xx `ZabbixComponent`, the lifecycle of an agent or proxy) |
| `Zabbix.Sdk` | 4000–5999 | `ApiLog` 40xx; `TreeLog` 50xx |
| `Zabbix.Proxy` | 6000–7999 | `ListenerLog` 60xx–63xx; `ProxyLog` 70xx |
| the PowerShell modules | 14000–14999 | `ZabbixPowerShellLog` |

An event on a hot path (every request, batch or tick) is `Debug`, never `Information`: its counts are a meter's. The
`Information` events of the client packages are the rare ones, an API version read, a session renewed, a redirect
followed, a watcher stopped. A failure is logged once, by the façade that made the exchange, at its level; the node
list's own account of it (1106) is `Debug`. *Consequence:* `DesignConformanceTests` holds every event in its package's
block and the client packages' `Information` events to that list. The send loop's outcomes are 3100–3106, the active
agent's conversation 32xx and the connector endpoint's 63xx.

An event's name is its method's name (`LoggerMessageAttribute.EventName` defaults to it), and the catalogue an operator
filters by, each event's id, package, level and name, is `tests/Zabbix.Tests/events.txt`.
`DesignConformanceTests.The_event_catalogue_matches_its_snapshot` compares it with the assemblies and
`Logger_message_event_ids_are_unique_across_the_assemblies` holds the ids unique across the family, so a renamed method,
a moved id or a changed level is a reviewed edit of that file, never a silent change
([logging guidance for library authors](https://learn.microsoft.com/dotnet/core/extensions/logging/library-guidance)).

### A classified log parameter needs the extended generator, in every package

`[ZabbixSecret]`, `[ZabbixPersonal]` and `[ZabbixItemKeyData]` on a `[LoggerMessage]` parameter are honoured only by the
generator in `Microsoft.Extensions.Telemetry.Abstractions`; the shared framework's generator compiles the same
declaration without a diagnostic and logs the value. So every library that declares a classified parameter references
the package, and a conformance test invokes each classified event through an unredacted `FakeLogger` and requires the
value to be absent from the message and the state. *Why:* a library that lacks the package logs the value in clear text
and nothing reports it. *Consequence:* the guarantee holds with no redaction registered (a
plain logger drops the value), and `AddZabbixRedaction` only chooses what a class becomes: `Secret` erased, `ItemKey`
masked to its name by `ZabbixItemKeyRedactor` (`web.page.get[***]`), `PeerText` marked `[redacted]` by
`ZabbixPeerTextRedactor`, anything else erased by the fallback. The policy turns the logger's discriminator off: it hands
a redactor `value:Tag`, which would erase a bare key and make a pass-through print the tag.

`AddZabbixRedaction` stays in `Zabbix.Sdk`. Its two halves are implementation packages: `EnableRedaction` is
`Microsoft.Extensions.Telemetry` (its closure brings the host's abstractions and configuration) and `ErasingRedactor`
and `AddRedaction` are `Microsoft.Extensions.Compliance.Redaction`. The contracts take abstractions only, and the wire
does not need either for its guarantee. The item-key redactor is in `Zabbix.Abstractions`, so an application that takes
only the wire, the agents or the proxies registers the same policy in its own `AddRedaction` call (the example is on
`ZabbixItemKeyRedactor`).

### The listener keeps its own accept loop

`ZabbixListener` runs its own accept loop over Kestrel's `IConnectionListenerFactory` and publishes the connection
instruments itself. *Why:* Kestrel's `ConnectionHandler` pipeline needs `KestrelServer`, composed by a web host; every
trapper, passive agent and passive proxy would then host ASP.NET Core's server. Re-open if Kestrel is shown to run in a
worker host without one.

### Every listener fails closed

`AllowedPeers` is required on every SDK listener, the trapper included, and an empty list fails validation; "everyone"
is written out. This deliberately deviates from the upstream server's trapper, which has no peer list. *Why:* a
listener that accepts values is always told who may send them; upstream's agent and proxy already refuse an empty
`Server=`. The PowerShell listener cmdlets make `-AllowedPeers` required for the same reason. The connector endpoint (`MapZabbixConnector`) takes a
`ZabbixConnectorOptions` that names a bearer token (the connector object's `authtype` bearer, compared in constant
time), a peer list, or `AllowAnonymous` for an endpoint the application's own authorization guards; naming none refuses
the mapping.

The listeners' transport is Kestrel's socket transport registered under the key `ZabbixListener.TransportKey`
(`AddZabbixSocketTransport`), not the unkeyed `IConnectionListenerFactory`. *Why:* in a web application that unkeyed
service and its `SocketTransportOptions` are Kestrel's own; a library that registered them would serve the web
endpoints with its own choice, or be handed a named-pipe or QUIC factory
([keyed services](https://learn.microsoft.com/dotnet/core/extensions/dependency-injection#keyed-services)).

### Durability means "kept until the destination's success"

A durable store keeps a leased batch until the destination acknowledges it and leases it again after a crash with the
same sequences. The send loop reads a store through that acknowledged face (`ZabbixSendLoop.RunAsync(ZabbixStore<T>, …)`,
`ZabbixWireClient.DrainAsync(ZabbixStore<ZabbixValue>, …)`): it leases a batch, acknowledges it once delivered or rejected,
and releases it back to the store when the drain window closes on it. A value the server acknowledged and then lost (a demoted HA node) is not kept. *Why:* `success` is the
only acknowledgement Zabbix sends. A replay window after a node switch is not built.

### Only a token's owner decides that a cancellation is a stop

A library catch that treats an `OperationCanceledException` as "stopped" tests the token it owns
(`IsCancellationRequested`); any other cancellation is the callee's failure. *Why:* an `HttpClient` timeout, or any
callee's own deadline, throws `TaskCanceledException`; taken as a stop, it ends a poll's cadence or a send loop
silently while the health check stays Healthy
([cancellation in managed threads](https://learn.microsoft.com/dotnet/standard/threading/cancellation-in-managed-threads)).
`DesignConformanceTests.Every_cancellation_catch_asks_whose_cancellation_it_is` holds the rule.

### Async meets a synchronous contract at three places, and they stay

`Task.Run` and `BlockingCollection<T>` are banned in every library (`BannedSymbols.txt`, RS0030), and three places use
them, each where a synchronous contract meets the asynchronous SDK. Each carries a `[SuppressMessage]` that names this
decision, and no fourth is added.

1. `ZabbixCmdlet.Start`. PowerShell's cmdlet contract is synchronous: `ProcessRecord` returns `void` and may write only
   from the pipeline thread. The operation starts with `Task.Run`, which gives it an execution context of its own, so
   the ambient `ZabbixPipeline` it enters (an `AsyncLocal`) never leaks onto the pipeline thread and a synchronous
   prefix of the operation never runs there. The pipeline thread drains the operation's writes from a
   `BlockingCollection<T>` with the stop token: a thread that must block until items or completion is exactly
   `GetConsumingEnumerable(CancellationToken)`, where a `Channel<T>`'s reader is asynchronous only and would need the
   sync-over-async wait the banned-API list forbids.
2. `ZabbixNameCompleter`. An argument completer returns its results synchronously; the fetch starts with `Task.Run`
   for the same reason as the cmdlet's.
3. `ZabbixSessionCredential`'s login. The password provider and the login request are caller-supplied code, so the
   login starts on the pool and the caller that started it waits on its own token, as every later caller does, instead
   of running a synchronous prefix of unknown code under its own cancellation.

*Why not await:* there is no asynchronous cmdlet base or completer in PowerShell, and a shared login cannot run under
the first caller's token; each bridge is where its contract meets the SDK, and one bridge per contract keeps every
other line asynchronous. `DesignConformanceTests.Only_the_recorded_bridges_start_work_on_the_pool` holds the list.

### Tests touch nothing external by default

Live tests are opt-in through environment variables; unset, the suite touches no external system. The EF Core tests run
on their own database (`ZABBIX_TEST_POSTGRES`), never a Zabbix database, and on SQLite otherwise. A test double counts
as evidence only when it is shaped by the Zabbix source it stands in for.

### Records compare their collections by value

A record's synthesized equality compares an `IReadOnlyList<T>`, an `IReadOnlyDictionary<TKey, TValue>`, the
`AdditionalFields` bag and a `JsonElement` by reference, so two records read from equal replies, or built from equal
values, would be unequal (337 generated records and seven hand-written ones have such a member). Every record with such a member writes its own
`Equals(T?)` and `GetHashCode()` over `ZabbixValueEquality`: a list element by element in order, a dictionary key by key,
JSON by `JsonElement.DeepEquals`; absent is not empty. The generator emits them; the compiler's `==`, `!=` and
`Equals(object)` call them. *Why not a value-list type* (`ZabbixValueList<T>` with value equality, assigned through each
`init`): the property would still be typed `IReadOnlyList<T>`, so a caller's own list (a collection expression, an array)
would keep reference equality unless every `init` copied it into the wrapper, which costs an allocation per member and
leaks a library type through `GetType()`; equality written beside the record keeps the caller's list as given and is the
shape `ZabbixOutput` and `ZabbixFieldValue` have. *Consequence:* `DesignConformanceTests` builds every public
record twice from equal, separately allocated values and requires them equal with equal hashes, and `GeneratedApiTests`
requires the emitted equality on every record with such a member.

### Generated code meets the same analyzers

Generated code is held to the same rules as hand-written code, including the PublicAPI baseline and the culture rules.
Where a generated shape cannot satisfy a rule, the suppression is a `[SuppressMessage]` with its justification, never a
blanket `#pragma` or an `.editorconfig` severity: per symbol where the reason is the symbol's own (an enum Zabbix numbers
without zero, CA1008; a URL member that holds macros, CA1056; a Zabbix type word, CA1720), and in one counted,
assembly-level file where one reason covers a family (`ZabbixApiSuppressions.g.cs`: CA2227 on every sendable record's
`AdditionalFields`, the settable extension-data bag System.Text.Json requires, as `ProblemDetails.Extensions` is).
`GeneratedApiTests` requires a reason on every one and pins the family's count to what reflection finds. `PublicAPI.Shipped.txt` holds every library's
released surface and `PublicAPI.Unshipped.txt` what changed since, and a conformance test holds that. *Hence no generated-code markers:*
an emitted file starts with a plain `// Emitted by Zabbix.Generator … Do not edit.` header, never `// <auto-generated/>`,
and no type carries `[GeneratedCode]`, and `.editorconfig` sets `generated_code = false` on the generator's output. Each
marker tells the analyzers the code is generated and switches most of them off, which would exempt the larger part of
the public surface from the rules above; `GeneratedApiTests` holds every emitted file to the plain header.

### The generated surface keeps SemVer after 1.0

Most of the public surface is generated from `schema/zabbix-api.json`, `schema/words.json` and `schema/overlay.json`,
so routine model maintenance changes it. Each kind of change costs the SemVer component
[the .NET breaking-change rules](https://github.com/dotnet/runtime/blob/main/docs/coding-guidelines/breaking-change-rules.md)
assign it, and the Azure SDK's rule applies: a member is never removed in a MINOR, it is marked `[Obsolete]` first.

- **Adding a Zabbix version** (8.2) is a MINOR: its new members carry `ZabbixSupportedAttribute`, and a call the
  connected server does not support still throws `ZabbixVersionException`.
- **A field or method a newer Zabbix removed** stays in the model, version-gated, for as long as any supported version
  has it; it is not a removal from the .NET surface.
- **Dropping a Zabbix version** (7.0) is a MAJOR: members only that version had leave the surface.
- **A rename through `words.json` or the overlay** adds the new name in a MINOR and keeps the old one as an
  `[Obsolete]` forwarder until the next MAJOR.
- **An enum gaining members** (an upstream value set grows) is a MINOR; a removed value follows the field rule above.
- **A field moving into or out of an object's `notFields`** changes a record's shape and is a MAJOR unless the old
  member stays as an `[Obsolete]` forwarder.

`PublicAPI.Unshipped.txt` and RS0016/RS0017 show every such change in review; the package-validation baseline (below)
fails the build on a binary break against the last release on nuget.org.

### Assembly identity: a major-only version, no strong name

`AssemblyVersion` is `<major>.0.0.0` and `FileVersion` the full version (`Directory.Build.props`), as the
Microsoft.Extensions packages ship (10.0.0.0 for every 10.0.x) and as [the versioning
guidance](https://learn.microsoft.com/dotnet/standard/library-guidance/versioning#assembly-version) considers: one
PowerShell session loads one copy of each assembly into the default context, and a module compiled against 1.1.0.0 would
otherwise fail to bind the 1.0.0.0 another module already loaded. The assemblies are not strong-named. [The strong-naming
guidance](https://learn.microsoft.com/dotnet/standard/library-guidance/strong-naming) is a CONSIDER aimed at .NET
Framework consumers; these libraries target net10.0 only, where the runtime neither validates nor uses the signature.
*The trigger to revisit:* a .NET Framework or .NET Standard target. If a key is ever added it never changes.

### Dependency floors move for a reason

A package's dependency versions are the floor every consumer inherits. [The dependency
guidance](https://learn.microsoft.com/dotnet/standard/library-guidance/dependencies) says to use the lowest version
that works, so a floor in `Directory.Packages.props` moves only for a fix the SDK needs (a security advisory included,
which `NuGetAudit` reports) or an API the code starts to use, never only because a newer patch shipped. The
Microsoft.Extensions 10.0.x line (the runtime's) and the 10.x line (dotnet/extensions: Resilience, Telemetry,
Compliance) move together with EF Core's 10.0.x, because each 10.x release depends on the matching 10.0.x. Test projects
may run on newer versions than the floor through `VersionOverride`, so the suite still sees current behaviour.

### Transitive packages are locked, not pinned

`CentralPackageTransitivePinningEnabled` stays off. Pinning a transitive package to its central version makes NuGet
write it into the nuspec as a direct dependency at that version (Learn, "Central Package Management", transitive
pinning), so `Zabbix.Wire` would declare `Microsoft.Extensions.Options` 10.0.12 where it inherits whatever
`Microsoft.Extensions.Telemetry.Abstractions` asks for: every package would gain dependencies it does not use and raise
floors the policy above keeps low. The reproducibility pinning would buy is already there: every project restores into
a committed `packages.lock.json`, CI restores in locked mode, and NuGet audit covers the transitive graph. A transitive
package with an advisory is lifted by a direct `PackageReference` in the one project that needs it, with its reason.
Each package's SPDX SBOM (`Microsoft.Sbom.Targets`, packed with `ZabbixSbom=true`) lists the resolved graph.

### PowerShell: secrets are `SecureString`

Every parameter that carries a token, password or PSK (`-ApiToken`, `-Password`, `-Passwd`, `-TlsPsk`, …) is
`SecureString`, decoded only at the point of use. A PSK identity and a URL are not secrets and are `string`.
`Connect-ZbxServer` refuses a URL carrying user info. *Why:* a `SecureString` parameter makes passing a literal secret
impossible to write, not just discouraged.

### PowerShell: `New-` returns the entity; `Set-` is a sink

`New-` cmdlets read the created objects back in one `.get` and write the entity, so `$g = New-ZbxHostGroup …; $g.GroupId`
works. `Set-` writes nothing unless `-PassThru` is given, then reads the changed objects back in one `.get` (Learn,
"Support the PassThru Parameter"). The read-back selects by the entity's own top-level `*ids` option, falling back to
`filter.<idfield>` only when there is none, because `template.get` with `filter.templateid` answers `[]` for a real id.

### PowerShell: confirmation is the engine's

`New`/`Set` cmdlets are `ConfirmImpact.Medium` and `Remove` cmdlets `High`, all through plain `ShouldProcess`. A second
`ShouldContinue` survives only on hand-written commands that fan out to many objects. `-Force` skips the prompt but
never `-WhatIf` or an explicit `-Confirm`; `$ConfirmPreference` is never rewritten. A host that cannot prompt gets one
terminating error. *Why:* the engine already owns confirmation, and a second layer fails a non-interactive host.

### PowerShell: one reference currency, no static state

`ZabbixReference<TEntity>` accepts an id, a name or a piped entity, and binds by property name too; `-Id` is an alias of
`-InputObject` on generated `Set-`/`Remove-`. Every piece of session state lives on the per-runspace `ZabbixContext`
(`ConditionalWeakTable<Runspace, ZabbixContext>`), never a static, so two runspaces never share a client.

### PowerShell: binding through PowerShell's own seams

Input binds through one `PSTypeConverter` registered per record by a generated `types.ps1xml`, on the SDK's own
System.Text.Json contract; unmodelled output fields surface through one `PSPropertyAdapter`; a generated `New-`/`Set-` takes every field parameter by
property name (secrets aside), so a CSV row or a `[pscustomobject]` creates or changes one object per row, and unbinds what
a piped Zabbix record bound that is not its own (`ZabbixPipedRecord`: its own object's references and its own id stay, so
`Get-ZbxHost | New-ZbxItem` takes the host's id and never its description); `-Filter` is the typed
parameters record, not a scriptblock language. [PowerShell binding](powershell-binding.md) has the survey and the
measurements. `-AdditionalProperty` is the last word over named parameters, and two of its keys that resolve to one wire field
with different values are refused. An API-required field is never `Mandatory`: it is checked after the `-AdditionalProperty`
merge, so the `-AdditionalProperty`-only idiom works, and the error names the missing field.

### PowerShell: help is user text built from source

MAML help and one reference page per command are generated at build time from the cmdlets' XML doc comments
(`Zabbix.PowerShell.HelpGen`), with one syntax entry per parameter set. Maintainers' notes live in `<remarks>`. A
hand-written command's examples, and optionally its synopsis and description, are a help script under
`src/Zabbix.PowerShell*/Examples/`, PowerShell's own comment-based help block plus `#region` examples, which a tool runs
against a live server. A generated command's synopsis, description and examples are composed by the generator from the
API object's help words in `schema/overlay.json` (`objects.<object>.help`) and written as its doc comment, so they are
data reviewed once per object, never a template filled from the parameter list. *Why not PlatyPS:* it reads `Get-Help`
from the built module, which has no help yet, so it never sees a doc comment. *Why not synthesize examples from the
syntax:* a parameter list knows neither the object's domain nor what the server requires, so its examples
carry placeholders (`'<name>'`, `value1`); a command with no example from either seam fails the build instead.

### PowerShell: console views without a TUI framework

`Show-ZbxWatch` renders with `PSStyle` (plain ANSI); there is no console graph or dashboard view, because the frontend draws them (ADR 0006). *Why:* Terminal.Gui's
current release is `net10.0`-only with many transitive `Microsoft.Extensions.*` and parser dependencies, which would
collide with the host's assemblies in a PowerShell session, for a view that needs a scrolling table, not windows.

### Zabbix's trademark and licences

The project uses Zabbix's name, reads Zabbix's AGPL-3.0 sources and describes an API that Zabbix documents under a
proprietary licence. Each of the three is governed by a different text, and the project follows each as written. This is
the project's reading of those texts, not legal advice.

**What Zabbix's trademark policy allows without a licence** (<https://www.zabbix.com/trademark>, version 1.1):

- an open-source package that exists to support the use of Zabbix (the policy's own example is an open-source plugin
  "... for Zabbix"), as long as nothing suggests an official link to Zabbix;
- "nominative fair use": text that names Zabbix to say what it talks about, and "zabbix" inside source-code identifiers
  (namespaces, type names, cmdlet nouns);
- the unaltered logo, standalone, for non-commercial use with no implied official link.

**What needs a licence:** a use that suggests an official link or endorsement; "Zabbix" combined with one of the
policy's listed words ("agent", "proxy", "server", "services", "solution", "support", "development", and others) in a
name; "Zabbix" shortened or made into an acronym; "Zabbix" in a domain name; the logo altered, inside another logo, or
on product packaging (a package icon is packaging); and any case where the policy's reading is in doubt. Requests go
through <https://www.zabbix.com/forms/legal_request>.

**How the project complies:**

- *The notice.* The policy asks every use of the mark to carry its sentence. Every artefact that ships or is shown to
  users carries "This project is not affiliated with, endorsed by or sponsored by Zabbix LLC. Zabbix is a registered trademark of Zabbix LLC." word for word: each package's README and description (one source, `ZabbixTrademarkNotice` in
  `Directory.Build.props`), each module manifest's description, `about_Community.Zabbix`, the README, CONTRIBUTING, SECURITY,
  THIRD-PARTY-NOTICES and the documentation site's footer. `DesignConformanceTrademarkTests` fails an artefact that
  lacks it or a tracked document that words either sentence differently. The policy names Zabbix LLC; the US
  registrations name Zabbix SIA, the Latvian parent. The notice uses the policy's own sentence; which entity it should
  name is the open question below.
- *The wording.* Nothing calls the project official, certified, approved or endorsed. The repository description and the
  documentation site call it unofficial.
- *The logo.* The README shows the Zabbix logo unaltered and standalone, 200 pixels wide (the policy's floor is 70),
  beside "works with Zabbix 7.0, 7.4 and 8.0" and above the notice, which the policy allows for non-commercial use with
  no implied official link (`assets/third-party/README.md` records its source). It is never a package or module icon:
  the package icon is the project's own, not the logo.
- *The sources* (AGPL-3.0 from 7.0). Reading them at development time is unrestricted, and the project ships none of
  them: `tools/fetch-zabbix-sources.sh` fetches the pinned commits into an ignored folder. What reaches the repository
  and the packages is facts (names, types, required flags, lengths, defaults, enumeration values, versions, wire
  formats), each with the file and line it was read from. A `_source` note, a source comment or a field sentence
  cites a Zabbix file and line and says what the code does in the project's own words. It never pastes PHP or C code,
  a comment or a message (CONTRIBUTING, "Design standard").
- *The documentation* (proprietary, all rights reserved; reuse "in whole or in part" needs Zabbix's written consent).
  The XML documentation, the PowerShell help and the reference pages are written in the project's own words; facts
  are taken from the sources and the public API reference, and no documentation text is reproduced.
- *The credits.* [THIRD-PARTY-NOTICES.md](../../THIRD-PARTY-NOTICES.md) credits the Zabbix sources and reproduces the
  licences of the components the modules redistribute (BouncyCastle and the Microsoft libraries under MIT, OpenTelemetry
  under Apache-2.0, Polly under BSD-3-Clause). Every package and every module folder carries it.

**The names, the identifiers and the icon:**

- *Names.* The package ids and the module names lead with `Community.`: the packages `Community.Zabbix.Abstractions`,
  `Community.Zabbix.Api`, `Community.Zabbix.Sdk`, `Community.Zabbix.Wire`, `Community.Zabbix.Agent`,
  `Community.Zabbix.Proxy`, `Community.Zabbix.Agent.Storage.EntityFrameworkCore` and `Community.Zabbix.Agent.Plugins`
  ([ADR 0002](../adr/0002-package-ids.md), amendment), the modules `Community.Zabbix`, `Community.Zabbix.Agent` and
  `Community.Zabbix.Proxy` ([ADR 0003](../adr/0003-powershell-module-split.md), amendment). `Community` means community
  software for Zabbix, in the sense of Ansible's `community.zabbix` collection: free, nothing for sale, one maintainer, no
  company behind it (a description of the project, not of its users: the MPL-2.0 allows commercial use). It is not a product or company name, and the `Community.` prefix is not reserved on NuGet (other
  owners publish under it); the project accepts that. No request goes to Zabbix: the names stay inside what the policy
  allows without a licence.
- *Type names keep `Zabbix`.* The `Zabbix`-prefixed type names (`ZabbixClient`, `ZabbixHost`) and the `Zbx` cmdlet
  noun prefix (`Get-ZbxHost`) stay: the policy names identifiers of the `Get-ZabbixHost` kind as nominative fair use, and
  `Zbx` is the short noun prefix shell users already know (vocabulary above). Everything a user installs, loads or
  imports carries the full word after `Community.`: the package and module names, each assembly, named after its package
  (`Community.Zabbix.Sdk.dll`, the modules' `Community.Zabbix.PowerShell.dll`), the root namespace
  (`using Community.Zabbix;`) and the telemetry names (`Community.Zabbix.*`; [ADR 0002](../adr/0002-package-ids.md),
  amendments).
- *The icon.* Every package carries the project's own picture, a white "C" ring with a green node on navy
  (`assets/icon.png`; [ADR 0004](../adr/0004-conventions.md), amendment). It has no "Z" and does not use Zabbix's
  colours. The Zabbix logo appears only in the README, unaltered and standalone ("The logo" above).

**The documentation, and one open question:**

- *The documentation stays independent.* Every field sentence states the facts in the project's own words; no
  consent is sought. `tools/check-doc-wording.py`, a step of the weekly `schema-drift` workflow, compares every
  `summary` in `schema/overlay.json` with the API reference of each supported version (object, `get` and commentary
  pages, fetched into a cache outside the repository; Zabbix's text is never committed). It fails on any sentence or
  clause of six words or more that equals the documentation's wording or comes within a difflib ratio of 0.85 of it.
  It allows short factual statements of five words or fewer ("ID of the host."), which can be put only a few ways,
  and the units `tools/doc-wording-allow.txt` lists with a reason (the list is empty).
- *The entity (open).* Which entity the notice names (Zabbix LLC, as the policy says, or Zabbix SIA, as the registrations say).

## Rejected shapes

Shapes that do not fit this SDK, each with the reason and what is used instead, so they are not proposed again.

| Shape rejected | Why | Instead |
| --- | --- | --- |
| an own logger façade (`IZabbixLogger` with console, null and ring-buffer implementations) | a third-party façade in a library; strings, not structure; no event ids | `ILogger<T>` + `[LoggerMessage]` per layer; one `ILoggerProvider` in the module |
| sync twins on every service; `Dispose()` doing a network logout | a second body per verb; a blocking dispose | async only; one bridge in the cmdlet base; `LogOutAsync` ends a login session in view, `DisposeAsync` ends it as a best effort, `Dispose` leaves it to expire |
| a hand-composed `HttpClient` with own retry and auth handlers | `Microsoft.Extensions.Http.Resilience` answers this exact chain | `AddHttpClient` + `SetHandlerLifetime(Infinite)` + `AddStandardResilienceHandler` |
| configuration mutated by a builder's `With*` and validated at `Build()`; positional DI overloads | configuration a file binds belongs to the options pattern: a builder's state is not bindable, reloadable or named ([options pattern](https://learn.microsoft.com/dotnet/core/extensions/options)) | one options graph, `[OptionsValidator]`, `ValidateOnStart`, named options. A builder whose product is an **object** stays a Microsoft shape: `ChatClientBuilder.Use(...).Build()`, `HostApplicationBuilder.Build()`, and the DI registration builder's `WithName`/`WithCredential` in `Microsoft.Extensions.Azure` ([`ChatClientBuilder`](https://learn.microsoft.com/dotnet/api/microsoft.extensions.ai.chatclientbuilder), [`WithName`](https://learn.microsoft.com/dotnet/api/microsoft.extensions.azure.azureclientbuilderextensions.withname)); a store is built by its constructor from the policy record and the services |
| mutable model classes with public `List<>`/`Dictionary<>` properties and an active-record back-reference | a mutation surface on a read model | records, `IReadOnlyList<T>`, no back-reference |
| an `I*Service` interface per API object | the contract is authored by Zabbix; an interface duplicates it | the Azure shape: one generated client per API object whose service methods are virtual ("DO make all service methods virtual", `dotnet-mocking-virtual-method`, [Azure SDK .NET guidelines](https://azure.github.io/azure-sdk/dotnet_introduction.html)). The root `ZabbixClient`, the tree's nodes, the generated `ZabbixApi` and every generated client have protected parameterless constructors and virtual members; a read record's `init` members are its model factory (`dotnet-mocking-factory-builder`) |
| CRTP builders with one-line setters, `Build()` returning a builder, sync-over-async inside | `record` + `init` + `with` are the builder; `Build` is terminal | request records whose `required` members the constructor takes and whose other members a lambda sets ([ADR 0013](../adr/0013-builders-verbs-and-typed-values.md), section 4) |
| reflective string-to-number and string-to-enum converters | System.Text.Json's number handling covers numbers; reflection is not AOT-safe | the source-generated context; one generic enum converter |
| process-global static id generator, static `Meter`/`ActivitySource`, a reflection cache under `lock` | static state is wrong in a DI-aware library | per-client counter with an echo check; `IMeterFactory` |
| error classification by English substring in each caller | loose and repeated | one classification from the code and the `data` prefix |
| six copies of connect → TLS → write → read, six failover loops, several PEM loaders and accept loops | one transport written six times | one framed-TCP transport on `System.IO.Pipelines`; façades over it |
| `while (!ct) { … await Task.Delay }` with wall-clock scheduling and backoff inside the tick | untestable time; a retry inside a scheduled tick | `PeriodicTimer` over `TimeProvider`; fail the tick, count it, keep the cadence |
| a connection limiter on `SemaphoreSlim`, unlimited by default | PSK handshakes each pin a thread | `ConcurrencyLimiter` + leases |
| `event EventHandler<>` hooks and settable `Func<…>` properties on agents and proxies | not a callback list, not events | the component *is* the stream; faults are completion; counts are the meter |
| hand-trimmed `ConcurrentQueue`s, `List<T>` + `lock` buffers, `InMemory`/`File`/`Sqlite` stores with own locks; Dataflow storage blocks | hand-rolled bounded queues; three durabilities for one concept | one `ZabbixStore<T>`; `ZabbixEntityFrameworkStore<T>` for durability |
| TLS modes as `string` on many option types | a fixed vocabulary is an enum | `ZabbixTlsMode`, `ZabbixTlsAcceptModes` |
| a process-wide static PowerShell session, watcher registry and graph host | two runspaces share one client | a per-runspace context |
| cancellable runs as `Task.Run(sync)` + `Wait(token)` over a path passing `CancellationToken.None` | Ctrl+C abandons an in-flight write | the async API with the stop token |
| `[ValidatePattern('^\d+$')]` on every id and `-GroupIds`/`-HostGroups` twins merged by hand | a parsed value is its own type | `ulong : IParsable<ulong>`; `ZabbixReference<T>` |
| hand-written hashtable coercion in the module | a second copy of the wire contract, which drifts from it at every nesting level | `ZabbixRecordConverter` over the SDK's contract |
| builder commands (`New-ZbxActionCondition` and the others generated from the overlay's `cmdlets.builders`) writing typed records instead of hashtables | a hashtable keyed by the wire names goes as it is into an `Invoke-ZbxApi` body and binds onto any record of the same shape through `ZabbixRecordConverter`; a typed record would need a conversion for each and lose the raw call | builders keep writing wire-keyed hashtables; the typed record is what the converter makes of one where a parameter is typed |
| JSON manifests restating the parameter types; three generators, none at build time | every list rots | one Roslyn generator over the API description |
| proxy config deltas replacing the snapshot | a delta erases every host it does not name | merge by id, `del_hostids`, a frozen snapshot per revision |
| `InvariantGlobalization=true` on a library | an application runtime switch; on a library it silences the culture analyzers | not set; culture rules at error |

## Measured facts

Re-measure a fact when .NET or Zabbix changes.

### Platform

| Fact | Consequence |
| --- | --- |
| .NET 10 has no TLS-PSK API (PSK appears only in `TlsCipherSuite` names) | BouncyCastle for PSK |
| on Linux, `X500DistinguishedName.Name` and `EnumerateRelativeDistinguishedNames(true)` are in OpenSSL's order and `Format(false)` in DER order; none equals upstream's `XN_FLAG_RFC2253` string | `ZabbixCertificateName` |
| BouncyCastle's blocking `TlsClientProtocol` reads synchronously; a token cannot abort a blocked read | the PSK client drives BouncyCastle's non-blocking mode over the socket (`ZabbixPskClientStream`), so the token cancels the read |
| BouncyCastle fails a retried hello carrying an external PSK; Zabbix servers allow only P-256 | the PSK client offers P-256 only |
| OpenSSL has no ECDHE-PSK GCM suite; upstream's TLS 1.2 string expands to CBC, GCM (plain PSK) and CCM suites | six TLS 1.2 suites offered |
| the shared framework has no `Stream` over an `IBufferWriter<byte>` | `BufferWriterStream` stays |
| `System.Version` rejects `"+7.-4.1"` and `"8.0.0rc1"` | `ZabbixComponentVersion` parses upstream's form |
| `JsonNumberEnumConverter<T>` refuses `"3"` even with `AllowReadingFromString`; `JsonStringEnumConverter<T>` writes names | an owned enum converter |
| System.Text.Json ignores unmapped members silently | read records carry an `AdditionalFields` bag |
| standard resilience timeouts end at the response headers | `ZabbixResponseBufferingHandler` buffers the body inside the deadline |
| `ReadAllAsync` disposed with a pending `MoveNext` throws `NotSupportedException` | the send loop owns its enumerator |
| OpenTelemetry `AddMeter("X.*")`/`AddSource("X.*")` match nested names and nothing else | `ZabbixTelemetryNames.Subscription` |
| PowerShell: `LanguagePrimitives` fails on an `IReadOnlyList<T>` property; a `[pscustomobject]` does not convert to `Hashtable`; `ConvertTo-Json` is Newtonsoft and ignores `JsonPropertyName`; a `<TypeAdapter>` resolves its type eagerly | see [PowerShell binding](powershell-binding.md) |

### Zabbix

| Fact | Consequence |
| --- | --- |
| API error text is always English (`initLocales('en_us')`, `ZBase.php`) | prefix classification is exact |
| "No permissions for system access." is -32602; the other login failures are -32500 (`CUser.php`) | login texts matched under either code |
| enums sent as JSON numbers are accepted by 7.0 and 8.0 | the SDK writes numbers |
| boolean `filter` values: 8.0 rejects them; 7.0 rejects some fields and silently returns `[]` for others | never emit JSON `true` in a filter |
| `searchByAny` applies to `filter` as well as `search`; `search` takes a list of patterns per field | the query's OR structure |
| `searchWildcardsEnabled: true` makes `search` a pattern that must contain `*`; without one it matches only the exact value | `-Name` sets the flag only when the name contains `*` |
| `template.get` with `filter.templateid` answers `[]` for a real id; `templateids` selects it | selection by the top-level `*ids` option |
| `history.get` `time_from` is inclusive by second; `clock,ns` sort is accepted | a saturated second is read whole |
| 8.0 `history.get` truncates types 5/6 at 64 KiB unless `maxValueSize: null`; 7.x rejects the parameter (`CHistory.php`) | `MaxValueSize` is a `ZabbixOptional<int>`, gated by version |
| 8.0 `trend.get` rejects `sortfield`/`filter`/`search`; `task.get` accepts only `taskids`/`output`/`preservekeys` | the generated parameters carry only what is accepted |
| `queue.get`/`status.get` take `type`/`limit` at the root; failures arrive in `info` (`trapper.c`) | `ZabbixWireClient` |
| a successful `active check heartbeat` gets no reply (`trapper.c`) | the heartbeat is a notification |
| 7.0 and 8.0 refuse config to a proxy of another major.minor; the refusal carries the server's version | the proxy adopts the announced version |
| 8.0 `item.create` defaults `trapper_hosts` to `{$TRAPPER.ALLOWED_HOSTS}`; `""` means deny-all on 8.0 and skip-the-check on 7.x | admission by version |
| a standby HA node resets every trapper connection; `hanode.get` reports the container's internal port | live tests target a node list, not a port; a failed exchange with the server carries the endpoint and the request in its `Data` |
| the Zabbix API has no machine-readable schema; `ACCESS_RULES` + `CApiServiceFactory` determine per-version availability | the model is extracted from the source |

## Documented divergences, to be closed

Each line is a place where the code differs from a document corrected to Microsoft's guidance. A change that closes
one deletes its line in the same commit, and follows the compatibility rules above.

| Divergence | The guidance | The change it needs |
| --- | --- | --- |
| public constructors of `Zabbix.Agent` and `Zabbix.Proxy` take default arguments: `ZabbixAgentHealthCheck`, `ZabbixMemoryStore<T>`, `ZabbixPersistentStore<T>`, `ZabbixStore<T>`, `ZabbixPassiveCheckResponder`, `ZabbixTrapper`, `ZabbixTrapperSource`, `ZabbixProxyUplink` (the agent and proxy classes and `ZabbixItemAssignment` take overloads) | "DO use member overloading rather than defining members with default arguments" and "DO make only the longest overload virtual" ([member overloading](https://learn.microsoft.com/dotnet/standard/design-guidelines/member-overloading)); a default is compiled into the caller | each constructor's shorter forms become overloads that call the longest, and `Zabbix.Agent` and `Zabbix.Proxy` join the libraries `No_public_constructor_of_a_client_library_takes_a_default_argument` reads |
| `*GetParameters.Filter` and `.Search` are the generated `Zabbix*GetFilter` and `Zabbix*GetSearch`, never `null`, so `q.Filter.TechnicalName = ["web-01"]` binds, and the indexer sets a field they do not name. |
| `ZabbixItemAssignment.Key`/`.Delay`/`.Timeout` and `ZabbixItem.Key`/`.Delay` are `string` beside `ZabbixItemKey` and `ZabbixItemDelay` | a value with a parsed form is exposed as its type, not as text the caller parses (the reasoning of [CA1056](https://learn.microsoft.com/dotnet/fundamentals/code-analysis/quality-rules/ca1056), "URI properties should not be strings", applied to the SDK's own value types) | an assignment's key and delay are parsed when the agent schedules the item, so one it cannot read is that item's "not supported" and never a refusal of the whole list (upstream's agent answers it so). The typed property needs a form that holds an unparsable text and says so (`ZabbixItemKey.TryParse` kept beside the raw text, or a `ZabbixItemKey?` with the text alongside), then the hosts read the typed member; `ZabbixItem.Key`/`.Delay` follow through the overlay's `fieldKinds` (a `key` and a `delay` kind with their converters) |
| the wire counts failovers and redirects as `zabbix.wire.failovers`/`zabbix.wire.redirects` beside the span events `zabbix.wire.failover`/`zabbix.wire.redirect`; the instrument, tag and event names are internal constants on each layer's telemetry type (the trapper's on `ZabbixTrapper`), not on the public `ZabbixTelemetryNames` | use the OpenTelemetry naming guidelines, and give a consumer the names it filters by ([creating metrics, best practices](https://learn.microsoft.com/dotnet/core/diagnostics/metrics-instrumentation#best-practices)) | one name per concept (the counter and its event under one word, the counter's `{failover}` unit saying what it counts) and nested `ZabbixTelemetryNames.Instruments`/`.Events`/`.Tags` with every public name, each layer's telemetry reading them; the trapper's in `Zabbix.Proxy` move in the same change |
| the agent's, the proxy's, the listener's and the PowerShell modules' activity sources, and the store's and the trapper's own meters, carry no version; the wire's, the API's and the tree's sources and every `ZabbixLayerTelemetry` meter carry the library's | an instrumentation scope names its library and version, so a backend tells which release emitted a span or a metric ([ActivitySource](https://learn.microsoft.com/dotnet/api/system.diagnostics.activitysource.-ctor), [MeterOptions.Version](https://learn.microsoft.com/dotnet/api/system.diagnostics.metrics.meteroptions.version)) | create each with `ZabbixLayerTelemetry.ScopeVersion(typeof(T).Assembly)` as the wire's are, and a conformance test that every `ActivitySource` and `Meter` the libraries create has a version |
| 36 `InternalsVisibleTo` pairs, nine of them between NuGet packages that version separately (`Zabbix.Abstractions` to `Zabbix.Api`, `Zabbix.Sdk`, `Zabbix.Wire`, `Zabbix.Agent` and `Zabbix.Proxy`; `Zabbix.Api` to `Zabbix.Sdk`; `Zabbix.Wire` to `Zabbix.Agent` and `Zabbix.Proxy`; `Zabbix.Agent` to `Zabbix.Proxy`) | internal members seen through `InternalsVisibleTo` "can introduce breaking changes" ([version and update considerations](https://learn.microsoft.com/dotnet/csharp/whats-new/version-update-considerations#introducing-breaking-changes-in-your-libraries)) | make the shared plumbing public (reviewed surface) or shared source compiled into each package. Until then the family's versions are pinned exactly at pack time: the `ZabbixExactFamilyVersions` target in `Directory.Build.targets` rewrites every `ProjectReference` dependency as `[version]` after NuGet's `_GetProjectReferenceVersions`, so no NuGet consumer resolves a mixed set (the PowerShell modules are the row below; [ADR 0004](../adr/0004-conventions.md), amendment). `Zabbix.Api` to `Zabbix.Sdk` cannot become shared source: the SDK's System.Text.Json source-generated context instantiates the API's converters named by the records' `[JsonConverter]` attributes, and a second copy of a converter in the SDK is a second type of the same name (`CS0436`) |
| the PowerShell modules load every dependency, the implementation-only ones (BouncyCastle, Polly, OpenTelemetry, the caching and resilience packages) included, into the default load context, so an older copy another module loaded first fails `Import-Module Community.Zabbix` (`FileLoadException`) or, for an older release under the same assembly version, is refused by the import's file-version check | "load your module's dependencies into a custom `AssemblyLoadContext`" so they cannot conflict with another module's ([resolving PowerShell module assembly dependency conflicts](https://learn.microsoft.com/powershell/scripting/dev-cross-plat/resolving-dependency-conflicts)) | split each module into a thin cmdlet assembly in the default context and an engine assembly in a module-owned `AssemblyLoadContext` that holds the implementation-only set; the `Microsoft.Extensions.*` types on the SDK's public surface (`ILogger`, `IServiceProvider`) cannot move, so the seam is the SDK boundary, and the siblings' `ZabbixModuleDependencies` resolver moves into that context. `ProcessTests` pins the current behaviour (docs/KNOWN-LIMITATIONS.md, "Shared assemblies across modules, measured") |
| the lab's images are pinned by digest and each pod names its `tools/zabbix-sources.lock` release (`SourceConformanceTests`), but nothing asks the running server for its version, and the 8.0 server is a trunk build reporting 8.0.0rc2 with rc1's API, because Docker Hub has no 8.0.0rc1 image (recorded in the lock file) | "the model's provenance is a file in the repository" ([ADR 0006](../adr/0006-protocol-split.md), section 6); a test that claims a version should observe it ([integration tests](https://learn.microsoft.com/dotnet/core/testing/#integration-tests)) | `lab.sh status` or the first live test compares `apiinfo.version` and the server's reported build with the lock line for its pod, and the 8.0 pod moves to the 8.0.0 image and tag once Zabbix publishes them |
| the property tests (`PropertyTests`, CsCheck) cover the item key, the frame reader, the server-active list, the update interval, the clock, the record converter and the PSK; the allowed-peer list, `ZabbixProxyConfig.Merge` against an oracle, the item-key redactor and the node and component-version formats have examples only, and no parser is fuzzed coverage-guided | "write tests that cover the input space, not one example" ([unit testing best practices](https://learn.microsoft.com/dotnet/core/testing/unit-testing-best-practices)); property-based tests ([CsCheck](https://github.com/AnthonyLloyd/CsCheck)) and coverage-guided fuzzing ([SharpFuzz](https://github.com/Metalnem/sharpfuzz)) | a CsCheck property each for `ZabbixAllowedPeers` (`Parse(x.ToString())` equals `x`, `Contains` agrees with `IPNetwork.Contains`), `ZabbixProxyConfig.Merge` (a full snapshot then a delta equals the delta applied by id), the redactor (the name kept, no parameter character leaks) and the node and version formats (format then parse is identity; the span and UTF-8 formatters agree); a nightly SharpFuzz job over `ZabbixHeader.TryRead`, `ZabbixRedirect.TryParse`, the trapper row reader, the connector NDJSON reader, `ZabbixItemKey.TryParse` and `ZabbixAllowedPeers.TryParse`, seeded from the live captures |
| no mutation score: Stryker.NET cannot analyse the solution, because its Microsoft.Testing.Platform runner is a preview that misreports, its VSTest runner finds no MTP tests (stryker-net issue 3094), and `Zabbix.Tests` reaches `Zabbix.Abstractions` only transitively | test strength is measured, not assumed: a surviving mutant is an untested decision ([Stryker.NET](https://stryker-mutator.io/docs/stryker-net/introduction/); [unit testing best practices](https://learn.microsoft.com/dotnet/core/testing/unit-testing-best-practices)) | a mutation job on `Zabbix.Abstractions`, `Zabbix.Wire` and `Zabbix.Agent` once Stryker's MTP runner is stable, with a direct `ProjectReference` from the test project to each mutated library; the score and the survivors in decision code are recorded here |
| the fakes (`FakeZabbixServer`, `MockTrapper`, `ScriptedApi`, the PowerShell `FakeZabbix`) are shaped by reading the Zabbix source, but nothing compares their replies with a real server's, and the SDK's listeners are never driven by upstream `zabbix_sender`, `zabbix_get` or agent2 | a test double stands in for the real dependency only as far as it is checked against it ([integration tests](https://learn.microsoft.com/dotnet/core/testing/#integration-tests)) | the live job records request and reply frames per version under `tests/Zabbix.Tests/Fixtures/<version>/`, a unit test replays them against each fake (JSON-equal modulo clocks) and the nightly run fails on drift; the lab adds the agent2 image with `zabbix_sender` and `zabbix_get`, and a live class drives the trapper, passive agent and active proxy with them in unencrypted, PSK and certificate modes |
| no performance regression test: the store and send figures in "A store is a channel, not a Dataflow block" are a single measurement, and the only bounds are wall-clock smoke checks in `AdversarialPipelineTests` | measure with BenchmarkDotNet and compare against a baseline ([BenchmarkDotNet](https://benchmarkdotnet.org/articles/overview.html); the .NET team's [ResultsComparer](https://github.com/dotnet/performance/tree/main/src/tools/ResultsComparer)) | a `tests/Zabbix.Benchmarks` project with `[MemoryDiagnoser]` over frame encode and decode, the sender frame, the JSON-RPC envelope, the store write, lease and acknowledge, and `ZabbixRecordConverter`; a weekly job keeps the JSON results and fails on a time regression over 10 % or any new allocation against the last main baseline |
| 31 `Adversarial*.cs` files in `tests/Zabbix.Tests` are grouped by kind of test rather than by subject, so one subject spans two or three files, and the default `Zabbix.Tests` floor counts the PostgreSQL store tests, so a fresh clone without PostgreSQL fails its floor | name and arrange tests by the unit of work ([unit testing best practices](https://learn.microsoft.com/dotnet/core/testing/unit-testing-best-practices)); "Tests touch nothing external by default" (this document) | fold each `Adversarial*` file into its subject's test class (a `<Subject>Tests.Adversarial.cs` partial where it is long); move the PostgreSQL tests into a `Zabbix.Tests.Postgres` project or behind an xunit trait the default run excludes, with a floor of its own |
| `Connect-ZbxServer` without `-Credential` or `-ApiToken` binds the `Anonymous` set, so `Connect-ZbxServer https://zbx` connects without asking for a credential and only the first authenticated call fails (`Not authorized`) | a cmdlet that needs credentials takes them through a `Credential` parameter that PowerShell prompts for when it is mandatory ([strongly encouraged development guidelines](https://learn.microsoft.com/powershell/scripting/developer/cmdlet/strongly-encouraged-development-guidelines#support-credential-parameters)); `Invoke-RestMethod` connects unauthenticated by default | choose the default set: keep `Anonymous` (the methods Zabbix answers without a session, `apiinfo.version`, stay one parameter away) and say so in the help's first example, or make `Credential` the default set with a mandatory `-Credential` so a bare `Connect-ZbxServer -Uri` prompts, with `-Anonymous` as the explicit switch of the anonymous set |
| the hand-written change commands bind every piped property whose name matches a parameter, whatever record carries it (`Get-ZbxEvent \| Remove-ZbxTag` binds the event's `Name` and `Value`); the generated `New-` and `Set-` unbind what a piped Zabbix record bound that is not its own (`ZabbixPipedRecord`), and a generated `Remove-` takes nothing by property name | a cmdlet's pipeline input is well defined: take by property name only what the piped object means for this command ([required development guidelines](https://learn.microsoft.com/powershell/scripting/developer/cmdlet/required-development-guidelines), SC02 "support input from the pipeline") | the hand-written commands with by-property-name parameters (`Add-ZbxTag`, `Remove-ZbxTag`, the membership and template commands, `Export-ZbxConfiguration`) apply `ZabbixPipedRecord.DropForeign` as the generated `New-`/`Set-` do, and `ZabbixGetCmdlet.DropForeignReferences` moves onto it; a `ModuleShapeTests` theory replays every producer and consumer pair. docs/KNOWN-LIMITATIONS.md states the behaviour until then |
| bit-set fields stay `int`: a maintenance period's `dayofweek` and `month` (`-DayOfWeek`, `-Month`), a user's `tokens_status`, and the widget and map fields the frontend validates as a bit range (`-Weekdays`, `-ItemValueType`, `-MarkElements`, `-ViewType`); Zabbix names no define per bit for most of them (the frontend checks a range such as 1 to 127), so no rule over the source yields a `[Flags]` family | "DO use an enum with `[Flags]` for a set of values that can be combined" ([enum design](https://learn.microsoft.com/dotnet/standard/design-guidelines/enum)); "use strongly-typed .NET types" for parameters ([strongly encouraged development guidelines](https://learn.microsoft.com/powershell/scripting/developer/cmdlet/strongly-encouraged-development-guidelines), SD03) | a hand-named `[Flags]` family in `schema/overlay.json` `enums` for each bit set Zabbix documents (Monday 1 to Sunday 64; January 1 to December 2048; the value types 1 to 16), referenced through `fieldEnums` as `PREFIX#flags`, so the API records, the `Get-` output and the `New-`/`Set-` parameters (through `ZabbixFlagsAttribute`) take the names; a regeneration and a `Zabbix.Api` public-API change in one commit |
| every generated `Get-` takes both `-Limit` (the API's `limit`) and `-First` (the engine's paging, `SupportsPaging`), two knobs for one cap: `-First` becomes the server's limit plus what `-Skip` passes over unless `-Limit` is given | one concept, one parameter, and the standard name where PowerShell has one: `-First`/`-Skip` are the paging parameters the engine adds ([`SupportsPaging`](https://learn.microsoft.com/dotnet/api/system.management.automation.cmdletcommonmetadataattribute.supportspaging); [standard cmdlet parameter names](https://learn.microsoft.com/powershell/scripting/developer/cmdlet/standard-cmdlet-parameter-names-and-types)) | remove `-Limit` from the generated `Get-` and `Get-ZbxTrend` (the emitter, `ZabbixGetCmdlet.Limit`, `ZabbixParameterSets`, the help generator's reading of it, about_Community.Zabbix_Filters and the examples), keeping `-First` as the one knob; the API's `limit` stays reachable through `-Filter`/`-Parameters` for a raw request. A rename users notice, so it lands with its own CHANGELOG entry and help examples |
| `Get-ZbxHttpTest` is hand-written with a shape no generated `Get-` has: an optional single `-Name` string, a positional `-Id`, no `-Filter`, `-Search` or `-Select*` | one command family, one shape: the same parameters for the same concept ([strongly encouraged development guidelines](https://learn.microsoft.com/powershell/scripting/developer/cmdlet/strongly-encouraged-development-guidelines), SD01 and SD02 "use standard naming", "use consistent parameter types") | generate `Get-ZbxHttpTest` from `httptest.get` like every other `Get-` (`ZabbixGetCmdlet`), keeping the hand-written step expansion as a partial of the generated class, so it takes `-Name` patterns, `-Id`, `-Host` as a reference, `-Filter`, `-Search` and the selections |
| `Watch-ZbxItem -Name` names the watch session, not an item, on a command whose noun is Item, and its item selectors `-HostName`, `-Key` and `-Group` are strings where the `Get-` commands take references (`-Host`, `-HostGroup`) | a parameter name means the same thing across a module, and a parameter takes the strongly typed form the other commands take ([strongly encouraged development guidelines](https://learn.microsoft.com/powershell/scripting/developer/cmdlet/strongly-encouraged-development-guidelines), SD01, SD02, SD03) | `-WatchName` (or `-SessionName`, as the agent and proxy session commands would then say too) for the watch, and `-Host` and `-HostGroup` as `ZabbixReference<ZabbixHost>`/`ZabbixReference<ZabbixHostGroup>` resolved to the tree node before the watch starts; `Receive-ZbxWatch`, `Stop-ZbxWatch` and `Get-ZbxWatch` take the same session parameter |
| `Set-ZbxHost -Status` completes and accepts `Template`, which `host.update` refuses (a template is not a host status the API lets a request set) | a parameter takes only the values the operation accepts, so completion offers no value that fails ([strongly encouraged development guidelines](https://learn.microsoft.com/powershell/scripting/developer/cmdlet/strongly-encouraged-development-guidelines), SD03 "parameters that take a list of options") | the request's own enum family (`ZabbixHostStatusRequest`: `Monitored`, `Unmonitored`) on `ZabbixHostUpdateRequest.Status` and `ZabbixHostCreateRequest.Status`, from an overlay `fieldEnums` entry listing the two defines, or a `[ValidateSet]` the cmdlet emitter derives from a request-side member list; both change generated code in `Zabbix.Api` |
| `-Search` keys do not complete: `-Search` is a hashtable, and an argument completer does not reach the keys of a hashtable literal | a parameter that takes one of a known set of values offers them to completion ([argument completion](https://learn.microsoft.com/powershell/scripting/learn/shell/tab-completion)) | a typed `-Search` record per object (the `*GetSearch` records already generated), whose properties complete as a hashtable cast to it does, or a completer on the hashtable's keys once PowerShell offers one; `-SortField` already completes the description's `sortable` fields |
| the name completer (`ZabbixNameCompleter`) ignores the other bound filters: `Get-ZbxItem -Host web01 -Name <Tab>` completes item names from every host | completion narrows by what is already bound (`IArgumentCompleter.CompleteArgument` receives `fakeBoundParameters` for this purpose, [argument completion](https://learn.microsoft.com/powershell/scripting/learn/shell/tab-completion)) | the completer reads the bound reference filters from `fakeBoundParameters` (`HostReference`, `HostGroup`, `Template`), resolves them as the command would, and adds their ids to its `.get`; a completion test with a fake server pins the narrowed request |
| `-Debug` shows the SDK's JSON-RPC event (4000) as `template.get #4 →` without the request's parameters, so the request a user would attach to a bug report is not visible at any preference | a debug trace shows what was sent ([logging guidance for library authors](https://learn.microsoft.com/dotnet/core/extensions/logging/library-guidance): log enough to diagnose, with sensitive data redacted) | the SDK logs the request's `params` at `Trace` through the redaction it already applies to secrets (`ZabbixDataClassifications.Secret`), and the module maps `-Debug` to that level for its own connection; the event lives in `Zabbix.Sdk` |
