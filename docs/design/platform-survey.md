<!--
This Source Code Form is subject to the terms of the Mozilla Public
License, v. 2.0. Copyright (c) 2026 schroeder247. SPDX-License-Identifier: MPL-2.0
-->
# Platform survey: the Microsoft libraries the push plane is built on

This is the record of the platform research the push plane (agents, proxies, trappers, the connector) is built on. The
project answers every "which primitive?" question by reading Microsoft Learn and the `dotnet/*` sources first
([platform first](decisions.md#design-decisions)); this is that reading, with a verdict per
candidate and where each one lands in the SDK. Every row cites its source (section 3). [The decisions register](decisions.md#a-store-is-a-channel-not-a-dataflow-block) holds the Dataflow
measurement; [the architecture](architecture.md) shows how the pieces fit.

## 1. The verdicts

| Candidate | What it is | Verdict | Where it lands |
| --- | --- | --- | --- |
| `System.Threading.Channels` | the in-box bounded buffer with a named full mode and a counted drop | **the edge** — every producer/consumer hand-off | `ZabbixStore<T>` is a `ChannelWriter<T>`; `ZabbixMemoryStore<T>` is a channel |
| `IAsyncEnumerable<T>` + `System.Linq.AsyncEnumerable` (.NET 10, in the box; replaces `System.Linq.Async`) | the stream and its operators | **the stream** — a store's read face, a watcher, a poll | everything a consumer reads; `Chunk(size)` exists, a time-bounded chunk does not (the send loop's batcher stays) |
| `Task.WhenEach` (.NET 9) | tasks yielded as they complete | useful, not needed | the agent's pumps end together (`WhenAll`) |
| `Channel.CreateUnboundedPrioritized` (.NET 9) | a priority channel | not a Zabbix concept (the wire is FIFO) | — |
| `PeriodicTimer` + `TimeProvider` | cadence on an injected clock | **adopted** | `ZabbixPoll.Every`, `ZabbixWatcher`, the send loop's flush, the proxy's config pull |
| `IHostedLifecycleService` (.NET 8) | start/stop phases around `IHostedService` | **adopted** — ordered shutdown | the agent host: `StartingAsync` builds, `StartAsync` runs, `StoppingAsync` stops the sources, `StopAsync` drains for `DrainTimeout`, `StoppedAsync` waits for the drain and then disposes the store and the sender; the listener the same, accept loop first |
| Kestrel's socket transport: `SocketTransportFactory : IConnectionListenerFactory`, `IConnectionListener.AcceptAsync`, `ConnectionContext.Transport : IDuplexPipe` (shared framework `Microsoft.AspNetCore.App`, no HTTP involved) | the platform's own TCP accept loop, memory-pooled pipes, socket options, per-connection abort | **adopted** — the listener discipline is the platform's; `IConnectionListenerFactory` is the seam a test replaces with an in-memory transport | `Zabbix.Proxy` (`<FrameworkReference Include="Microsoft.AspNetCore.App"/>`): `ZabbixListener` binds through the factory and reads frames with the `ZabbixHeader` pipe overloads |
| Kestrel's `ConnectionHandler` pipeline | connection middleware on `KestrelServer` | **rejected** — it needs `KestrelServer`, an `IServer` composed by a web host; every trapper, passive agent and passive proxy would host ASP.NET Core's server. The rejection is written at `ZabbixListener` | the listener keeps its own accept loop over the factory above |
| `System.Threading.RateLimiting` (`ConcurrencyLimiter`, `PartitionedRateLimiter<IPAddress>`) | leases, partitioned by key | **adopted** — a global concurrency cap and a per-peer cap on the listener; ASP.NET Core removed its own `ConcurrencyLimiter` middleware in favour of this | `ZabbixListenerOptions.MaxConcurrentConnections`, `MaxConnectionsPerPeer` |
| `System.Net.IPNetwork` (.NET 8) | CIDR | **adopted** for the addresses and blocks of `AllowedPeers` and `trapper_hosts`: it refuses bare addresses, names and macros, so `ZabbixPeer` reads each entry as Zabbix does and holds an address or a block as an `IPNetwork`, and a listener's empty list fails | `ZabbixPeer`, `ZabbixAllowedPeers` |
| `SslStream` + BouncyCastle PSK | TLS server side | **adopted** — the client side's twin: `ZabbixTls.WrapServerAsync`; the PSK server through BouncyCastle's `PskTlsServer`, because .NET has no TLS-PSK API | `ZabbixTls`, `ZabbixTlsAcceptor` |
| `System.IO.Pipelines` `PipeReader.AsStream()` / `PipeWriter.AsStream()` | streams over pipes | **adopted** for the TLS hop only: `SslStream` and BouncyCastle need a `Stream`, the connection is an `IDuplexPipe` | a small duplex adapter in `Zabbix.Proxy` |
| `System.Diagnostics.Metrics.MeterListener` | observe any .NET meter | **not adopted** — a meter as an item source has no consumer in the SDK; an application that wants its meters as items writes one `AddPoll` over a `MeterListener` (ADR 0006) | — |
| `Microsoft.Extensions.Diagnostics.HealthChecks` (`IHealthCheck`, `IHealthCheckPublisher`; works on the generic host) | health as a first-class host concept | **adopted** — an agent's health is its store depth against the cap and the age of its last delivery | `ZabbixAgentHealthCheck`, registered by `AddHealthCheck()` |
| `HybridCache` (`Microsoft.Extensions.Caching.Hybrid`) | two-level cache with stampede protection and serialization | **not adopted** — the active agent's session state is a fresh random session id per active agent host, and nothing persists it | — |
| `Microsoft.Extensions.ServiceDiscovery` (`ServiceEndpointResolver.GetEndpointsAsync`, config `Services:{name}:{scheme}`, DNS and platform providers) | names → endpoints | **not adopted** — an active-node resolver over `hanode.get` would duplicate the wire's own node-list failover, which is upstream's (`commshigh.c`) (ADR 0006) | — |
| `Microsoft.Extensions.Http.Resilience` / Polly v8 | the resilience pipeline | **adopted** for the API's HTTP chain and the send loop's retry; **not** for the node-list failover: a retry cannot tell the connect phase from the reply phase and re-sends written frames, so the node walk is owned and fails over only at connect (`ZabbixNodeLink`) | `AddStandardResilienceHandler`; `ZabbixSendLoop` |
| `Microsoft.Extensions.Compliance` | classification and redaction | **adopted** | `[ZabbixSecret]`, a `DataClassificationAttribute`, on tokens, passwords and PSKs |
| TPL Dataflow | block meshes | **rejected** (measured); no project references it | — |

## 2. What the push plane is, in these terms

```
sources  ──WriteAsync──▶  ZabbixStore<T>  ──lease, ack─────▶  ZabbixSendLoop  ──ZabbixExchangeCallback──▶  server / proxy
(poll, trapper)            (a channel)                          (batch, retry)                      (framed TCP, TLS)

listener: IConnectionListenerFactory.BindAsync ─▶ AcceptAsync loop ─▶ one handling task per connection
          each connection: peer allowed? ─▶ lease (global + per-peer) ─▶ TLS? ─▶ ZabbixHeader.ReadAsync(pipe) ─▶ handler ─▶ reply
```

A trapper is a listener whose handler writes the frame's values into a store. A proxy is a trapper plus a loop that
uploads the store as `proxy data` and a cadence that pulls `proxy config`. A passive agent is a listener whose handler
answers item keys. An active agent is a poll whose keys come from `active checks`, sending `agent data`.

## 3. Sources

- Kestrel endpoints and `ListenOptions`: https://learn.microsoft.com/aspnet/core/fundamentals/servers/kestrel/endpoints
- `SocketTransportFactory`, `BindAsync`, ctor `(IOptions<SocketTransportOptions>, ILoggerFactory)`: https://learn.microsoft.com/dotnet/api/microsoft.aspnetcore.server.kestrel.transport.sockets.sockettransportfactory
- `IConnectionListener.AcceptAsync` / `UnbindAsync`, `ConnectionContext.Transport`: https://learn.microsoft.com/dotnet/api/microsoft.aspnetcore.connections.iconnectionlistener
- `IHostedLifecycleService` and the event order: https://learn.microsoft.com/dotnet/core/extensions/generic-host
- `System.Linq.AsyncEnumerable` in .NET 10 (breaking change page): https://learn.microsoft.com/dotnet/core/compatibility/core-libraries/10.0/asyncenumerable
- `Task.WhenEach` and prioritized channels: https://learn.microsoft.com/dotnet/core/whats-new/dotnet-9/libraries#threading
- `PartitionedRateLimiter.Create`, `RateLimitPartition.GetConcurrencyLimiter`: https://learn.microsoft.com/dotnet/api/system.threading.ratelimiting.partitionedratelimiter
- ASP.NET Core 11 removes its `ConcurrencyLimiter` middleware for `System.Threading.RateLimiting`: https://learn.microsoft.com/aspnet/core/breaking-changes/11/concurrencylimiter-removed
- Resource monitoring is metrics-based, `IResourceMonitor` obsolete (EXTOBS0001): https://learn.microsoft.com/dotnet/core/diagnostics/diagnostic-resource-monitoring
- Health checks, publisher, generic host: https://learn.microsoft.com/aspnet/core/host-and-deploy/health-checks
- `HybridCache`: https://learn.microsoft.com/aspnet/core/performance/caching/hybrid
- Service discovery: https://learn.microsoft.com/dotnet/core/extensions/service-discovery
