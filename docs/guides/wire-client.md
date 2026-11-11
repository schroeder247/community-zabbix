<!--
This Source Code Form is subject to the terms of the Mozilla Public
License, v. 2.0. Copyright (c) 2026 schroeder247. SPDX-License-Identifier: MPL-2.0
-->
# The wire client

`ZabbixWireClient` speaks the Zabbix protocol: framed messages over TCP. Use it to send values to a server or proxy, read an
item from an agent, and ask a server for its status and queue. To manage Zabbix's configuration, use
[`ZabbixClient`](api-client.md) instead, which speaks JSON-RPC to the frontend.

```bash
dotnet add package Community.Zabbix.Wire
```

The client belongs to one server or proxy, and each read from an agent names the agent. You don't need the API or a host.

## Send a value

```csharp
using var wire = new ZabbixWireClient("zabbix.example");

ZabbixItemKey<long> requests = new("app.requests");

ZabbixSenderResponse sent = await wire.SendAsync("web-01", requests, 42);

Console.WriteLine($"processed {sent.Processed}, failed {sent.Failed} of {sent.Total}");
```

A `ZabbixItemKey<T>` declares a key once, with its type, so passing `"ok"` here doesn't compile. To send several values in one call:

```csharp
ZabbixItemKey<string> status = new("app.status");

await wire.SendAsync(batch => batch
    .Add("web-01", requests, 42)
    .Add("web-02", status, "ok"));
```

Name the server as `host`, `host:port`, or `[ipv6]:port`. The port is 10051 unless you name another. The host and a trapper
item with that key must exist on the server.

`SendAsync` also takes `ZabbixValue` values, for items only known at run time and to say an item isn't supported, with
`IsSupported = false`. A large call goes in
frames of at most `BatchSize` values and `MaxBatchBytes` bytes, each sent when the one before was answered.

### Read the reply

A returned `ZabbixSenderResponse` means the server took every frame.

| Member | What it holds |
| --- | --- |
| `Processed`, `Failed`, `Total` | The server's counts, summed over the frames |
| `HasFailures` | Whether `Failed` is above 0 |
| `Frames` | How many frames the call took |
| `TimeSpent`, `Info` | The server's own timing, and its raw `info` line |

A value the server refused isn't an exception; `Failed` counts it. If `Failed` is above 0, check that:

- The host and the key exist.
- The value has the item's type.
- Your address is in the item's allowed hosts (`trapper_hosts`).
- The server has loaded the item. A new item reaches its configuration cache at the next `CacheUpdateFrequency` refresh.

### Handle failures

Anything other than a counted success throws, so a value is never reported as delivered by mistake.

| Exception | What happened | What the server took |
| --- | --- | --- |
| `ZabbixTrapperRefusedException` | The reply wasn't a counted success. `Response` and `Info` hold it. | Nothing |
| `SocketException` | No node could be reached. | Nothing |
| `AuthenticationException` | The TLS handshake was refused. | Nothing |
| `TimeoutException` | The deadline passed. | Unknown for the frame in flight |
| `IOException` | The connection was lost after the request was written, or a redirect couldn't be followed. | Nothing, for a redirect |
| `InvalidDataException` | The reply wasn't a frame, wasn't JSON, or was larger than `MaxPayloadBytes`. | Unknown |
| `ZabbixSenderIncompleteException` | A later frame failed after earlier ones were taken. | `Delivered` |
| `ZabbixSenderCanceledException` | The token was canceled after the first frame was taken. | `Delivered` |
| `ArgumentException` | One item alone is larger than `MaxBatchBytes`. | Nothing was sent |

The two partial failures carry `Delivered`, the answer for the frames taken, and `Unconfirmed`, the values the server didn't
confirm. Send `Unconfirmed` again, and nothing the server confirmed goes twice:

```csharp
try
{
    ZabbixSenderResponse sent = await wire.SendAsync(values, cancellationToken);
    if (sent.HasFailures) Console.WriteLine($"the server refused {sent.Failed} of {sent.Total}");
}
catch (ZabbixSenderIncompleteException partial)
{
    await wire.SendAsync(partial.Unconfirmed, cancellationToken);
}
catch (ZabbixTrapperRefusedException refused) when (refused.IsPaused)
{
    // The server paused data collection and took nothing. Keep the values and send them later.
}
```

### Keep values through an outage

To batch, retry, and survive restarts, drain a store into the client with `DrainAsync` from `Community.Zabbix.Agent`. See
[Agents](zabbix-agents.md#send-through-the-send-loop) and [Stores](stores.md).

## HA nodes and redirects

Name the nodes of an HA cluster, or a proxy and its fallback, in order of preference. Add each one to a builder, or join them with
`;` for the constructor: `new ZabbixWireClient("zbx-server-01;zbx-server-02")`.

```csharp
using ZabbixWireClient wire = ZabbixWireClient.CreateBuilder()
    .AddServer("zbx-server-01")
    .AddServer("zbx-server-02:10051")
    .WithTimeout(TimeSpan.FromSeconds(5))
    .Build();
```

- The client tries the next node only when a connect or TLS handshake fails. Nothing is sent again once a request is written.
- The node that answered stays first for the next call, and a failed first node moves behind the others.
- `Timeout` applies to each node, so the longest wait before a failure is the number of nodes times `Timeout`.

`SendAsync` follows a redirect, as `zabbix_sender` does. A server of an HA cluster or a proxy group answers with the address
of the node that takes the values, and the frame goes there, with the same TLS settings. That address can be outside your list:
read [What the wire's TLS checks](../../SECURITY.md#what-the-wires-tls-checks-and-what-it-does-not) before you rely on
redirects across a network you don't trust. If a redirect can't be followed, the call throws `IOException` and nothing was taken.

## Read an item from an agent

Each read names the agent, so one client reads from many. It doesn't need a server.

```csharp
using var wire = new ZabbixWireClient();

ZabbixAgentAnswer answer = await wire.GetAgentItemAsync("web-01", "system.cpu.load[all,avg1]");

Console.WriteLine(answer.IsSupported ? answer.Value : $"not supported: {answer.Error}");
```

Name the agent as `host` or `host:port`. The port is `ZabbixWireClient.DefaultAgentPort` (10050) unless you name another. The
agent's `Server` setting must allow your address.

A key the agent doesn't support is an answer with `Error` set, not an exception. `ZabbixAgentRefusedException` means the agent
refused the request as a whole. Network, TLS, deadline, and frame failures throw as they do for a send.

The client asks in JSON first and falls back to plaintext for an agent older than 7.0, which it remembers for that agent. It
keeps a small object per agent until you dispose it, plus a copy of the certificate and key if you use certificate TLS.

## Ask a server for its status and queue

The client sends the requests the web frontend sends to the server's trapper port. Most need an API token, which `WithToken` sets.
A `user.login` session's id works too.

```csharp
using ZabbixWireClient wire = ZabbixWireClient.CreateBuilder()
    .AddServer("zabbix.example")
    .WithToken(apiToken)
    .Build();

await wire.PingAsync();
JsonElement status = await wire.GetStatusAsync();
foreach (ZabbixQueueDelays row in await wire.GetQueueAsync(ZabbixQueueOverview.ByProxy))
    Console.WriteLine($"proxy {row.ProxyId}: {row.Delay5} overdue 5-10 s, {row.Delay600} over 10 min");
```

| Method | What it asks | What it needs |
| --- | --- | --- |
| `PingAsync` | Whether the server is up and takes this session | A session |
| `GetStatusAsync` | The "System information" statistics | A super admin's session, for the full answer |
| `GetQueueAsync` | Overdue items, counted by delay | A super admin's session |
| `GetQueueDetailsAsync` | The most overdue items | A super admin's session |
| `GetStatsAsync` | The server's internal statistics | An address `StatsAllowedIP` admits |
| `GetQueueSizeAsync` | How many items are overdue | An address `StatsAllowedIP` admits |
| `TestItemAsync` | Runs an item's collection and preprocessing | A session |
| `SendAlertAsync` | Sends a test message through a media type | A session |
| `TestReportAsync` | Generates and sends a scheduled report | A session |
| `EvaluateExpressionsAsync` | Evaluates trigger expressions | A session |
| `RequestAsync` | Any request whose handler reads a `data` object | What the request needs |

A `failed` reply throws `ZabbixServerRefusedException`, with the server's reason in `Info`. A failed exchange throws what a send
throws, with the request and the node in `Exception.Data` (`Zabbix.Request`, `Zabbix.Node`).

## Create a client

Each of these gives the same client:

| To | Use |
| --- | --- |
| Name the server and nothing else | `new ZabbixWireClient("zbx-server-01")`, or `new ZabbixWireClient()` for a client that only reads from agents |
| Set more than the server | `ZabbixWireClient.CreateBuilder()`, its verbs, and `Build()` |
| Share a client in an application with a host | `AddZabbixWireClient`, with the same verbs. See [Register it in a host](#register-it-in-a-host). |

The builder's verbs set what `ZabbixWireOptions` holds, in the order you call them:

| Verb | What it sets |
| --- | --- |
| `AddServer` | Adds a node of the server or proxy, after the nodes added before it. Pass a `DnsEndPoint`, or `host` or `host:port` as text. |
| `WithToken` | The API token the server's requests carry. |
| `WithPsk`, `WithCertificate` | TLS with a pre-shared key, given as bytes, or with certificate files. A later call replaces an earlier one. See [TLS and PSK](tls-and-psk.md). |
| `WithTimeout` | How long one attempt on one node may take. |
| `WithCompression` | Whether a request is compressed when that makes it smaller. |
| `WithBatchSize`, `WithCheckTimeout` | The most values one frame carries, and how long an agent may take to answer. |
| `Configure` | Any option, through a delegate. |
| `Bind` | The options in a configuration section, keyed by their names. |
| `UseLogging`, `UseTimeProvider` | Where the client logs, and the clock it reads. Only the builder from `CreateBuilder()` has these: in a host, the client uses the host's. |

- `Build()` checks the options with the rules a host checks at start. If they don't validate, it throws `InvalidOperationException`,
  with each failure in the `OptionsValidationException` inside.
- `Build()` runs once. To build another client, create another builder.
- You must dispose the client you build. The logger factory you pass to `UseLogging` stays yours.

## Set options

Set them with the builder's verbs, pass `ZabbixWireOptions` to the constructor, or set `Options` before the first request. The
client reads them at the first request.

| Option | Default | Meaning |
| --- | --- | --- |
| `Server` | None | The server or proxy, or the nodes of one HA cluster. A client that only reads from agents doesn't need one. |
| `SessionId` | None | The API token the server's requests carry, which `WithToken` sets, or a `user.login` session's id. It's never logged. |
| `BatchSize` | 250 (`DefaultBatchSize`) | The most values one frame carries. |
| `MaxBatchBytes` | 1 GiB (`DefaultMaxBatchBytes`) | The most bytes one frame's JSON payload carries, before compression. |
| `CheckTimeout` | 3 s | How long a read from an agent may take: whole seconds, 1 to 600. |
| `Connection` | See below | How the client connects, to the server and to every agent. |

`ZabbixConnectionOptions` is the same for this client, the agents, and the proxies:

| Option | Default | Meaning |
| --- | --- | --- |
| `Timeout` | 10 s | One attempt on one node: connect, handshake, request, and reply. |
| `MaxPayloadBytes` | 16 MiB (`DefaultMaxPayloadBytes`) | The largest reply read, declared or inflated. A reply over it throws `InvalidDataException`. |
| `Compress` | `false` | Compress a request when that makes it smaller. |
| `Tls` | Unencrypted | Certificate or pre-shared-key TLS. See [TLS and PSK](tls-and-psk.md). |

A server passed to a constructor or to `AddServer` that doesn't parse throws `ArgumentException` there. Options that can't be used, including a
certificate file that can't be read, throw `InvalidOperationException` at the first request, with the reason in the message and
the cause inside. So does a request to the server when none is set.

## The link beneath the client

The client runs over a `ZabbixWireLink`, the way an `HttpClient` runs over a handler. The link holds the node list, the connection
options, and the walk from one node to the next. Build one yourself to wrap every exchange, for a log, a counter, or a fault
injector in a test, or to send a request the client doesn't model:

```csharp
using Community.Zabbix.Protocol;

using var link = new ZabbixWireLink([ZabbixEndPoint.Parse("zbx-server-01"), ZabbixEndPoint.Parse("zbx-server-02")]);
var real = link.AsExchange(followRedirects: false);
Func<ReadOnlyMemory<byte>, CancellationToken, ValueTask<ReadOnlyMemory<byte>>> logged = async (request, cancellationToken) =>
{
    ReadOnlyMemory<byte> reply = await real(request, cancellationToken);
    Console.WriteLine($"{request.Length} bytes sent, {reply.Length} answered");
    return reply;
};

using var wire = new ZabbixWireClient(logged);
JsonElement stats = await wire.GetStatsAsync();
```

- An exchange is one request and its reply, as a `Func`: the payload goes in, and the reply's payload comes back unframed.
- `AsExchange(followRedirects: true)` follows a proxy group's redirect once. Pass `acceptLargeReplies: true` to accept a reply of
  up to 16 GiB, as a proxy's configuration needs.
- A client built on an exchange sends everything over it. To also pass a token, logging, or a batch size, use
  `new ZabbixWireClient(exchange, options, loggers, time)`. `Options.Server` then only names the other end in logs and failures.
- The client owns nothing it was given. You must dispose the link after the clients that use it, because the link holds what
  certificate mode read from its files.

## Lifetime and logging

The client is thread-safe and meant to be shared. Each call opens its own connection. Dispose the client to release what
certificate mode read from its files.

Without a logger factory, the client logs nowhere. Pass one to `UseLogging`, or to the longest constructor:

```csharp
using ILoggerFactory loggers = LoggerFactory.Create(logging => logging.AddConsole());

using ZabbixWireClient wire = ZabbixWireClient.CreateBuilder()
    .AddServer("zbx-server-01")
    .UseLogging(loggers)
    .Build();
```

The client logs under the category `Community.Zabbix.ZabbixWireClient`. See [Telemetry](telemetry.md) for its event ids, spans,
and metrics.

## Register it in a host

`AddZabbixWireClient` registers a client under a name. The container builds it, shares it, and disposes it, and the client logs
through the host's logging.

```csharp
HostApplicationBuilder builder = Host.CreateApplicationBuilder(args);
builder.Services.AddZabbixWireClient("prod")
    .AddServer("zbx-server-01")
    .AddServer("zbx-server-02")
    .WithTimeout(TimeSpan.FromSeconds(5));

using IHost app = builder.Build();
ZabbixWireClient wire = app.Services.GetRequiredService<IZabbixWireClientFactory>().Create("prod");
await wire.SendAsync("web-01", new ZabbixItemKey<long>("app.requests"), 42);
```

- `AddZabbixWireClient` returns an `IZabbixWireClientBuilder`, which takes the verbs of [Create a client](#create-a-client) except
  `UseLogging` and `UseTimeProvider`.
- `IZabbixWireClientFactory.Create(name)` returns the client registered under the name, built once and shared. It throws
  `KeyNotFoundException` for a name nobody registered, and `TryCreate` asks first.
- The client is also a keyed singleton: `[FromKeyedServices("prod")] ZabbixWireClient wire`.
- `AddZabbixWireClient()` without a name registers the unnamed client, which is also injected as plain `ZabbixWireClient`.
- Registering a name again continues setting up the same client.
- `Bind(builder.Configuration.GetSection("Wire"))` reads the options from a section you choose, keyed by their names: `Server`,
  `BatchSize`, `Connection:Timeout`, and the others in [Set options](#set-options). Keep the token out of the file.
- The options are checked when the host starts. Options that don't validate fail with `OptionsValidationException`.

## Use it from PowerShell

The `Community.Zabbix` module has the same requests as commands: `Send-ZbxData`, `Get-ZbxAgentItem`, `Get-ZbxQueue`, and
`Get-ZbxServerStatus`.

```powershell
Send-ZbxData -HostName 'web-01' -Key 'app.requests' -Value 42 -Server 'zabbix.example:10051'
Get-ZbxAgentItem -Agent 'web-01' -Key 'agent.ping', 'system.uname'
```

With `-Server`, `Send-ZbxData` needs no connection. Without it, the trapper is `Connect-ZbxServer -Trapper`'s.

## See also

- [TLS and PSK](tls-and-psk.md): encrypt the connection.
- [Agents](zabbix-agents.md): collect, store, and send on a schedule.
- [Stores](stores.md): keep values until the server has them.
- [Items](items.md): the keys an agent answers.
- [Troubleshooting](../TROUBLESHOOTING.md) and [Known limitations](../KNOWN-LIMITATIONS.md).
- The [SendValues, ReadAgentItem, and AskServer samples](../../samples/README.md).
