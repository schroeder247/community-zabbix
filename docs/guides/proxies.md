<!--
This Source Code Form is subject to the terms of the Mozilla Public
License, v. 2.0. Copyright (c) 2026 schroeder247. SPDX-License-Identifier: MPL-2.0
-->
# Proxies

A proxy takes values from agents and senders, keeps them, and passes them to a server. `ZabbixActiveProxy` connects out to the
server, and `ZabbixPassiveProxy` waits for the server to connect to it. Both are in `Community.Zabbix.Proxy`, which brings
`Community.Zabbix.Agent` with it.

```bash
dotnet add package Community.Zabbix.Proxy
```

You must create the proxy on the server first, with the same name. A proxy starts, stops, and disposes as an [agent](zabbix-agents.md) does.

## Run an active proxy

```csharp
using System.Net;

await using ZabbixActiveProxy proxy = ZabbixActiveProxy.CreateBuilder("proxy-01")
    .AddServer("zbx-server-01")
    .AllowPeer(IPNetwork.Parse("10.0.0.0/8"))
    .Configure(o => o.OfflineBuffer = TimeSpan.FromHours(24))
    .Build();
await proxy.StartAsync();
```

- `CreateBuilder` takes the proxy's name as the server knows it.
- `AddServer` names the server the proxy reports to.
- `AllowPeer` says who may reach the proxy's trapper: an `IPNetwork`, an `IPAddress`, or a DNS name. The trapper listens on 10051
  unless `AddTrapper` sets another port, and follows the rules in [Choose who may connect](listeners.md#choose-who-may-connect).
- `Build()` checks the options with the rules a host checks at start, and throws `InvalidOperationException` saying what to set.

For its configuration, the proxy:

- Pulls `proxy config` before its trapper opens and every `ConfigInterval`, and merges each delta into what it holds. Until the
  first configuration arrives, every value sent to it is refused.
- Keeps what it holds when a pull fails (event 7001).
- Announces `Version` until a server refuses it and names its own, which the proxy adopts (events 7002, 7003).

A server that doesn't know the proxy, or doesn't allow its address or encryption, closes the connection without an answer (event
7002). A proxy you just created reaches the server's configuration cache at the next `CacheUpdateFrequency` refresh.

A new item can lose its first values. Once the server's cache holds a host, `proxy config` gives the proxy that host's items from the
database, including an item the cache doesn't hold yet. Until the cache's next refresh, the server drops a value for that item and
still answers the proxy with `success`, so the proxy can't tell. If you need an item's first values, you must wait one
`CacheUpdateFrequency` after you create the item before you send them.

For its values, the proxy:

- Leases a page of at most `PageSize` values from its store every `DataInterval`, and sends it with the store's sequence ids. When
  more values are waiting, the next page goes at once.
- Treats only the server's `success` as an acknowledgement. A failed or refused page stays leased and goes again unchanged (events
  7016, 7011), and the server's `last_id` drops what it already has.
- Holds its history while the server answers `upload: disabled` (`IsHeld`, event 7012).
- Drops a value whose item has left the configuration (event 7014).

### Report to an HA cluster

```csharp
using System.Net;

await using ZabbixActiveProxy proxy = ZabbixActiveProxy.CreateBuilder("proxy-01")
    .AddServer("zbx-server-01;zbx-server-02")                     // one cluster: its nodes, in the order they're tried
    .AllowPeer(IPNetwork.Parse("10.0.0.0/8"))
    .Build();
```

A proxy reports to one server or one HA cluster, as Zabbix's proxy does. Give `AddServer` all of a cluster's nodes at once: joined by
`;`, or as `DnsEndPoint`s. Each `AddServer` names a server, so `Build()` refuses a second one, as Zabbix's proxy refuses a `,` in its
`Server` setting. The proxy walks the nodes as [the wire client](wire-client.md#ha-nodes-and-redirects) does, but it never follows a
redirect.

## Run a passive proxy

```csharp
using System.Net;

await using ZabbixPassiveProxy proxy = ZabbixPassiveProxy.CreateBuilder("dmz-proxy")
    .WithVersion("7.4")                                           // the server's major.minor
    .AllowPeer(IPNetwork.Parse("10.0.0.0/8"))                     // the server, and the agents and senders that report here
    .Build();
await proxy.StartAsync();
```

One listener answers the server, the agents, and the senders:

- The server pushes `proxy config`. The proxy merges it and answers `success`.
- The server pulls `proxy data`, one page per visit, and `proxy tasks`.
- Only the server's `success` acknowledges a page. An unacknowledged page is the next pull's, with the same ids.
- Agents and senders send values as they do to an active proxy.

You must set the server's version with `WithVersion`. No server message tells a passive proxy the server's version, and a server
never configures a proxy of another major.minor. Without it, `Build()` throws `InvalidOperationException`, and a host fails at start.

## Send values to a proxy

Point `zabbix_sender` and agents at the proxy's trapper, as you would point them at a server. The proxy takes a value by Zabbix's
own rules:

- The value's host and literal key must name an enabled item of a monitored host.
- `sender data` goes to trapper items, or to HTTP agent items that allow traps. `agent data` goes to active agent items.
- For `sender data`, the peer must be in the item's `trapper_hosts`. An empty list admits everyone on a 7.x protocol, and denies
  everyone from 8.0.

A refused value counts as failed in the proxy's answer, and as `refused` on `zabbix.proxy.values` (event 7020). The proxy answers
agents' `active checks` from its configuration. See [Read what a trapper answers](listeners.md#read-what-a-trapper-answers).

## Keep values through an outage

```csharp
using System.Net;
using Microsoft.EntityFrameworkCore;
using Community.Zabbix.Storage.EntityFrameworkCore;

await using ZabbixActiveProxy proxy = ZabbixActiveProxy.CreateBuilder("proxy-01")
    .AddServer("zbx-server-01")
    .AllowPeer(IPNetwork.Parse("10.0.0.0/8"))
    .UseEntityFrameworkStore(db => db.UseNpgsql(connectionString))
    .Build();
```

- History is in memory, capped by `MaxItems` and `Overflow`, unless the builder chooses a store.
- `UseEntityFrameworkStore`, in `Community.Zabbix.Agent.Storage.EntityFrameworkCore`, puts history in a database, which keeps
  unacknowledged pages across a restart. `UseStore` takes a store of your own, and `UseMemoryStore` goes back to memory. They're
  the push agent's verbs too. See [Stores](stores.md).
- A value older than `OfflineBuffer` is dropped as `expired`.
- At stop, an active proxy has `DrainTimeout` to send what it holds.

## Handle tasks and add your own data

```csharp
using System.Net;
using Community.Zabbix.Proxy;

await using ZabbixActiveProxy proxy = ZabbixActiveProxy.CreateBuilder("proxy-01")
    .AddServer("zbx-server-01")
    .AllowPeer(IPNetwork.Parse("10.0.0.0/8"))
    .OnTask((task, cancellationToken) => ValueTask.FromResult(new ZabbixProxyTaskResult(
        ZabbixProxyTaskType.RemoteCommandResult, task.Clock, task.Ttl, Status: 0, "done", task.ParentTaskId ?? 0UL)))
    .Build();
```

| Verb | On | What it does |
| --- | --- | --- |
| `OnTask` | Both | Runs a task the server hands back, and returns the result to upload. Without it, tasks are only logged (event 7015). A handler that throws is logged (event 7018), and the task is reported as failed. |
| `AddProvider` | Both | Adds a page of your own values to each upload. A provider that throws is skipped for that turn (event 7017). |
| `OnConfig` | Passive | Can veto a pushed configuration: return the reason, or `null` to accept it. A veto keeps the configuration held (event 7032). |
| `OnStats` | Passive | Answers `zabbix.stats`. Without it, the reply's `data` is empty. |

A later `OnTask`, `OnConfig`, or `OnStats` replaces an earlier one, and each `AddProvider` adds one. A proxy's handlers are set when
it's built.

`DiscoveryStore`, `AutoregistrationStore`, `AvailabilityStore`, and `TasksStore` on the proxy are where you write other rows to
upload. They're in memory, and `null` before start and after stop. Write a discovery result with `ZabbixProxyDiscovery.WriteAsync`.

A refused page that carries rows other than history is split: the history goes first, then each kind alone. Rows the server refuses
on their own are dropped (event 7040) and counted as `invalid` on `zabbix.proxy.values`.

## Encrypt a proxy's connections

```csharp
using System.Net;

byte[] key = Convert.FromHexString(File.ReadAllText("/etc/zabbix/dmz-proxy.psk").Trim());   // a TLSPSKFile holds the key as hex

await using ZabbixPassiveProxy proxy = ZabbixPassiveProxy.CreateBuilder("dmz-proxy")
    .WithVersion("7.4")
    .AllowPeer(IPNetwork.Parse("10.0.0.0/8"))
    .WithPsk("dmz-proxy", key)                                    // the server connects with the proxy's key
    .AllowPsk("web-01", web01Key)                                 // an agent connects with its own
    .Build();
```

- `WithPsk` sets the proxy's own identity and key, Zabbix's `TLSPSKIdentity` and `TLSPSKFile`. An active proxy connects to its
  server with them, and a passive proxy takes its server's connections with them. In the server's settings for the proxy, the
  same identity and key must be allowed.
- `AllowPsk` lets an agent or a sender connect with its own identity and key, as an entry of the trapper's `PreSharedKeys`.
- A key is 16 to 256 bytes. The options keep it as the hex digits a `TLSPSKFile` holds.
- A trapper that took only unencrypted connections, its default, takes pre-shared-key connections only after either verb. To take
  both, set `TlsAccept` with `AddTrapper` or `Configure` after it. See [TLS and PSK](tls-and-psk.md).

## Set options

`Configure` sets any option, in order with the other verbs, and `Bind` sets them from a configuration section. A proxy you construct
with `new ZabbixActiveProxy(options)` or `new ZabbixPassiveProxy(options)` reads `Options` when it starts. Options that can't be used
make `Build()` throw `InvalidOperationException`, or `StartAsync` on a proxy you construct, saying what to set.

| Option | Default | Meaning |
| --- | --- | --- |
| `Name` | None | The proxy's name as the server knows it. Required on a class you construct. A registration's name takes its place. |
| `Mode` | `Active` | `Active` or `Passive`. Only `AddZabbixProxies` reads it, to choose the class. |
| `Server` | None | Active only. The server, or the nodes of one HA cluster joined by `;`, as Zabbix's `Server` spells them. `AddServer` sets it. |
| `Connection` | See [Set options](wire-client.md#set-options) | Active only. How the proxy connects to the server. |
| `Version` | 7.4.0 when active, none when passive | `major.minor[.patch]`. Required on a passive proxy. `WithVersion` sets it. |
| `ConfigInterval` | 10 s | Active only. How often to pull the configuration. |
| `DataInterval` | 1 s | Active only. How often to send history when no more is waiting. |
| `PageSize` | 1000 | The most values one page carries: 1 to 100,000. |
| `OfflineBuffer` | 1 hour | How long history is kept when it can't be sent: 1 to 720 hours. |
| `DrainTimeout` | 30 s | Active only. How long a stopping proxy tries to send what it holds. |
| `MaxItems` | 100,000 | The store's cap, in values. |
| `Overflow` | `DropOldest` | What a full store does. See [Stores](stores.md#set-a-policy). |
| `Trapper` | Port 10051 | The listener for agents and senders, and for the server on a passive proxy. `AddTrapper`, `AllowPeer`, and `AllowPsk` set it. See [Set a listener's options](listeners.md#set-a-listeners-options). |

## In an application with a host

`AddZabbixActiveProxy` and `AddZabbixPassiveProxy` register a proxy under its name and return its builder, with the verbs
`CreateBuilder` has. The host validates the options, starts the proxy, and stops it.

```csharp
using System.Net;

HostApplicationBuilder builder = Host.CreateApplicationBuilder(args);

builder.Services.AddZabbixActiveProxy("proxy-01")
    .AddServer("zbx-server-01")
    .AllowPeer(IPNetwork.Parse("10.0.0.0/8"))
    .AddHealthCheck();

builder.Services.AddZabbixPassiveProxy("dmz-proxy")
    .WithVersion("7.4")
    .AllowPeer(IPNetwork.Parse("10.0.0.0/8"))
    .AddHealthCheck();

await builder.Build().RunAsync();
```

- The proxy is a keyed service under its name: `GetRequiredKeyedService<ZabbixActiveProxy>("proxy-01")`, or
  `GetRequiredKeyedService<ZabbixPassiveProxy>("dmz-proxy")`. Registering a name again continues setting up the same proxy.
- `UseStore` also takes the container, as `(provider, policy, services)`.
- `AddHealthCheck` adds `zabbix-active-proxy-{name}` or `zabbix-passive-proxy-{name}`, with the `zabbix` and `proxy` tags. An active
  proxy's check reports its uploads as an agent's check does, and a passive proxy's check reports whether it listens.
- Options that can't be used throw `OptionsValidationException` at start.

From a configuration file, `AddZabbixProxies` registers every `Zabbix:Proxies:{name}` row and chooses the class by its `Mode`. See
[Configuration](configuration.md).

```csharp
builder.Services.AddZabbixProxies(builder.Configuration);
```

## Use it from PowerShell

The `Community.Zabbix.Proxy` module runs a proxy as a session, kept under its `-Name`:

```powershell
Start-ZbxPassiveProxy -Name 'dmz-proxy' -AllowedPeer '10.0.0.0/24' -Version '7.4'
Get-ZbxPassiveProxyStatus -Name 'dmz-proxy'
Stop-ZbxPassiveProxy -Name 'dmz-proxy'
```

- `Start-ZbxPassiveProxy` listens on `-Port` (10051), and requires `-AllowedPeer` and `-Version`. `Send-ZbxPassiveProxyHistory`
  queues values for the server to pull.
- `Start-ZbxActiveProxy` runs only the uplink, with no trapper. It pulls the configuration every `-RefreshInterval` (60 s), which
  `Set-ZbxActiveProxy` changes while the session runs, and uploads what `Register-ZbxActiveProxyDataProvider` scriptblocks return.

See [PowerShell](powershell.md) for the protocol commands that run with no session.

## Limits

- Proxies keep agent sessions in memory, and answer every `active checks` request in full.
- Proxies forward raw values, with no preprocessing, and keep no log read positions.
- A passive proxy needs the server's version, from `WithVersion`.

[Known limitations](../KNOWN-LIMITATIONS.md#behaviour) has each of these in full.

## See also

- [Listeners](listeners.md): who may connect, what a trapper answers, and the proxy protocol's building blocks.
- [Stores](stores.md): keep history across a restart.
- [TLS and PSK](tls-and-psk.md): certificates and pre-shared keys on both sides of the wire.
- [Telemetry](telemetry.md): the proxy's instruments and log events.
- [Security](../../SECURITY.md): the TLS rules and the listeners' threat model.
- [Troubleshooting](../TROUBLESHOOTING.md) and [Known limitations](../KNOWN-LIMITATIONS.md).
