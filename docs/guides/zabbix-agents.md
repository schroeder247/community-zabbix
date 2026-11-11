<!--
This Source Code Form is subject to the terms of the Mozilla Public
License, v. 2.0. Copyright (c) 2026 schroeder247. SPDX-License-Identifier: MPL-2.0
-->
# Agents

An agent runs in your application and collects values for a Zabbix server. Build one with its builder and start it, or register it
in a host, which runs the same class with the same verbs. Pick the agent by who decides what to collect, and who connects:

| Agent | Use it when | Who connects | Package |
| --- | --- | --- | --- |
| `ZabbixPushAgent` | Your code decides what to collect and when. It answers no item keys. | The agent, to a trapper port | `Community.Zabbix.Agent` |
| `ZabbixActiveAgent` | The server decides which keys to collect, and the agent can reach the server. | The agent, to a trapper port | `Community.Zabbix.Agent` |
| `ZabbixPassiveAgent` | The server decides which keys to ask for, and the server can reach the agent. | The server, to port 10050 | `Community.Zabbix.Proxy` |

```bash
dotnet add package Community.Zabbix.Agent
dotnet add package Community.Zabbix.Proxy    # for ZabbixPassiveAgent or AddTrapper; it brings Community.Zabbix.Agent
```

`Community.Zabbix.Agent` brings `Community.Zabbix.Wire`, and not the API client.

## Send values on a schedule

`ZabbixPushAgent` collects values from your own sources and sends them to trapper items.

```csharp
using Community.Zabbix;

ZabbixItemKey<long> memory = new("app.memory");

await using ZabbixPushAgent agent = ZabbixPushAgent.CreateBuilder("web-01")
    .AddServer("zbx-server-01")
    .AddPoll(memory, TimeSpan.FromSeconds(30), () => GC.GetTotalMemory(false))
    .Build();
await agent.StartAsync();

Console.ReadLine();    // the agent runs until it's disposed, which drains it
```

The builder's name, `web-01`, is the agent's name and the host its values are for, as Zabbix knows it. The agent samples every 30
seconds, keeps each value in a store, and sends it until the server answers. On the server, `web-01` must have an item
`app.memory` of type "Zabbix trapper", and the item's allowed hosts (`trapper_hosts`) must admit the agent's address.

`Build()` checks the options with the rules a host checks at start, and throws `InvalidOperationException` saying what to set. It
runs once.

### Send your application's metrics

If your application records metrics with `System.Diagnostics.Metrics`, the agent can send them, and you declare nothing twice:

```csharp
using System.Diagnostics.Metrics;

Meter meter = new("MyApp");
Counter<long> requests = meter.CreateCounter<long>("app.requests");

await using ZabbixPushAgent agent = ZabbixPushAgent.CreateBuilder("web-01")
    .AddServer("zbx-server-01")
    .AddMeter("MyApp")
    .Build();
await agent.StartAsync();

requests.Add(1);
```

- An instrument's name is the item key, and a measurement's tags are the key's parameters, in the order of their names.
- A counter sends its running total, because Zabbix stores whole values. A gauge and an observable instrument send what they last said.
- A histogram has no one value to send, so it's left out, with a warning (event 3010). So is a name that can't be a key (event 3011).
- The agent sends every 30 seconds, unless you pass another interval: `AddMeter("MyApp", TimeSpan.FromSeconds(10))`.

### Add a source

```csharp
using System.Threading.Channels;

Channel<ZabbixValue> orders = Channel.CreateUnbounded<ZabbixValue>();

await using ZabbixPushAgent agent = ZabbixPushAgent.CreateBuilder("web-01")
    .AddServer("zbx-server-01")
    .AddSource(_ => orders.Reader)                                     // a channel your application writes to
    .AddTrapper(10051, o => o.AllowedPeers.Add("10.0.0.0/8"))          // values from senders, with Community.Zabbix.Proxy
    .Build();
```

| Verb | What it adds | What to know |
| --- | --- | --- |
| `AddPoll(key, interval, read)` | A cycle on the agent's clock for a typed item, for the agent's host. The key's type fixes what `read` returns. | `read` is `() => value`, `ct => ReadAsync(ct)`, or takes a service from the agent's container. A failed cycle is logged and the next one runs. |
| `AddPoll(interval, collect)` | A cycle for values known only at run time. `collect` returns one `ZabbixValue`, also asynchronously, or several. | A cycle that throws is logged (event 3005) and skipped. A poll holds up to 4,096 values for a reader that falls behind and drops the oldest (event 3006), so it keeps its cadence. |
| `AddSource(services => …)` | Any `IAsyncEnumerable<ZabbixValue>`, or a `ChannelReader<ZabbixValue>` that ends by completion, made from the agent's container when it starts. | At stop the agent reads such a channel to its end rather than cutting it short. |
| `AddMeter(meterName)` | What a `Meter` of your application records. | See [Send your application's metrics](#send-your-applications-metrics). |
| `AddTrapper(port, …)` | Values from senders on a trapper port. It starts after the agent and stops before it. | It takes the `ZabbixListenerOptions` a proxy's trapper takes. See [Listeners](listeners.md). |

A source that faults is logged (event 3002), and the others continue.

### Read a value from a service

A poll's delegate can take a service from the agent's container. The builder's `Services` is that container:

```csharp
ZabbixItemKey<long> pending = new("orders.pending");

ZabbixPushAgentBuilder builder = ZabbixPushAgent.CreateBuilder("web-01").AddServer("zbx-server-01");
builder.Services.AddSingleton<OrderQueue>();
builder.AddPoll(pending, TimeSpan.FromMinutes(1), (OrderQueue queue) => queue.Count);

await using ZabbixPushAgent agent = builder.Build();
await agent.StartAsync();

sealed class OrderQueue
{
    public long Count => 0;
}
```

Each read resolves the service in a scope of its own, as a web host does for a request, so a scoped service such as a
`DbContext` works. A read that waits on I/O takes the cancellation too: `(OrdersDb db, CancellationToken ct) => db.CountAsync(ct)`.
A service the container doesn't have fails `Build()`.

### Send to several destinations

```csharp
await using ZabbixPushAgent agent = ZabbixPushAgent.CreateBuilder("web-01")
    .AddServer("zbx1;zbx2")                     // one destination: the HA nodes of one cluster, tried in order
    .AddServer("proxy-b:10051")                 // a second destination, which gets every value too
    .Configure(o => o.Send.BatchSize = 100)
    .Build();
```

Each `AddServer` adds a destination, with its own store and connection, and each destination gets every value. Several nodes in one
call are one HA cluster: `"zbx1;zbx2"` in Zabbix's spelling, or `AddServer(node1, node2)` with `DnsEndPoint` nodes. The nodes of a
cluster are tried in order, with failover at connect only, never after the request is written. See
[HA nodes and redirects](wire-client.md#ha-nodes-and-redirects).

The options keep the destinations as Zabbix's `ServerActive` text, `zbx1:10051;zbx2:10051,proxy-b:10051`, so configuration can
bind them. `Configure` sets any option, and `Bind` binds a configuration section, each in order with the other verbs.

| `ZabbixPushAgentOptions` | Default | Meaning |
| --- | --- | --- |
| `HostName` | The builder's name | The host typed polls and meters report for. A `ZabbixValue` names its own host. |
| `ServerActive` | None | The destinations, which `AddServer` adds to. |
| `Connection` | 10 s, no TLS | Timeout, [TLS](tls-and-psk.md), compression, and reply cap. |
| `MaxItems` | 100,000 | The store's cap, per destination, and the most values a trapper's intake holds. |
| `Overflow` | `DropOldest` | What a full store sheds. `Wait` is refused, because sources run on a schedule. |
| `Send.BatchSize` | 250 | The most values in one frame. |
| `Send.FlushInterval` | 5 s | How long a partial batch waits before it's sent anyway. |
| `Send.RetryDelay`, `Send.MaxRetryDelay` | 0.5 s, 30 s | The backoff between attempts on one batch. |
| `Send.DrainTimeout` | 10 s | How long a stopping agent keeps delivering. |

The store is in memory unless `UseStore` or `UseEntityFrameworkStore` chooses a durable one, as on a proxy's builder. See
[Stores](stores.md).

## Send through the send loop

Each destination of a push or active agent runs one send loop. To run one without an agent, call `DrainAsync`, which drains a
store or any stream into a wire client:

```csharp
using Community.Zabbix.Hosting;
using Community.Zabbix.Protocol;
using Community.Zabbix.Storage;

await using var store = new ZabbixMemoryStore<ZabbixValue>();
using var wire = new ZabbixWireClient("zabbix.example");
var options = new ZabbixSendLoopOptions { Send = new ZabbixSendOptions { BatchSize = 100 } };

Task<long> drain = wire.DrainAsync(store, options);
await store.WriteAsync(new ZabbixValue("web-01", "app.requests", 42, DateTimeOffset.UtcNow));
store.TryComplete();                 // no more values: the drain ends once the store is empty
long answered = await drain;         // values delivered or rejected
```

The loop cuts the values into batches by `BatchSize` or `FlushInterval`, sends each batch as one frame, and delivers one batch at
a time, in order. The server's answer decides what happens to the batch:

| Outcome | When | What happens to the batch | Event |
| --- | --- | --- | --- |
| Delivered | The trapper answered `success`. | It leaves the store. Values the server counted as failed inside that answer are its verdict on each value, and they aren't sent again. | 1002 |
| Rejected | The trapper refused the batch for good. | It's dropped and logged. | 3105 |
| Retried | The exchange failed (unreachable, timed out, TLS refused, a redirect that couldn't be followed), or the server paused data collection. | It's kept and sent again after an exponential backoff with jitter, from `RetryDelay` up to `MaxRetryDelay`, for as long as the failure lasts. Meanwhile the store fills, and its `Overflow` policy decides what survives a long outage. | 3104 |

At stop, an agent stops its sources, then gives the send loop `DrainTimeout` to deliver what it holds. What's still undelivered
goes back to the store: a durable store keeps it for the next process, and a memory store loses it with the process. A value
nothing can keep is counted and logged as abandoned (events 3008 and 3106). The default windows fit inside `StopTimeout` and the
generic host's 30-second `HostOptions.ShutdownTimeout`.

- A source is any `IAsyncEnumerable<ZabbixValue>`, or a store, read through its acknowledged face.
- `ZabbixSendLoop.RunAsync` drains into a destination of your own: a delegate that takes a batch and a `CancellationToken` and
  answers a `ZabbixSendOutcome`, `Delivered`, `Rejected`, `Retried`, or `Held`. `DeliverAsync` on a `ZabbixWireClient` is one.
- `ZabbixSendLoopOptions` also takes a `Logger`, a clock (`Time`), and a `Progress` that receives a `ZabbixSendProgress` (an
  outcome, a count, and a time) for every batch.
- Each of these takes a `CancellationToken` last, which stops reading and starts the drain window; without one, the loop runs until
  its source ends.

## Answer the server's active checks

`ZabbixActiveAgent` asks the server which keys to collect, and your items answer them.

```csharp
ZabbixItemKey<long> cpus = new("app.cpu.count");
ZabbixItemKey<long> diskFree = new("app.disk.free");

await using ZabbixActiveAgent agent = ZabbixActiveAgent.CreateBuilder("web-01")    // the host, as the server knows it
    .AddServer("zbx-server-01")
    .Configure(o => o.HostMetadata = "Linux web")                                   // what autoregistration matches on
    .AddItem(cpus, () => Environment.ProcessorCount)
    .AddItem(diskFree, key => new DriveInfo(key[0]).AvailableFreeSpace)
    .Build();
await agent.StartAsync();
```

On the server, `web-01` must have items of type "Zabbix agent (active)" with these keys (`app.disk.free[/]`).

- **The item list.** Every `RefreshInterval`, the agent asks for its item list. When the list changes, the new schedule replaces
  the old, and an item whose key and delay didn't change keeps its next due time. A refused request is logged (event 3201), and a
  list that can't be refreshed is kept (event 3202).
- **The schedule.** Each item is collected on its own `delay`, a `ZabbixInterval` read as Zabbix reads it: an update interval
  (`30s`), flexible intervals (`50s;10s/1-5,09:00-18:00`), and scheduling intervals (`0;wd1-5h9m30`), in the agent's local time
  zone, with items of the same delay spread by their id. A delay that doesn't parse makes the item not supported (event 3205).
  When the wall clock is stepped back, the schedule moves with it (event 3209).
  `ZabbixInterval.Parse(text).GetNextCheck(now, zone)` gives the same answer to your own code.
- **The checks.** The agent's items answer them, and the [rules every key follows](items.md#the-rules-every-key-follows) apply.
  Up to `MaxConcurrentChecks` checks run at once, each bounded by the item's timeout or `CheckTimeout`.
- **The upload.** Values go through an in-memory store per destination and the send loop, as `agent data`. A reply other than
  `success` keeps and retries the batch (event 3203). A server that answers `"upload":"disabled"` holds history until it says
  otherwise (event 3206). The agent follows a redirect from an HA cluster, as Zabbix's agent does.
- **The sessions.** Each destination, each `AddServer`, has its own item list, session, buffer, and heartbeat. The session id is
  random and new in each process, and the server de-duplicates values within a session, so a restart starts a new session. The
  buffer is in memory: values not yet sent are lost when the process stops. See [Known limitations](../KNOWN-LIMITATIONS.md).

| `ZabbixActiveAgentOptions` | Default | Meaning |
| --- | --- | --- |
| `HostName` | The builder's name | The Zabbix host. An agent you construct has the machine's name. |
| `Name` | `HostName` | The agent's name in logs, metrics, and status, for an agent you construct. A builder's name takes its place. |
| `HostMetadata` | None | What autoregistration matches on. |
| `RefreshInterval` | 5 s | How often the item list is refreshed. Zabbix's `RefreshActiveChecks`. |
| `HeartbeatInterval` | 60 s | How often a heartbeat goes: whole seconds, 1 to 3600. Zero turns it off. |
| `CheckTimeout` | 3 s | How long a check may take when the server doesn't say. |
| `MaxConcurrentChecks` | 4 per processor | How many checks run at once. |
| `KeyRules`, `BundledItems` | None, `true` | Which keys the agent answers. See [Bundled items](bundled-items.md). |
| `ServerActive`, `Connection`, `MaxItems`, `Overflow`, `Send` | [As on the push agent](#send-to-several-destinations) | `Wait` is refused here too, because checks run on a schedule. |

## Answer the server's polls

`ZabbixPassiveAgent` listens on port 10050 and answers the server's requests. It's in the `Community.Zabbix.Proxy` package.

```csharp
using System.Net;

ZabbixItemKey<long> queueDepth = new("app.queue.depth");

await using ZabbixPassiveAgent agent = ZabbixPassiveAgent.CreateBuilder("web-01")   // what agent.hostname answers
    .AllowPeer(IPNetwork.Parse("10.0.0.0/24"))                                     // who may poll
    .AddItem("app.version", _ => "2.4.1")
    .AddItem(queueDepth, key => Queues.Depth(key[0]))                              // Queues: your own; key[0]: the first parameter
    .Build();
await agent.StartAsync();
```

- **Who may poll.** You must name them, as Zabbix's `Server=` line does: `AllowPeer` takes an `IPAddress`, an `IPNetwork`, or a DNS
  name, and each call adds one. An agent with no peers fails `Build()`. `AllowPsk` takes a peer's pre-shared key. A proxy's and a
  listener's builders have both verbs too.
- **What it answers.** The four keys every Zabbix agent answers (`agent.ping`, `agent.version`, `agent.hostname`, `system.uname`),
  [Zabbix's own keys](bundled-items.md), as its `KeyRules` allow, and each of its items. The
  [rules every key follows](items.md#the-rules-every-key-follows) apply.
- **What it understands.** Both forms a server uses: the JSON `passive checks` request, each key with its own timeout (1 to 600
  seconds), and the classic one key per connection that `zabbix_get` sends. `CheckTimeout` bounds a key whose request names no
  timeout.
- **Live keys.** `Items` is live, so a key you add while the agent runs is answered from the next request on.

| `ZabbixPassiveAgentOptions` | Default | Meaning |
| --- | --- | --- |
| `HostName` | The builder's name | What `agent.hostname` answers. An agent you construct has the machine's name. |
| `Name` | `HostName` | The agent's name in logs, metrics, and status, for an agent you construct. A builder's name takes its place. |
| `AllowedPeers` | None (required) | Who may poll this agent, as Zabbix's `Server=` text, which `AllowPeer` adds to. |
| `Address`, `Port` | `0.0.0.0`, 10050 | Where to bind. Port `0` lets the platform pick, and `agent.Listener.LocalEndPoint` then names it. |
| `CheckTimeout` | 3 s | How long one key may take when the request doesn't say. |
| `KeyRules`, `BundledItems` | None, `true` | Which keys the agent answers. See [Bundled items](bundled-items.md). |

The rest, including connection limits and TLS, are the options every listener has. See [Listeners](listeners.md).

## Answer your own item keys

The active and passive agents answer item keys from one table, `Items`. Their builders add keys with `AddItem`, a library's
`ZabbixItemProvider` with `AddItems`, and providers from a folder with `AddPlugins`
(`Community.Zabbix.Agent.Plugins`). A delegate answers a key, its shape says what it needs, and the key's type fixes the type of
the value it returns:

| Delegate | For |
| --- | --- |
| `() => queue.Count` | A value at hand |
| `key => orders.Count(key[0])` | A key with parameters |
| `(key, ct) => orders.CountAsync(key[0], ct)` | A read that waits on I/O, within the check's deadline |
| `(IOrderQueue queue) => queue.Count` | A service from the agent's container |
| `(IOrderQueue queue, CancellationToken ct) => queue.CountAsync(ct)` | A service, and a read that waits on I/O |

While an agent runs, `agent.Items.Add` adds a key, `agent.Items.Register` runs a library's registration, and
`agent.Items.AddPlugins` loads plugins; each returns `Items`, so the calls chain. See [Items](items.md), which also covers the
PowerShell sessions' scriptblocks, and [Plugins](plugins.md).

## Start and stop an agent

Every agent and proxy derives from `ZabbixComponent`, which gives it one lifetime.

```csharp
using var stop = new CancellationTokenSource();
Console.CancelKeyPress += (_, press) => { press.Cancel = true; stop.Cancel(); };

agent.StopTimeout = TimeSpan.FromSeconds(10);
await agent.RunAsync(stop.Token);
```

An agent takes no process signals. The application decides when to stop it, so it fits a console app, a desktop app, a test, or a
script. To run several agents or proxies, start each one, or register them in a host.

| Member | What it does | What can go wrong |
| --- | --- | --- |
| `StartAsync` | Starts the agent, then what its builder's container hosts, such as a trapper. An agent starts once. | An agent you construct is checked here: something missing or invalid throws `InvalidOperationException` saying what to set, with the `OptionsValidationException` inside, and nothing has started. A start that fails after that, a port already in use for one, stops what had started and rethrows, and you can then only dispose the agent. |
| `StopAsync` | Stops what the builder's container hosts, then the agent, draining what its store still holds, as a host's shutdown does. An agent that isn't running is left as it is. | Every phase runs even when another fails, and the failure is thrown: one as itself, several as an `AggregateException`. |
| `RunAsync` | Starts the agent, waits until its token is canceled or the agent is stopped, and stops it, as `WebApplication.RunAsync` does. | It throws what `StartAsync` and `StopAsync` throw. |
| `DisposeAsync` | Stops a running agent first, so `await using` drains it, then disposes the builder's container. | A failure to stop is logged (event 3305), not thrown. To see it, call `StopAsync` first. |
| `Dispose` | Closes at once, without delivering what the agent holds. It's the synchronous form, for a container disposed synchronously. | |
| `StopTimeout` | How long a stop may take before the token its phases hold is canceled: 30 seconds, the generic host's default. `Timeout.InfiniteTimeSpan` waits for as long as the stop takes. A host bounds an agent it runs with its own timeout instead. | |

Your logging, clock, and meters go to the builder: `UseLogging`, `UseTimeProvider`, and `UseMeterFactory`. Without them, the agent
logs nowhere, runs on the system's clock, and counts on meters of its own. Your application keeps owning what it passes:

```csharp
using ILoggerFactory loggers = LoggerFactory.Create(logging => logging.AddConsole());   // your application's own logging

await using ZabbixPushAgent agent = ZabbixPushAgent.CreateBuilder("web-01")
    .AddServer("zbx-server-01")
    .UseLogging(loggers)
    .Build();
```

The builder holds a container of its own, `Services`, where it registers the agent as a host does. The agent owns that container
and disposes it. An application that sets the options itself constructs the agent instead, with `()`, `(options)`, or
`(options, loggers, time, meters)`, and `Options` is read at start.

## Read an agent's status

A push or active agent keeps a `ZabbixComponentStatus` for each destination, with its `Delivered`, `Rejected`, `Abandoned`, `Stored`,
`Held`, `LastDelivered`, and `Fault`, among others. `Statuses` lists them, in `ServerActive` order, and `Status` is the first. An
active proxy reports the same states, so it has a `ZabbixComponentStatus` too.

```csharp
foreach (ZabbixComponentStatus status in agent.Statuses)
    Console.WriteLine($"{status.Name}: {status.Delivered} delivered, {status.Stored} stored, {status.Held} held");
```

## Alert on log events

The agents log under event ids 3000 to 3399, the stores under 2000 and 2100, and the wire under 1000 to 1399. These are worth an
alert:

| Event | Level | Meaning |
| --- | --- | --- |
| 3005, 3002, 3003 | Error | A poll cycle, a source, or a loop failed. The agent goes on. |
| 3104 | Warning | A batch wasn't acknowledged and is retried. |
| 3105, 3103 | Warning, Error | A batch was rejected and dropped. |
| 3106, 3008, 3007 | Warning | Values were lost at stop. |
| 2100 | Warning | A store dropped records (its policy, logged at the 1st, 10th, 100th, …). |
| 3201, 3203 | Warning | The server refused the active checks request or the agent data. |
| 3206 | Information | The server paused or resumed history upload. |

Item keys in these messages are classified data, which a plain logger drops. [Telemetry](telemetry.md) covers redaction, the
`Community.Zabbix.Agent` and `Community.Zabbix.Store` traces and metrics, and every layer's block of event ids.

## In an application with a host

`AddZabbixPushAgent`, `AddZabbixActiveAgent`, and `AddZabbixPassiveAgent` register an agent under its name and return its builder,
with the verbs `CreateBuilder` has. The host validates the options, starts the agent, and stops it.

```csharp
using System.Net;

ZabbixItemKey<long> memory = new("app.memory");
ZabbixItemKey<string> version = new("app.version");

HostApplicationBuilder builder = Host.CreateApplicationBuilder(args);

builder.Services.AddZabbixPushAgent("edge-01")
    .AddServer("zbx-server-01;zbx-server-02")
    .AddPoll(memory, TimeSpan.FromSeconds(30), () => GC.GetTotalMemory(false))
    .AddHealthCheck();

builder.Services.AddZabbixActiveAgent("web-01")
    .AddServer("zbx-server-01")
    .AddItem(version, () => "2.4.1")
    .AddHealthCheck();

builder.Services.AddZabbixPassiveAgent("web-01")
    .AllowPeer(IPNetwork.Parse("10.0.0.0/24"))
    .AddHealthCheck();

await builder.Build().RunAsync();
```

- **The name is the agent's identity.** It's the name of its options, its key in the container, and its host name unless `Configure`
  or `Bind` sets another. Registering a name again continues setting up the same agent, as `AddHttpClient` does.
- **Resolve an agent** by its name: `GetRequiredKeyedService<ZabbixPushAgent>("edge-01")`, and likewise for the other two. A push
  or active agent's statuses are also the keyed service `GetRequiredKeyedService<IReadOnlyList<ZabbixComponentStatus>>(name)`.
- **The container owns the agent.** Disposing the one you resolve leaves it running, as disposing an `HttpClient` from
  `IHttpClientFactory` leaves its handler, and the container disposes it at shutdown.
- **An active and a passive agent of one name answer the same items**, as one Zabbix agent answers a key whether the server polls
  it or the agent sends it. Above, the passive agent `web-01` answers `app.version` too.
- **Services.** A delegate that takes a service resolves it from the application's container.
- **Bind from configuration** with `AddZabbixAgents(builder.Configuration)`, which registers every push agent under
  `Zabbix:PushAgents:{name}` and every active agent under `Zabbix:ActiveAgents:{name}`. `AddZabbixProxies(builder.Configuration)`
  also registers every `Zabbix:PassiveAgents:{name}`. See [Configuration](configuration.md). Sources, items, and stores are code
  only: an `AddZabbix…` call with the same name adds them to the agent a row declares.

### Add a health check

`AddHealthCheck()` puts an agent on the host's health checks, as `zabbix-agent-{name}`, `zabbix-active-agent-{name}`, or
`zabbix-passive-agent-{name}`, tagged `zabbix` and `agent`. A web host maps them with `MapHealthChecks`, and a worker publishes them
through an `IHealthCheckPublisher`. A push or active agent with several destinations reports the worst. To set its thresholds,
call `AddHealthCheck(o => o.StaleAfter = TimeSpan.FromMinutes(2))`.

| Status | A push or active agent | A passive agent |
| --- | --- | --- |
| Unhealthy | A loop is faulting, or values are waiting and nothing has been delivered for `StaleAfter` (5 min). | It stopped listening. |
| Degraded | The store is past `DegradedFillFraction` (0.8) of its cap, the send loop holds a batch the destination didn't take, the latest final answer was a rejection, or nothing has been delivered in `StaleAfter` since start. | It hasn't started. |
| Healthy | Otherwise. | It listens. |

## Use them from PowerShell

The `Community.Zabbix.Agent` module runs the active and passive agents as sessions in a runspace. See [PowerShell](powershell.md).

```powershell
Start-ZbxActiveAgent -Name 'web-agent' -HostName 'web-01' -Server 'zabbix.example:10051'
Register-ZbxActiveAgentHandler -Name 'web-agent' -Key 'app.health' -ScriptBlock { 'ok' }
(Get-ZbxActiveAgentStatus -Name 'web-agent').AgentStatus | Select-Object Delivered, Rejected
Stop-ZbxActiveAgent -Name 'web-agent'
```

## See also

- [Items](items.md) and [Plugins](plugins.md): the keys the active and passive agents answer.
- [Bundled items](bundled-items.md): Zabbix's own keys, and the rules that restrict them.
- [Stores](stores.md): the store between collection and delivery, and durable stores.
- [The wire client](wire-client.md): the Zabbix protocol the agents send over.
- [TLS and PSK](tls-and-psk.md): TLS for the agents' connections.
- [Listeners](listeners.md) and [Proxies](proxies.md): the trapper, the options every listener has, and the proxies.
- [Getting started](../GETTING-STARTED.md): an agent in a whole program, run against a server.
- [Known limitations](../KNOWN-LIMITATIONS.md): what's lost at stop, and sessions that don't survive a restart.
- The [PushAgent, ActiveAgent, PassiveAgent, and WorkerService samples](../../samples/README.md).
