<!--
This Source Code Form is subject to the terms of the Mozilla Public
License, v. 2.0. Copyright (c) 2026 schroeder247. SPDX-License-Identifier: MPL-2.0
-->
# Listeners

A listener takes connections from Zabbix peers. Use a trapper to take values from `zabbix_sender` and pass them on, and the
connector endpoint to take the stream a Zabbix connector posts. Both are in `Community.Zabbix.Proxy`, which brings
`Community.Zabbix.Agent` with it. The [passive agent](zabbix-agents.md) and the [proxies](proxies.md) are listeners too.

```bash
dotnet add package Community.Zabbix.Proxy
```

Listeners bind sockets through Kestrel's socket transport. They start no web host, and leave a web application's own Kestrel
settings alone. Only the connector endpoint runs in a web application.

## Relay values from senders

```csharp
await using ZabbixPushAgent relay = ZabbixPushAgent.CreateBuilder("relay")
    .AddServer("zbx-server-01")
    .AddTrapper(10051, o => o.AllowedPeers.Add("10.0.0.0/8"))     // required: an empty list is refused
    .Build();
await relay.StartAsync();
```

Values sent to port 10051 by anyone in `10.0.0.0/8` go into the agent's store and on to `zbx-server-01`. The trapper is one of the
[push agent's](zabbix-agents.md) sources:

- It starts after the agent and stops before it, and goes with the agent.
- What it reported as processed reaches the store before the agent drains, unless the drain window closes first (event 3008).
- A later `AddTrapper` for the same agent sets up the same trapper, and the last call wins.

Point `zabbix_sender` at a relay, and active agents at a [proxy](proxies.md). A trapper reads rows by host and key, which is how
`zabbix_sender` names them. An active agent of 4.4 or later names its items by id, which only a proxy can resolve, because a proxy
holds the server's configuration. Its rows count as failed at a relay.

## Choose who may connect

Every listener fails closed: it needs a list of who may connect.

```csharp
builder.AddTrapper(10051, o =>
{
    o.AllowedPeers.Add("10.0.0.0/8");                     // a CIDR block
    o.AllowedPeers.Add("192.0.2.10, zbx-01.example");     // an address and a DNS name, comma-separated
});
```

- `AllowedPeers` reads as Zabbix's `Server=` setting does: IP addresses, CIDR blocks, and DNS names, one per entry or
  comma-separated.
- You must write out "everyone", as `0.0.0.0/0,::/0`.
- A name is resolved on each connection, and the operating system's resolver caches it. A name that doesn't resolve matches nothing
  (event 6006).
- An empty list is refused. `Build()` throws `InvalidOperationException` saying what to set, and a host's validation at start
  throws `OptionsValidationException`.
- The builders of a passive agent, a proxy, and a listener take its peers typed: `AllowPeer(IPNetwork.Parse("10.0.0.0/8"))`,
  `AllowPeer(IPAddress)`, or `AllowPeer("zbx-01.example")` for a DNS name. Each call adds one entry.

Each connection is checked in this order, and then gets one framed request and its reply:

1. The per-peer limit
2. The peer list
3. The listener's concurrency limit
4. The protection it arrives with (`TlsAccept`)

A refused connection is closed without a reply. It's logged once per peer and reason an hour (event 6003), and counted on
`zabbix.listener.rejected_connections` with its reason: `peer_limit`, `peer_not_allowed`, `listener_full`,
`unencrypted_not_allowed`, `tls_not_allowed`, or `connection_refused`. A connection whose `Timeout` passes before it's served is
logged the same way, with the reason `timeout`.

## Accept encrypted connections

```csharp
builder.AddTrapper(10051, o =>
{
    o.AllowedPeers.Add("10.0.0.0/8");
    o.TlsAccept = ZabbixTlsAcceptModes.Psk;                                  // unencrypted connections are refused
    o.PreSharedKeys["web-01"] = File.ReadAllText("web-01.psk").Trim();       // identity to hex key, [ZabbixSecret]
});
```

- For certificate mode, set `o.Tls = new ZabbixTlsOptions { CertificateFile = …, KeyFile = …, CaFile = … }`. Leave `Tls.Mode`
  unset, because `TlsAccept` decides.
- The builders of a passive agent, a proxy, and a listener take a peer's key as bytes: `AllowPsk("web-01", key)` adds it to
  `PreSharedKeys` as hex. A listener that took only unencrypted connections, its default, then takes pre-shared-key ones only.
- A credential the accept set never uses, such as a key set while `TlsAccept` stays `Unencrypted`, fails validation.

See [TLS and PSK](tls-and-psk.md), and [Security](../../SECURITY.md) for what the TLS checks and what it doesn't.

## Set a listener's options

A listener reads `ZabbixListenerOptions` once, at start. A configuration reload doesn't rebind a port or change a running listener's
peers. `AddTrapper` takes these options on a push agent and on a proxy alike.

| Option | Default | Meaning |
| --- | --- | --- |
| `Address`, `Port` | `0.0.0.0`, 10051 (10050 for a passive agent) | Where to bind. Port `0` lets the platform pick. |
| `AllowedPeers` | None (required) | Who may connect. |
| `MaxConnectionsPerPeer` | 16 | Beyond it, a peer's connection is refused, not queued. |
| `MaxConcurrentConnections` | 256 | Beyond it, as many again wait for a slot, and the rest are refused. |
| `Timeout` | 30 s | One request, connect to reply. |
| `MaxPayloadBytes` | 16 MiB | The largest frame accepted. |
| `Compress` | `false` | Whether replies are compressed. |
| `TlsAccept` | `Unencrypted` | A set of `ZabbixTlsAcceptModes`: `Unencrypted`, `Psk`, `Certificate`. |
| `Tls`, `PreSharedKeys` | None | The certificate files, or PSK identities and their hex keys. |

A relay's intake buffer holds as many values as each of the push agent's stores: its `MaxItems`, 100,000 unless set.

## Read what a trapper answers

A trapper reads `sender data` and `agent data` frames and answers as a server does: `success`, with the processed, failed, and total
counts and the seconds spent in `info`. A sender reads it as [Read the reply](wire-client.md#read-the-reply) shows.

| A row | Counts as |
| --- | --- |
| Names a host and a key that parses, and the writer behind the trapper kept the value | Processed |
| Carries no value | Processed, and nothing is written |
| Names no host or key, or its key doesn't parse | Failed |
| Has an `ns` outside its range, or is marked not supported and gives no reason | Failed |
| The writer didn't keep it: a full intake buffer (the push agent's `MaxItems`), or a store that refused it | Failed |

- A proxy's trapper writes straight to the proxy's store. Over a durable store, a value counts as processed only once it's committed.
- A relay's trapper writes to its intake buffer, and the agent moves values from there to the store. A value counts as processed once
  it's in the buffer, so a store's refusal or a failed commit doesn't reach the sender.
- A frame that isn't a trapper request is answered `failed`, with `not a sender data request` in `info` (event 6101).
- The counts are `zabbix.listener.trapper.values`, tagged `processed` or `failed`.

## Receive a connector's stream

A Zabbix connector (`New-ZbxConnector`, or `connector.create`) streams item values and events as newline-delimited JSON over HTTP.
`MapZabbixConnector` maps the endpoint it posts to, in a `Microsoft.NET.Sdk.Web` project, and writes each record into a channel:

```csharp
using System.Threading.Channels;
using Community.Zabbix.Connector;

WebApplicationBuilder builder = WebApplication.CreateBuilder(args);
WebApplication app = builder.Build();

Channel<ZabbixConnectorRecord> records = Channel.CreateBounded<ZabbixConnectorRecord>(
    new BoundedChannelOptions(10_000) { FullMode = BoundedChannelFullMode.Wait });    // Wait paces the server and loses nothing
var connector = new ZabbixConnectorOptions { BearerToken = builder.Configuration["Zabbix:ConnectorToken"] };
connector.AllowedPeers.Add("192.0.2.10");                                             // with a token as well, a request needs both
app.MapZabbixConnector("/zabbix/connector", records.Writer, connector);
await app.RunAsync();
```

Read `records.Reader`. Each record is a `ZabbixConnectorValue` (an item value: `ItemId`, `Host`, `Value`, `Clock`) or a
`ZabbixConnectorEvent` (an event: `EventId`, `Name`, `Severity`, `Hosts`, `Tags`).

The endpoint fails closed: mapping it with no `BearerToken`, no `AllowedPeers`, and no explicit `AllowAnonymous = true` throws
`ArgumentException`. The peer is the connection's remote address, so behind a reverse proxy, configure forwarded headers.

| Reply | When | What the server does |
| --- | --- | --- |
| 200 | Every record is in the channel | Moves on |
| 400 | A line is neither an item value nor an event, and nothing is written (event 6301) | Logs the `error` and drops the batch |
| 401 | The bearer token is missing or wrong (event 6303) | Doesn't retry |
| 403 | The peer is outside `AllowedPeers` (event 6303) | Doesn't retry |
| 503 | The channel completed while records were being written (event 6302) | Sends the whole body again |

Delivery is at least once. The server sends a body again after a 503 or a transport failure, so de-duplicate by `ItemId` and `Clock`,
or by `EventId`.

Outside ASP.NET Core, `ZabbixConnectorFeed.ReadAsync(body, cancellationToken)` reads the records from any stream, a request body or
a file, as they arrive. `ZabbixConnectorFeed.Parse` reads one line or a whole body at once. A line that's neither an item value nor
an event throws `JsonException`.

## Build your own from the pieces

The classes above are built from public pieces, and each works on its own. Here, a listener of your own runs a trapper:

```csharp
using System.Net;
using System.Threading.Channels;
using Community.Zabbix.Listening;

Channel<ZabbixValue> values = Channel.CreateBounded<ZabbixValue>(
    new BoundedChannelOptions(10_000) { FullMode = BoundedChannelFullMode.Wait });

await using ZabbixListener listener = ZabbixListener.CreateBuilder()
    .UseSocketTransport(new IPEndPoint(IPAddress.Loopback, 0))      // port 0: the platform picks one
    .AllowPeer(IPAddress.Loopback)
    .OnRequest(new ZabbixTrapper(values.Writer).Handler)
    .Build();
await listener.StartingAsync(cancellationToken);                     // binds the port
await listener.StartAsync(cancellationToken);                        // starts accepting
Console.WriteLine(listener.LocalEndPoint);
```

- `UseSocketTransport` binds Kestrel's socket transport: on a port of every interface, or on an `IPEndPoint`. You don't build the
  transport yourself.
- `AllowPeer` and `AllowPsk` are the verbs a passive agent and a proxy have. `WithCertificate` gives the listener its certificate,
  `Configure` sets any other option, and `UseLogging`, `UseTimeProvider`, and `UseMeterFactory` set its logging, clock, and meters.
- `OnRequest` takes a `ZabbixRequestCallback`, one frame in and one out, or a `ZabbixConversationCallback`, for a protocol of
  several frames. A later call replaces it.
- `Build()` checks the options with the rules the listener checks at start, and throws `InvalidOperationException` saying what to
  set. It runs once.
- You must call `StartingAsync` before `StartAsync`. After it, `LocalEndPoint` names the bound address, so a listener on port `0`
  reports the port it got.
- To bring a transport of your own, such as an in-memory one in a test, construct the listener with it:
  `new ZabbixListener(options, handler, transport)`.

| Piece | What it does |
| --- | --- |
| `ZabbixListener` | Serves one framed request per connection through a `ZabbixRequestCallback`, which receives a `ZabbixRequest` (`Payload`, `Peer`) and returns the reply's payload. A handler that throws is answered `failed` (event 6005). `CreateBuilder` builds one on Kestrel's socket transport, and its constructors take an `IConnectionListenerFactory` of your own. |
| `ZabbixConversationCallback` | Takes the connection whole, for a protocol of several frames. `ZabbixConversationContext.Reply(handler)` is the one-frame case. |
| `ZabbixTrapper` | `Handler` writes each value into a `ChannelWriter<ZabbixValue>`. Give it a channel in `BoundedChannelFullMode.Wait` mode: a channel that drops reports a dropped value as kept. |
| `ZabbixTrapperSource` | Pairs the trapper with its listener as a hosted service: a push agent's `AddTrapper`. Read its values once, from `ReadAllAsync`. |
| `ZabbixPassiveCheckResponder` | `Handler` answers keys from a `ZabbixItemDictionary` table. `ZabbixPassiveAgent` pairs it with its listener. |
| `ZabbixProxyUplink` | The active proxy's side of the server conversation, over any `ZabbixExchangeCallback` ([the link](wire-client.md#the-link-beneath-the-client)). `GetConfigAsync(held)` pulls `proxy config` and merges it into the configuration held, `UploadAsync(page, more, proxyDelay)` sends a `proxy data` page, and `ProbeAsync` asks a server that disabled uploads whether it has enabled them. |
| `ZabbixProxyAdmission` | `BuildAsync(config, protocol)` builds the admission rule for a configuration, and `Refusal` says why a value is refused. |
| `ZabbixProxyDataProviders` | Asks providers for pages and merges them. |
| `ZabbixComponentVersion` | Reads Zabbix's version strings, `8.0.0rc1` included. |
| `ZabbixAllowedPeers` | `Parse("10.0.0.0/8,zbx.example", null)` reads a list as the listeners read it, so you can check a list before you use it. `ContainsAsync(address)` answers for one peer. |

## In an application with a host

```csharp
builder.Services.AddZabbixPushAgent("relay")
    .AddServer("zbx-server-01")
    .AddTrapper(10051, o => o.AllowedPeers.Add("10.0.0.0/8"));
```

- The registration creates the same classes, and the host runs them: the trapper is a hosted service, registered after the agent,
  so it starts after it and stops before it. See [Agents](zabbix-agents.md#in-an-application-with-a-host).
- A later `AddTrapper` for the same agent configures the same trapper, and the last call wins.
- A proxy's `AddTrapper` takes the same `ZabbixListenerOptions`, and sets up the proxy's own trapper.
- Every registration calls `AddZabbixSocketTransport`, which registers the transport under its own service key,
  `ZabbixListener.TransportKey`. To replace it, register another `IConnectionListenerFactory` under that key. A class you construct
  builds the transport itself.
- A listener of your own has no registration: in a host, a passive agent, a proxy, or a push agent's trapper is the listener the
  host runs.

From a configuration file, `AddZabbixProxies` registers every `Zabbix:PushAgents:{name}:Trapper` subsection, whose port is 10051 unless
it names another, and every `Zabbix:PassiveAgents:{name}` and `Zabbix:Proxies:{name}` row. See [Configuration](configuration.md).

```csharp
builder.Services.AddZabbixProxies(builder.Configuration);
```

## Use it from PowerShell

`Read-ZbxConnectorStream`, in the `Community.Zabbix.Proxy` module, parses a connector's newline-delimited JSON into item values and
events, piped or from a file. It isn't an HTTP listener, and no command runs a trapper.

```powershell
Get-Content connector.ndjson | Read-ZbxConnectorStream -ItemHandler { '{0} = {1}' -f $_.Name, $_.Value }
```

## Limits

- A peer's repeated refusal is logged once an hour, and every occurrence is counted.
- A listener refuses credentials it wouldn't use.
- The proxies have [limits](proxies.md#limits) of their own.

[Known limitations](../KNOWN-LIMITATIONS.md#behaviour) has each of these in full.

## See also

- [Proxies](proxies.md): the active and passive proxies.
- [Agents](zabbix-agents.md): the passive agent, and the push agent a trapper feeds.
- [Stores](stores.md): what a trapper's values go into.
- [TLS and PSK](tls-and-psk.md): certificates and pre-shared keys on both sides of the wire.
- [Telemetry](telemetry.md): the listener's instruments and log events.
- [Security](../../SECURITY.md): the TLS rules and the listeners' threat model.
- [Troubleshooting](../TROUBLESHOOTING.md) and [Known limitations](../KNOWN-LIMITATIONS.md).
- The [PassiveAgent sample](../../samples/README.md).
