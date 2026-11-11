<!--
This Source Code Form is subject to the terms of the Mozilla Public
License, v. 2.0. Copyright (c) 2026 schroeder247. SPDX-License-Identifier: MPL-2.0
-->
# Telemetry

Every package publishes its traces and metrics through `System.Diagnostics` and its logs through `ILogger`. Your application decides
what to collect and where to export it. `ZabbixTelemetryNames`, in `Community.Zabbix.Abstractions`, has the names to subscribe to.

```bash
dotnet add package OpenTelemetry.Exporter.OpenTelemetryProtocol   # brings OpenTelemetry
dotnet add package OpenTelemetry.Extensions.Hosting               # in an application with a host
```

## Collect traces and metrics

```csharp
using OpenTelemetry;
using OpenTelemetry.Metrics;
using OpenTelemetry.Trace;
using Community.Zabbix.Diagnostics;

using TracerProvider traces = Sdk.CreateTracerProviderBuilder()
    .AddSource(ZabbixTelemetryNames.Subscription).AddOtlpExporter().Build();            // "Community.Zabbix.*"
using MeterProvider metrics = Sdk.CreateMeterProviderBuilder()
    .AddMeter(ZabbixTelemetryNames.Subscription).AddOtlpExporter().Build();
```

- The sources and meters are process-wide, so these providers see every client and agent in the process.
- The exporters read `OTEL_EXPORTER_OTLP_ENDPOINT` and the other `OTEL_*` variables.
- Every source and meter is named under `Community.Zabbix.`, so the wildcard picks up a layer added later.
- To take one layer only, name it: `AddSource(ZabbixTelemetryNames.Api)`.

## Collect logs

Hand your logger factory to each client and agent you build. A client or agent you give none logs nowhere, but its traces and metrics
still flow. This sample takes `Community.Zabbix.Sdk` for the client and `Community.Zabbix.Agent` for the agent:

```csharp
using OpenTelemetry.Logs;

using ILoggerFactory loggers = LoggerFactory.Create(logging =>
{
    logging.EnableRedaction().AddOpenTelemetry(o => o.AddOtlpExporter());
    logging.Services.AddRedaction(redaction => redaction.SetZabbixRedactors());         // secrets and item keys stay out
});

await using ZabbixClient client = ZabbixClient.CreateBuilder()
    .WithEndpoint("https://zabbix.example/")
    .WithToken(apiToken)
    .UseLogging(loggers)                                                              // where the client logs
    .Build();
await using ZabbixPushAgent agent = ZabbixPushAgent.CreateBuilder("web-01")
    .AddServer("zbx-server-01")
    .UseLogging(loggers)                                                              // where the agent logs
    .Build();
```

## Find a source or meter

| Name (`ZabbixTelemetryNames.`) | Spans | Instruments |
| --- | --- | --- |
| `Community.Zabbix.Api` (`Api`) | One client span per JSON-RPC call, named for the method | `rpc.client.call.duration` |
| `Community.Zabbix.Wire` (`Wire`) | `zabbix.wire.exchange`, `zabbix.wire.notify`, one per request `ZabbixWireClient` asks a server | `zabbix.wire.exchange.duration`, `zabbix.wire.failovers`, `zabbix.wire.redirects` |
| `Community.Zabbix.Listener` (`Listener`) | `zabbix.listener.request`, one server span per connection | `zabbix.listener.connection.duration`, `zabbix.listener.active_connections`, `zabbix.listener.rejected_connections`, `zabbix.listener.trapper.values` |
| `Community.Zabbix.Store` (`Store`) | None | `zabbix.store.pending`, `zabbix.store.dropped`, `zabbix.store.acknowledged` |
| `Community.Zabbix.Tree` (`Tree`) | `zabbix.tree.history.page`, `zabbix.tree.watcher.tick` | `zabbix.tree.operation.duration`, `zabbix.tree.watcher.polls`, `zabbix.tree.watcher.failures`, `zabbix.tree.watcher.dropped` |
| `Community.Zabbix.Agent` (`Agent`) | One per exchange (`active checks`, `agent data`) | `zabbix.agent.exchange.duration`, `zabbix.agent.values` |
| `Community.Zabbix.Proxy` (`Proxy`) | `zabbix.proxy.config`, `zabbix.proxy.data` | `zabbix.proxy.operation.duration`, `zabbix.proxy.values` |
| `Community.Zabbix.PowerShell` (`PowerShell`) | One per command, named for it. One per session start and stop (`{kind}.start`, `{kind}.stop`), a child of the command's. One root span per handler invocation (`{kind}.invoke`), linked to its session's. | `zabbix.powershell.command.duration`, `zabbix.powershell.session.duration`, `zabbix.powershell.handler.duration`, `zabbix.powershell.handler.invocations` |

These hold across layers:

- Durations are histograms in seconds, with bucket boundaries from 5 ms to 60 s.
- A span and its duration sample carry the same tags.
- A failure adds `error.type` to both and sets the span's status to that type, never to the message. A peer's refusal isn't attached as an
  exception, because its text echoes the request. A caller's cancellation isn't an error.
- An exchange with a peer carries `server.address` and `server.port`.
- A wire span carries `network.transport`, `zabbix.tls.mode`, and the Zabbix request as `rpc.method`.
- A listener span carries the peer as `client.address`. A metric doesn't, because a tag per peer has no bound.
- The API follows the OpenTelemetry RPC conventions (`rpc.system.name` = `jsonrpc`, `rpc.method`). A server error sets `error.type` and
  `rpc.response.status_code` to the JSON-RPC code, and adds `zabbix.api.error.code` and `zabbix.api.error.kind` (`validation`,
  `session_terminated`, `transient`, and others). A pipeline timeout is `timeout`.

## Build a dashboard

| Question | Instrument | Split by |
| --- | --- | --- |
| Is the API slow or failing? | `rpc.client.call.duration` | `rpc.method`, `error.type`, `zabbix.api.error.kind` |
| Is a backlog building? | `zabbix.store.pending` (records held, read at collection) | `zabbix.store.name` |
| Are values being lost? | `zabbix.store.dropped` | `zabbix.store.reason`: `refused`, `discarded`, `expired`, `drop_oldest`, `drop_newest`, `drop_write`, `faulted` |
| Do agents deliver? | `zabbix.agent.values` | `zabbix.agent.delivery.outcome`: `delivered`, `rejected`, `retried`, `held`, `returned`, `abandoned` |
| Is the HA pair moving? | `zabbix.wire.failovers`, `zabbix.wire.redirects` | |
| Who is knocking? | `zabbix.listener.rejected_connections` | `zabbix.listener.rejection` |
| What do trappers take? | `zabbix.listener.trapper.values` | `zabbix.listener.trapper.outcome`: `processed`, `failed` |
| What do proxies forward? | `zabbix.proxy.values` | `zabbix.proxy.outcome` (`uploaded`, `held`, `refused`, `unmapped`, `invalid`), `zabbix.proxy.kind` |
| Are proxy exchanges failing? | `zabbix.proxy.operation.duration` | `zabbix.proxy.operation` (`config`, `data`), `zabbix.proxy.mode`, `error.type` |
| Does a watch keep up? | `zabbix.tree.watcher.failures`, `zabbix.tree.watcher.dropped` | `zabbix.tree.scope` |

The tags are low-cardinality. A scope is a depth (`server`, `group`, `host`, `item`), never a name, and the store and proxy names are the
ones your application registered.

## Alert on log events

Every event has a fixed id in its layer's block. An id never moves between blocks and is never reused, so a query or an alert can match on
it. The category is the emitting type, as in `Community.Zabbix.ZabbixWireClient[1002]`.

| Ids | Layer |
| --- | --- |
| 1000–1399 | The wire: 10xx sends, 11xx the exchange, 12xx reads from an agent, 13xx the server's requests |
| 20xx, 21xx | The stores |
| 30xx–33xx | The agents and the send loop: 32xx the active agent, 33xx the lifecycle of an agent or proxy |
| 40xx | The API |
| 50xx | The tree |
| 60xx–63xx | The listeners: 61xx the trapper, 62xx the passive agent, 63xx the connector endpoint |
| 70xx | The proxies |
| 14xxx | The PowerShell modules |

These events are worth an alert:

| Id | What happened |
| --- | --- |
| 1002 | A server counted values as failed. |
| 1102 | An HA node couldn't be connected. |
| 2000 | A store's backend failed. |
| 2100 | A store dropped records. |
| 3008 | Values were abandoned at stop. |
| 5003 | A watcher tick failed. |
| 6003 | A listener refused a connection. |
| 7001, 7002 | A proxy couldn't pull its configuration. |
| 7012 | The server disabled uploads. |
| 7030 | A proxy loop faulted. |
| 7040 | Rows the server refused were dropped. |

A refusal a peer can repeat at will (6002, 6003, 7020) is logged once an hour per peer or key and reason. Every occurrence still counts on the
instruments.

The standard resilience handler logs one `Polly[3] Execution attempt` line at `Information` for every API call, under your application's `Polly`
category. To quiet it, set `"Logging": { "LogLevel": { "Polly": "Warning" } }`. A failed try still logs at `Warning` or `Error`.

## Redact logs

```csharp
builder.Logging.EnableRedaction();
builder.Services.AddRedaction(redaction => redaction.SetZabbixRedactors());
```

`EnableRedaction` turns redaction on for your application's logs, and `SetZabbixRedactors`, in `Community.Zabbix.Abstractions`, sets the
SDK's redactor for each class of `ZabbixDataClassifications`. Without them, a classified value still stays out of the message and the
state, because a plain logger drops it.

| Class | Attribute | What it holds | What the SDK's redactor does |
| --- | --- | --- | --- |
| `Secret` | `[ZabbixSecret]` | Tokens, passwords, and pre-shared keys: `ZabbixClientOptions.ApiToken`, `ZabbixListenerOptions.PreSharedKeys`, `ZabbixConnectorOptions.BearerToken` | Erases it in every environment |
| `ItemKey` | `[ZabbixItemKeyData]` | Item keys | Keeps the name and masks the parameters (`ZabbixItemKeyRedactor`): `web.page.get[host,,user:password]` is logged as `web.page.get[***]` |
| `PeerText` | `[ZabbixPeerText]` | A server's, proxy's, or agent's own explanation | Writes `[redacted]` (`ZabbixPeerTextRedactor`), because Zabbix echoes the request into it |
| `Personal` | `[ZabbixPersonal]` | User names | Nothing: your fallback redactor handles it, as it handles any class without a redactor. The default fallback erases it |

- `SetZabbixRedactors` sets redactors for the SDK's classes and nothing else. The fallback, the logger's options, and every other
  library's classes stay yours.
- The item-key redactor reads a key with the logger's discriminator on or off, so a bare key keeps its name either way.
- Every package's application sets the redactors the same way: the extension is in `Community.Zabbix.Abstractions`.
- Exported spans aren't redacted, so they carry error types and never messages.

To see a class in full on a trusted sink, set a pass-through after the SDK's redactors:

```csharp
using Microsoft.Extensions.Compliance.Redaction;

builder.Logging.EnableRedaction();
builder.Services.AddRedaction(redaction => redaction
    .SetZabbixRedactors()
    .SetRedactor<NullRedactor>(ZabbixDataClassifications.ItemKey));   // trusted sinks only
```

## Run a local collector

The Aspire Dashboard is an OTLP collector with a viewer for all three signals:

```bash
podman run --rm -d --name aspire-dashboard -p 18888:18888 -p 4317:18889 mcr.microsoft.com/dotnet/aspire-dashboard
export OTEL_EXPORTER_OTLP_ENDPOINT=http://localhost:4317
dotnet run --project <your app>
```

Open `http://localhost:18888`. The login token is in `podman logs aspire-dashboard`. Traces, metrics, and structured logs appear under the
application's service name.

## In an application with a host

The host's own builder subscribes once, for every registration:

```csharp
using OpenTelemetry;                                 // UseOtlpExporter
using Community.Zabbix.Diagnostics;                  // ZabbixTelemetryNames

builder.Logging.EnableRedaction();
builder.Services.AddRedaction(redaction => redaction.SetZabbixRedactors());
builder.Services.AddOpenTelemetry()
    .WithTracing(t => t.AddSource(ZabbixTelemetryNames.Subscription))      // "Community.Zabbix.*"
    .WithMetrics(m => m.AddMeter(ZabbixTelemetryNames.Subscription))
    .WithLogging()
    .UseOtlpExporter();                    // reads OTEL_EXPORTER_OTLP_ENDPOINT and the other OTEL_* variables
```

## Use it from PowerShell

The modules write the SDK's log to PowerShell's streams:

| Stream | What it shows |
| --- | --- |
| Verbose | Trace records, and the `Polly` lines, so an unreachable server is one error record and not a warning per try |
| Debug | Debug records |
| Information | Information records |
| Warning, error | Warnings and errors |

To export over OTLP, set `OTEL_EXPORTER_OTLP_ENDPOINT` before `Import-Module`. Otherwise, the OpenTelemetry SDK is never loaded.

```powershell
$env:OTEL_EXPORTER_OTLP_ENDPOINT = 'http://localhost:4317'
$env:OTEL_SERVICE_NAME = 'zabbix-scripts'          # optional; Community.Zabbix.PowerShell when unset
Import-Module Community.Zabbix
(Get-ZbxContext).ExportsTelemetry                  # True
```

- One set of exporters serves the process and every connection and session in it.
- The exporters read the other `OTEL_*` variables themselves (protocol, headers).
- Exported logs start at `Information`. Configure them with `Logging__LogLevel__Default` and `Logging__LogLevel__<category>` in the environment.
- Pending telemetry is flushed when a connection closes (`Disconnect-ZbxServer`, a closing runspace, `Remove-Module`), within 10 seconds.

## See also

- [The API client](api-client.md), [Listeners](listeners.md), [Proxies](proxies.md), and [Stores](stores.md): what the instruments count.
- [PowerShell](powershell.md): the modules' streams and sessions.
- [Known limitations](../KNOWN-LIMITATIONS.md#behaviour): item keys in logs, peer text, and refusals logged once an hour.
- [SECURITY.md](../../SECURITY.md): which secrets the SDK handles and how to report a leak.
- The [WorkerService sample](../../samples/README.md): a hosted application to subscribe from.
