<!--
This Source Code Form is subject to the terms of the Mozilla Public
License, v. 2.0. Copyright (c) 2026 schroeder247. SPDX-License-Identifier: MPL-2.0
-->
# Community Zabbix

[![CI](https://github.com/schroeder247/community-zabbix/actions/workflows/ci.yml/badge.svg)](https://github.com/schroeder247/community-zabbix/actions/workflows/ci.yml)
[![Docs](https://github.com/schroeder247/community-zabbix/actions/workflows/docs.yml/badge.svg)](https://github.com/schroeder247/community-zabbix/actions/workflows/docs.yml)
[![License: MPL-2.0](https://img.shields.io/badge/license-MPL--2.0-blue.svg)](LICENSE)
[![Status: alpha](https://img.shields.io/badge/status-alpha-orange.svg)](#status)

A .NET SDK and PowerShell modules for Zabbix 7.0, 7.4, and 8.0.

Use Community Zabbix to:

- Manage Zabbix through its API: hosts, templates, items, triggers, users, and every other API object.
- Send values to Zabbix and read values from agents, as `zabbix_sender` and `zabbix_get` do.
- Report your application's metrics as a Zabbix agent, and answer item keys with your own code.
- Run a trapper, a passive agent, or a proxy inside your application or script.

[Documentation](docs/INDEX.md) | [API reference](https://schroeder247.github.io/community-zabbix/api/) | [Command reference](docs/reference/Community.Zabbix/README.md) | [Samples](samples/README.md) | [Changelog](CHANGELOG.md)

<p><img src="assets/third-party/zabbix-logo-2025.png" alt="Zabbix logo" width="200"> &nbsp; works with Zabbix 7.0, 7.4 and 8.0</p>

This project is not affiliated with, endorsed by or sponsored by Zabbix LLC. Zabbix is a registered trademark of Zabbix LLC.
Provided "as is", without warranty of any kind: use at your own risk.

> [!WARNING]
> **No warranty. Use at your own risk.** This software is provided "AS IS." The author **cannot be held liable** for any operational impact, data loss, or system damage that may result from its use — including corrupted configuration, lost monitoring data, locked-out accounts, or leaked credentials. **Test in a non-production environment first.** See the [Disclaimer of Warranty](docs/LEGAL.md#disclaimer-of-warranty) below and the full [LICENSE](LICENSE) for legal terms.

## Status

Community Zabbix is in alpha, and there's no release yet. The packages aren't on nuget.org and the modules aren't on the
PowerShell Gallery: to try them, build them [from source](docs/INSTALL.md#from-source). Until 1.0.0, the API can change in
any commit.

## Getting started

### Prerequisites

- **.NET:** the .NET 10 SDK.
- **PowerShell:** PowerShell 7.6 or later, on Windows, macOS, or Linux.
- **Zabbix:** a 7.0, 7.4, or 8.0 server, and an API token if you use the API.

### Install

There's no release yet. Until 1.0.0, [build the packages and modules from source](docs/INSTALL.md#from-source); the commands
below are how you'll install a release.

Pick what you need. Each package brings the ones it depends on.

| To | .NET package | PowerShell module |
| --- | --- | --- |
| Manage and query Zabbix through its API | `Community.Zabbix.Sdk` | `Community.Zabbix` |
| Send values, read agents, and ask a server for its status | `Community.Zabbix.Wire` | `Community.Zabbix` |
| Run a push or active agent | `Community.Zabbix.Agent` | `Community.Zabbix.Agent` |
| Run a passive agent, a proxy, a trapper, or a connector endpoint | `Community.Zabbix.Proxy` | `Community.Zabbix.Agent`, `Community.Zabbix.Proxy` |
| Keep unsent values in a database across restarts | `Community.Zabbix.Agent.Storage.EntityFrameworkCore` | |
| Load an agent's item keys from a folder | `Community.Zabbix.Agent.Plugins` | `Community.Zabbix.Agent` |

```bash
dotnet add package Community.Zabbix.Sdk
```

```powershell
Install-PSResource Community.Zabbix
```

To install from a GitHub release or from source, see [Install](docs/INSTALL.md).

### Create a client

The API client needs the frontend's address and an API token. Keep the token out of your source:

```csharp
using Community.Zabbix;

string token = Environment.GetEnvironmentVariable("ZABBIX_API_TOKEN")!;
await using var zabbix = new ZabbixClient("https://zabbix.example/", token);

Console.WriteLine(await zabbix.GetApiVersionAsync());
```

To set more, such as timeouts, retries or logging, start with `ZabbixClient.CreateBuilder()`. See
[The API client](docs/guides/api-client.md#create-a-client).

For a whole program, follow [Getting started](docs/GETTING-STARTED.md), or [Getting started with PowerShell](src/Zabbix.PowerShell/GETTING-STARTED.md).

## Key concepts

### Two clients, one for each protocol

Zabbix talks in two ways, and the SDK has a client for each:

| Client | Protocol | Reaches | Use it to |
| --- | --- | --- | --- |
| `ZabbixClient` | JSON-RPC over HTTP | The frontend | Read and change configuration, history, and events |
| `ZabbixWireClient` | The Zabbix protocol: framed messages over TCP | A server, a proxy, or an agent | Send values, read an agent's item, ask a server for its status and queue |

Both are thread-safe. Create one and share it, and dispose it when you're done.

### Agents and proxies

These classes take the other side of the conversation. Build one with its `CreateBuilder`, and start it:

| Class | What it does | Package |
| --- | --- | --- |
| `ZabbixPushAgent` | Collects values on your schedule and sends them, keeping each one until the server has it | `Community.Zabbix.Agent` |
| `ZabbixActiveAgent` | Asks the server which items to collect, and answers them | `Community.Zabbix.Agent` |
| `ZabbixPassiveAgent` | Listens, and answers the server's polls | `Community.Zabbix.Proxy` |
| `ZabbixActiveProxy` | Fetches its configuration from a server, and uploads what it collects | `Community.Zabbix.Proxy` |
| `ZabbixPassiveProxy` | Waits for a server to send its configuration and fetch its data | `Community.Zabbix.Proxy` |

### Items

An item key, such as `system.cpu.load[all,avg1]`, names what an agent collects. An active or passive agent answers Zabbix's
own keys out of the box. To answer your own, add them to its `Items`, in code or as a provider or plugin.

### On its own or in a host

Every class works on its own: construct it, use it, and dispose it. You don't need a dependency-injection container. In an
application with a host, the same classes register as services and start and stop with the application.

## Examples

- [Query the API](#query-the-api)
- [Send a value](#send-a-value)
- [Read an item from an agent](#read-an-item-from-an-agent)
- [Ask a server for its status](#ask-a-server-for-its-status)
- [Run an agent](#run-an-agent)
- [Answer your own item keys](#answer-your-own-item-keys)
- [Use PowerShell](#use-powershell)
- [Register in a host](#register-in-a-host)

### Query the API

`ZabbixClient` has a client for every API object, `zabbix.Hosts`, `zabbix.Items`, and so on. Each takes a lambda for what to read, the
fields a create requires and a lambda for the rest, and an indexer for one object, which updates it with a lambda too.
`ZabbixItemKeys` declares Zabbix's own keys with their types.

```csharp
using System.Net;

var hosts = await zabbix.Hosts.GetAsync(q =>
{
    q.Search.VisibleName = "web";
    q.Output = [ZabbixHostFields.HostId, ZabbixHostFields.VisibleName];   // a host's fields only
    q.Limit = 10;
});

ulong linux = await zabbix.HostGroups.CreateAsync("Linux servers");
ulong id = await zabbix.Hosts.CreateAsync("web-02", [linux], h =>
{
    h.Interfaces = [ZabbixInterface.Agent(IPAddress.Parse("10.0.0.2"))];
    h.Tags = [("env", "prod")];
});

await zabbix.Hosts["web-02"].UpdateAsync(h => h.Status = ZabbixHostStatus.Disabled);

double load = await zabbix.Hosts["web-01"].GetLastAsync(ZabbixItemKeys.Cpu.Load());
```

See [The API client](docs/guides/api-client.md) and [The tree and the watcher](docs/guides/tree-and-watcher.md).

### Send a value

The host and a trapper item with this key must exist on the server.

```csharp
using var wire = new ZabbixWireClient("zbx-server-01");     // port 10051 unless you name another

ZabbixItemKey<long> requests = new("app.requests");                // the key, with its type

ZabbixSenderResponse sent = await wire.SendAsync("web-01", requests, 42);

Console.WriteLine($"processed {sent.Processed}, failed {sent.Failed}");
```

See [The wire client](docs/guides/wire-client.md).

### Read an item from an agent

Each read names the agent, so one client reads from many. The agent's `Server` setting must allow your address.

```csharp
using var wire = new ZabbixWireClient();

double load = await wire.GetAgentItemAsync("web-01", ZabbixItemKeys.Cpu.Load());     // port 10050 unless you name another

Console.WriteLine(load);
```

### Ask a server for its status

You need a super admin's API token for the status and the queue.

```csharp
await using ZabbixWireClient wire = ZabbixWireClient.CreateBuilder()
    .WithServer("zbx-server-01")
    .WithToken(token)
    .Build();

JsonElement status = await wire.GetStatusAsync();
IReadOnlyList<ZabbixQueueDelays> queue = await wire.GetQueueAsync();
```

### Run an agent

A push agent samples on your schedule and retries through outages:

```csharp
ZabbixItemKey<long> memory = new("app.memory");

await using ZabbixPushAgent agent = ZabbixPushAgent.CreateBuilder("web-01")
    .AddServer("zbx-server-01")
    .AddPoll(memory, TimeSpan.FromSeconds(30), () => GC.GetTotalMemory(false))
    .Build();

await agent.StartAsync();
```

See [Agents](docs/guides/zabbix-agents.md) and [Stores](docs/guides/stores.md).

### Answer your own item keys

An active agent asks the server which keys a host has, and answers them:

```csharp
ZabbixItemKey<string> version = new("app.version");
ZabbixItemKey<long> diskFree = new("app.disk.free");

await using ZabbixActiveAgent agent = ZabbixActiveAgent.CreateBuilder("web-01")
    .AddServer("zbx-server-01")
    .AddItem(version, () => "2.4.1")
    .AddItem(diskFree, key => new DriveInfo(key[0]).AvailableFreeSpace)
    .Build();

await agent.StartAsync();
```

See [Items](docs/guides/items.md), [Plugins](docs/guides/plugins.md), and [Bundled items](docs/guides/bundled-items.md).

### Use PowerShell

The modules are built on the SDK. A scriptblock goes into the `Items` of the same class a C# program builds.

```powershell
Import-Module Community.Zabbix
Connect-ZbxServer -Uri https://zabbix.example/ -ApiToken (Read-Host -AsSecureString 'API token')

Get-ZbxHost -Name 'web-*' | Format-Table TechnicalName, VisibleName, Status
Send-ZbxData -Server zbx-server-01 -HostName web-01 -Key app.requests -Value 42

Import-Module Community.Zabbix.Agent
Start-ZbxActiveAgent -Name web -HostName web-01 -Server zbx-server-01
Register-ZbxAgentHandler -Name web -Key app.version -ScriptBlock { '2.4.1' }
```

See [PowerShell](docs/guides/powershell.md) and the [command reference](docs/reference/Community.Zabbix/README.md).
`Get-Help <command> -Examples` shows each command at work.

### Register in a host

In a worker service or an ASP.NET Core application, bind named clients and hosted agents from configuration, and add in code what
a file can't hold. `AddZabbixClient` and `AddZabbixActiveAgent` take the verbs their classes' `CreateBuilder` takes:

```csharp
HostApplicationBuilder builder = Host.CreateApplicationBuilder(args);
builder.Services.AddZabbixClients(builder.Configuration);       // Zabbix:Servers:{name}
builder.Services.AddZabbixClient("prod")                        // the server the file declares: what code adds
    .WithTimeout("configuration.import", TimeSpan.FromMinutes(5));
builder.Services.AddZabbixAgents(builder.Configuration);        // Zabbix:ActiveAgents:{name} and the other agents
builder.Services.AddZabbixActiveAgent("web-01")                 // the agent the file declares: its items
    .AddItem(new ZabbixItemKey<string>("app.version"), () => "2.4.1");
builder.Build().Run();
```

```json
{
  "Zabbix": {
    "Servers": { "prod": { "Endpoint": "https://zabbix.example/" } },
    "ActiveAgents": { "web-01": { "ServerActive": "zbx-server-01" } }
  }
}
```

Keep the token out of the file: set `Zabbix__Servers__prod__ApiToken` in the environment or in user secrets. See
[Configuration](docs/guides/configuration.md) and the [worker service sample](samples/README.md).

## Troubleshooting

| What you see | What to check |
| --- | --- |
| A send returns `Failed` above 0 | The host or key doesn't exist, the value has the wrong type, your address isn't in the item's allowed hosts, or the server hasn't loaded a new item yet. |
| `SocketException` or `TimeoutException` | The address, the port (10051 for a server, 10050 for an agent), and any firewall between you. |
| `ZabbixServerRefusedException` | Its `Info` holds the server's reason: often a session that isn't a super admin's, or an address `StatsAllowedIP` doesn't admit. |
| `InvalidOperationException` at the first request or at `StartAsync` | Its message says which option to set. |

For more, and for how to turn on logging, see [Troubleshooting](docs/TROUBLESHOOTING.md) and
[Known limitations](docs/KNOWN-LIMITATIONS.md).

## Next steps

Read these in order. Each guide starts with the piece on its own and, where it registers in a host, ends with that.

| Guide | What it covers |
| --- | --- |
| [Getting started](docs/GETTING-STARTED.md) | A program built step by step: create a host, send a value, read it back, and run an agent |
| [Getting started with PowerShell](src/Zabbix.PowerShell/GETTING-STARTED.md) | The same with the modules |
| [The API client](docs/guides/api-client.md) | Credentials, the generated clients, errors, retries, and version gating |
| [The tree and the watcher](docs/guides/tree-and-watcher.md) | Latest values and history by host and item, and watching them |
| [The wire client](docs/guides/wire-client.md) | Sending values, reading agents, and asking a server |
| [Agents](docs/guides/zabbix-agents.md) | The push, active, and passive agents, and their lifetime and health |
| [Items](docs/guides/items.md) | The keys an agent answers: handlers, providers, and scriptblocks |
| [Plugins](docs/guides/plugins.md) | Item keys loaded from a folder |
| [Bundled items](docs/guides/bundled-items.md) | Zabbix's own keys that every agent answers, and the rules that restrict them |
| [Stores](docs/guides/stores.md) | Keeping values until the server has them |
| [Proxies](docs/guides/proxies.md) | The active and passive proxies |
| [Listeners](docs/guides/listeners.md) | The trapper, the connector endpoint, and the pieces beneath them |
| [TLS and PSK](docs/guides/tls-and-psk.md) | Encrypting the wire, the agents, and the listeners |
| [Telemetry](docs/guides/telemetry.md) | Logs, traces, and metrics |
| [Configuration](docs/guides/configuration.md) | Binding everything from `appsettings.json` |
| [PowerShell](docs/guides/powershell.md) | The three modules: connections, commands, sessions, and help |

Also see the [samples](samples/README.md), the [feature list](docs/FEATURES.md), and the [documentation map](docs/INDEX.md), which
also lists the reference and design documents.

## Support and contributing

One maintainer answers issues and pull requests, on a best-effort basis, with no guaranteed response time. To report a
vulnerability, follow [SECURITY.md](SECURITY.md) and don't open a public issue. To build and test the code, see
[CONTRIBUTING.md](CONTRIBUTING.md).

## License

Community Zabbix is licensed under the [Mozilla Public License 2.0](LICENSE). The license, the trademark notice, and the
disclaimer of warranty are explained in [License, trademark, and warranty](docs/LEGAL.md).
