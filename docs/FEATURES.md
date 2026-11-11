<!--
This Source Code Form is subject to the terms of the Mozilla Public
License, v. 2.0. Copyright (c) 2026 schroeder247. SPDX-License-Identifier: MPL-2.0
-->
# Features

What each package and module does. To choose one, see [Key concepts](../README.md#key-concepts); to install one, see [Install](INSTALL.md).

## .NET packages

### `Community.Zabbix.Sdk`: the API client

- Every method of the Zabbix 7.0, 7.4 and 8.0 API, 244 methods on 68 objects, as typed clients (`zabbix.Hosts`,
  `zabbix.Items`, …), read with a lambda (`zabbix.Hosts.GetAsync(q => q.Search.VisibleName = "web")`), and created from the
  fields they require (`zabbix.Hosts.CreateAsync("web-02", [groupId])`).
- A model generated from each Zabbix version's own source: a method or field the connected server's version does not
  have is refused before anything is sent.
- API tokens, or a user name and password whose session is renewed when the server ends it.
- A client built in one line from the frontend's address
  (`new ZabbixClient("https://zabbix.example/", token)`), one with more to set from `ZabbixClient.CreateBuilder()`, or named
  clients registered with `AddZabbix("prod")` and bound from `appsettings.json`, with the same verbs (`WithEndpoint`,
  `WithToken`, `WithTimeout`, `ConfigureResilience`) and the same HTTP pipeline either way.
- Timeouts, a circuit breaker and a concurrency limit on every call; a read is retried with backoff on a transient
  failure, a write never is.
- A tree of hosts, groups and items: `zabbix.Hosts["web-01"].GetLastAsync(ZabbixItemKeys.Cpu.Load())` reads a value as its
  type, `zabbix.HostGroups["Linux"].Hosts["web-01"]` reaches a host through its group, and `GetLatestAsync` and
  `GetHistoryAsync` read values as streams.
- Every asynchronous method takes a `CancellationToken` last, by position (`zabbix.Hosts.GetAsync(stoppingToken)`), and one
  with several forms has each with a token and without one.
- `ZabbixItemKey<T>`, a key with its type, and `ZabbixItemKeys`, Zabbix's own keys with theirs.
- Watchers that poll a host, a group or an item and yield each new value once.
- The mass-update methods Zabbix 8.0 removed, emulated on 8.0.
- A link to any object's page in the web frontend.
- OpenTelemetry traces and metrics, and logs with fixed event ids that keep secrets and item keys out.

### `Community.Zabbix.Wire`: the Zabbix protocol

`ZabbixWireClient` speaks to a server, a proxy or an agent. `ZabbixClient` speaks JSON-RPC to the frontend.

- `SendAsync` sends values to trapper items, as `zabbix_sender` does: `wire.SendAsync("web-01", requests, 42)`. It follows HA
  nodes and the server's redirects, and reports how many values the server did not accept.
- `GetAgentItemAsync` reads an item from an agent, as `zabbix_get` does.
- `GetStatusAsync`, `GetStatsAsync` and `GetQueueAsync` ask a server for its status, statistics and queue, as the web
  frontend does.
- It names a peer as `host` or `host:port`, as Zabbix's own configuration spells one.
- `new ZabbixWireClient("zbx-server-01")` takes a server. `ZabbixWireClient.CreateBuilder()` sets more, such as HA nodes, a token,
  TLS and logging, and checks it all when it builds. `AddZabbixWireClient("prod")` registers a named client in a host with the same
  verbs, and `IZabbixWireClientFactory` returns it.
- TLS with certificates or a pre-shared key (PSK), set with Zabbix's own setting names, including certificate
  revocation lists and issuer and subject checks.

### `Community.Zabbix.Agent`: agents in your application

- `ZabbixPushAgent.CreateBuilder("web-01")` and `ZabbixActiveAgent.CreateBuilder("web-01")` build an agent with no host, which
  checks its options when it builds and stops when it's disposed. `AddZabbixPushAgent("web-01")` and
  `AddZabbixActiveAgent("web-01")` register the same classes as hosted services on the .NET generic host, with the same verbs.
  The name is the agent's host unless set. They do not need the API client.
- The push agent collects values from your own sources on a schedule and sends them to Zabbix: `AddPoll` reads a typed
  item, from a value at hand or a service of the agent's container, and `AddSource` takes any stream of values.
- `AddMeter` sends what your application's `System.Diagnostics.Metrics` meter records, with no key declared twice.
- The active agent runs Zabbix active checks: the server says which item keys to collect, and your items answer them.
  `AddItem` takes a delegate whose shape says what it needs (a value at hand, the key's parameters, a read that waits on
  I/O, or a service), and the key's type fixes the type of the value.
- `AddServer` adds a destination, and several nodes in one call are an HA cluster.
- Item providers: a library's keys, implemented against `Community.Zabbix.Abstractions` alone, added to an active or a
  passive agent with `AddItems`, or `agent.Items.Add` while it runs.
- Zabbix's own item keys, answered by every active and passive agent: memory, processors, file systems, processes,
  files, network interfaces and services, web pages and certificates, with Zabbix's parameters and values, and
  Zabbix's `AllowKey` and `DenyKey` rules to restrict them.
- A send loop that batches and retries, and keeps each value until the server confirms it.
- In-memory and durable stores (`UseStore`, `UseMemoryStore`), the same verbs on a push agent's and a proxy's builder, and
  health checks for the application's health endpoint.

### `Community.Zabbix.Proxy`: listeners and proxies

- A trapper that accepts values from `zabbix_sender` and agents.
- A passive agent that answers the server's polls with your handlers.
- An active and a passive proxy: configuration from the server, and data uploaded through a durable buffer.
- The HTTP endpoint a Zabbix connector streams item values and events to.
- Lists of allowed peers, TLS and PSK, on Kestrel's socket transport without starting a web server.
- `ZabbixPassiveAgent.CreateBuilder("web-01")` builds a passive agent with no host, and `AddZabbixPassiveAgent("web-01")`
  registers one in a host, with the same verbs: `AllowPeer` takes an `IPAddress`, an `IPNetwork`, or a DNS name, and
  `AllowPsk` a peer's key as bytes.
- `ZabbixActiveProxy.CreateBuilder("proxy-01")` and `ZabbixPassiveProxy.CreateBuilder("dmz-proxy")` build the proxies, and
  `AddZabbixActiveProxy` and `AddZabbixPassiveProxy` register them in a host, with the same verbs: `AddServer` for the one
  server or HA cluster an active proxy reports to, `WithVersion`, `WithPsk` for the proxy's own key, `AllowPeer` and
  `AllowPsk`, the store's verbs, `AddTrapper`, `AddProvider`, and `OnTask`, `OnConfig`, and `OnStats` for what answers the
  server.
- `ZabbixListener.CreateBuilder()` builds a listener of your own on Kestrel's socket transport (`UseSocketTransport`), with
  `AllowPeer`, `AllowPsk`, `WithCertificate`, and `OnRequest`. A trapper is a source of a push agent (`AddTrapper`), with the
  options a proxy's trapper takes. The connector endpoint maps into an ASP.NET Core application.

### `Community.Zabbix.Agent.Storage.EntityFrameworkCore`: a durable store

- Keeps values in a database until the server has them, so a restart loses nothing, on the EF Core provider you
  choose: `UseEntityFrameworkStore(db => db.UseSqlite("Data Source=agent.db"))` on a push agent's or a proxy's builder.

### `Community.Zabbix.Agent.Plugins`: plugins from a folder

- Loads item providers from a folder the configuration names, as Zabbix's agent loads modules: only listed
  plugins, each in a load context of its own, so plugins can depend on different versions of one package.
- A plugin that cannot be loaded stops the agent from starting; on Linux and macOS, so does a plugin whose files, or a
  folder above it, other accounts can write.

## PowerShell modules

### `Community.Zabbix` (267 cmdlets)

- `Connect-ZbxServer` with an API token or a user name and password, and several named connections in one session.
- `Get-`, `New-`, `Set-` and `Remove-` commands for every API object, with pipeline input, `-WhatIf` and `-Confirm`,
  and tab completion of names from the server.
- Configuration export, import, comparison and testing in JSON, XML or YAML, and copies of hosts, templates and items.
- History and trends, live watches of items, hosts and groups, and problem acknowledgement.
- `Send-ZbxData` and `Get-ZbxAgentItem` over the Zabbix protocol, and the server's queue and status.
- `Show-ZbxPage` opens any object's page in the web frontend.
- Help with examples for every command.

### `Community.Zabbix.Agent` (20 cmdlets)

- Runs an active or a passive agent in a PowerShell session, with item handlers written as scriptblocks.
- Loads the same plugins a .NET agent loads (`-Plugin`, `-PluginPath`, `-PluginSetting`).
- Sets up the agent's host in Zabbix and exports its items as a template.
- Shows a running agent's status and log, and tests an item key without a server (`Test-ZbxAgentItem`).

### `Community.Zabbix.Proxy` (23 cmdlets)

- Runs an active or a passive proxy in a PowerShell session, with scriptblocks that provide its data.
- Builds, sends and receives proxy protocol messages (`New-ZbxProxyData`, `Send-ZbxProxyData`, `Receive-ZbxProxyConfig`).
- Reads the stream a Zabbix connector sends (`Read-ZbxConnectorStream`).
