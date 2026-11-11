<!--
This Source Code Form is subject to the terms of the Mozilla Public
License, v. 2.0. Copyright (c) 2026 schroeder247. SPDX-License-Identifier: MPL-2.0
-->
# Configuration

Bind servers, agents, and listeners from one document: the `Zabbix` section of `appsettings.json`, environment variables, or any other
`IConfiguration` source. Each package binds the sections it builds.

```bash
dotnet add package Community.Zabbix.Sdk      # AddZabbixClients: the servers
dotnet add package Community.Zabbix.Agent    # AddZabbixAgents: the push and active agents (optional)
dotnet add package Community.Zabbix.Proxy    # AddZabbixProxies: the push agents with their trappers, the passive agents, and the proxies (optional)
```

`appsettings.json`, with no token in it:

```json
{
  "Logging": { "LogLevel": { "Default": "Information", "Polly": "Warning" } },
  "Zabbix": {
    "Servers": {
      "prod": {
        "Endpoint": "https://zabbix.example/",
        "Resilience": { "Retry": { "MaxRetryAttempts": 5 } }
      }
    }
  }
}
```

`Program.cs`:

```csharp
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Community.Zabbix;

HostApplicationBuilder builder = Host.CreateApplicationBuilder(args);
builder.Services.AddZabbixClients(builder.Configuration.GetSection(ZabbixConfigurationSections.Section));
builder.Logging.AddZabbixRedaction();

using IHost app = builder.Build();
ZabbixClient prod = app.Services.GetRequiredService<IZabbixClientFactory>().Create("prod");
Console.WriteLine(await prod.GetApiVersionAsync());
```

- The token comes from the environment (`Zabbix__Servers__prod__ApiToken`) or another secret source. See
  [Keep secrets out of the document](#keep-secrets-out-of-the-document).
- Each root also takes the whole `builder.Configuration`, and finds the `Zabbix` section in it.
- The `"Polly": "Warning"` line quiets the one `Information` line the standard resilience handler logs for every call. A failed
  attempt is still logged.

An agent's builder binds a section of your own with `Bind`, and an agent you construct takes the same options through its
constructor. See [Agents](zabbix-agents.md).

## Choose the sections to bind

Every section is a map from a name to one row of options. A row's keys are the option properties' names, in any case.
`ZabbixConfigurationSections` holds the section names as constants.

| Section | Bound by | Options of a row |
| --- | --- | --- |
| `Zabbix:Servers:{name}` | `AddZabbixClients` | `ZabbixOptions`, with a `Resilience` subsection in the shape of `HttpStandardResilienceOptions` |
| `Zabbix:PushAgents:{name}` | `AddZabbixAgents`, `AddZabbixProxies` | `ZabbixPushAgentOptions` |
| `Zabbix:PushAgents:{name}:Trapper` | `AddZabbixProxies` only | `ZabbixListenerOptions`: the agent's trapper listener |
| `Zabbix:ActiveAgents:{name}` | `AddZabbixAgents` only | `ZabbixActiveAgentOptions` |
| `Zabbix:PassiveAgents:{name}` | `AddZabbixProxies` only | `ZabbixPassiveAgentOptions` |
| `Zabbix:ActiveAgents:{name}:Plugins`, `Zabbix:PassiveAgents:{name}:Plugins` | `AddPlugins(section)` on the agent's builder, from `Community.Zabbix.Agent.Plugins`. No document root calls it. | `Path`, `Load`, and each plugin's settings under its name ([Plugins](plugins.md)) |
| `Zabbix:Proxies:{name}` | `AddZabbixProxies` only | `ZabbixProxyOptions`. `Mode` picks an active (the default) or a passive proxy. |

- A root binds only its own sections and leaves the rest alone, so one document can serve several processes.
- A process that hosts active agents and listeners calls both hosting roots. One that also calls the API adds `AddZabbixClients`.
- Each root reads the names once, when you call it. A name that a configuration reload adds isn't registered.
- A wire client has no section in the document. Its builder's `Bind` reads a section you name, with the names of `ZabbixWireOptions`.
  See [Register it in a host](wire-client.md#register-it-in-a-host).

## Configure a server

```json
"prod": {
  "Endpoint": "https://zabbix.example/",
  "MaxConnectionsPerServer": 20,
  "Timeouts": { "configuration.import": "00:05:00" },
  "Resilience": {
    "Retry": { "MaxRetryAttempts": 5 },
    "TotalRequestTimeout": { "Timeout": "00:01:00" }
  }
}
```

| Key | Default | Meaning |
| --- | --- | --- |
| `Endpoint` | Required | The frontend's address, `https://zabbix.example/`, whose `api_jsonrpc.php` the client calls. Or the JSON-RPC endpoint itself, `https://zabbix.example/api_jsonrpc.php`. |
| `ApiToken` | None | The API token, a secret. Without one, and without a credential in code, the server is anonymous. |
| `FrontendUri` | The endpoint's folder | Where the web frontend is, for page links. |
| `AllowUnencryptedCredentials` | `false` | Whether a credential may go to a plain `http` endpoint on another host. |
| `MaxConnectionsPerServer` | 100 | The most connections held open to the server. 0 is the platform's default. |
| `MaxResponseContentBytes` | 1 GiB | The largest reply the client reads. |
| `Timeouts` | None | A deadline per API method, replacing both the attempt and the total timeout for it. |
| `Resilience` | The standard handler's | `Retry`, `AttemptTimeout`, `TotalRequestTimeout`, `CircuitBreaker`, and `RateLimiter`. See [Change retries and timeouts](api-client.md#change-retries-and-timeouts). |

- The options are validated when the host starts. A missing `Endpoint`, a `FrontendUri` that isn't an absolute `http` or `https` URI, a
  token holding whitespace, a credential for plain `http`, and a non-positive timeout each fail with `OptionsValidationException`.
- The `Resilience` keys must meet the standard handler's own rules: the total timeout must exceed the attempt timeout.
- Only the `Resilience` subsection takes effect on a reload. The endpoint, token, connection cap, and per-method deadlines are read
  once, so changing them takes a restart.

## Configure agents and listeners

```json
{
  "Zabbix": {
    "PushAgents": {
      "edge-01": {
        "ServerActive": "zbx-server-01;zbx-server-02,zbx-dr",
        "Send": { "BatchSize": 250, "FlushInterval": "00:00:05" },
        "Trapper": { "Port": 10051, "AllowedPeers": [ "10.0.0.0/8" ] }
      }
    },
    "ActiveAgents": {
      "web-01": { "ServerActive": "zbx-server-01", "KeyRules": [ "DenyKey=vfs.file.contents[*]" ] }
    },
    "Proxies": {
      "proxy-01": { "Server": "zbx-server-01;zbx-server-02", "Trapper": { "Port": 10052, "AllowedPeers": [ "10.0.0.0/8" ] } }
    }
  }
}
```

```csharp
builder.Services.AddZabbixAgents(builder.Configuration);    // PushAgents and ActiveAgents
builder.Services.AddZabbixProxies(builder.Configuration);   // PushAgents with their Trapper, PassiveAgents, and Proxies
```

- **A row's name.** It's the agent's name, and its host name unless the row sets `Hostname`.
- **`ServerActive`.** `;` separates the nodes of one high-availability destination, and `,` separates one destination from another.
  In code, each `AddServer` adds one destination to it.
- **`AllowedPeers`.** Every listener fails closed. A trapper, passive agent, or proxy with an empty `AllowedPeers` fails validation at
  start, so you must write out "everyone" (`0.0.0.0/0,::/0`).
- **A proxy's `Server`.** One server, or the nodes of one high-availability cluster joined by `;`, as Zabbix's proxy reads its
  `Server`. A `,` fails validation at start, because a proxy reports to one server or cluster. In code, `AddServer` sets it.
- **A proxy's listener.** It's the proxy's `Trapper` subsection, the same options a push agent's `Trapper` has. A passive proxy
  (`"Mode": "Passive"`) must also set `Version` to the server's version.
- **An agent's `Trapper`.** Without `Community.Zabbix.Proxy`, this subsection isn't bound.
- **`KeyRules`.** On an active or passive agent, these are Zabbix's `AllowKey` and `DenyKey` lines, tried in order. Rules that don't
  parse, or that allow every key, fail validation at start.
- **`BundledItems`.** The agents answer Zabbix's own keys by default, and `"BundledItems": false` leaves them out. See
  [Bundled items](bundled-items.md).

## Combine a document with code

A row's name is the name of its options, the same name code registers: `AddZabbixClients` calls `AddZabbix(name, row)` for each server
row. Register the name again to add what a file can't hold, such as a credential built in code, a delegating HTTP handler, an agent's
polls and item handlers ([Items](items.md)), or a store.

```csharp
string password = builder.Configuration["ZabbixLogin:Password"]
    ?? throw new InvalidOperationException("ZabbixLogin:Password is not set.");

builder.Services.AddZabbixClients(builder.Configuration);              // prod: endpoint, resilience
builder.Services.AddZabbix("prod")                                     // the same client, continued
    .WithCredential(ZabbixCredential.FromLogin("api-reader", password));

builder.Services.AddZabbixAgents(builder.Configuration);               // edge-01: where to send, batching
builder.Services.AddZabbixPushAgent("edge-01")                         // edge-01: what to send
    .Configure(o => o.Hostname = "web-01")
    .AddPoll(new ZabbixItemKey<long>("app.requests"), TimeSpan.FromSeconds(30), () => Environment.ProcessorCount);
```

- Each binding and each verb is one step on the same named options, run in the order you registered them. For a value both set,
  the later step wins. A row's name is the agent's host name unless a step sets `Hostname`.
- A credential given in code and an `ApiToken` in the row are two credentials for one client, and validation refuses the pair.
- The document registers named clients only. For the unnamed client, which is also injected as plain `ZabbixClient`, bind one row
  directly: `builder.Services.AddZabbix(builder.Configuration.GetSection("Zabbix:Servers:prod"))`.

## Keep secrets out of the document

Never commit a token, password, or pre-shared key. Every key of the document has an environment variable form, with `__` for each `:`,
and any configuration source binds to the same key:

```bash
export Zabbix__Servers__prod__ApiToken="$(cat /run/secrets/zabbix-token)"   # environment variable
dotnet user-secrets set "Zabbix:Servers:prod:ApiToken" "<token>"           # user secrets, in Development
```

- `Host.CreateApplicationBuilder` reads environment variables after `appsettings.json`, so the variable wins. It reads user secrets in
  the Development environment when the project has a `UserSecretsId`.
- A token is one word. A token read from a file keeps its line end, and validation refuses whitespace, so trim it.
- The secret members (`ZabbixOptions.ApiToken`, `ZabbixTlsOptions.Psk`, and the session password) are marked `[ZabbixSecret]`, the
  `ZabbixDataClassifications.Secret` class. The SDK's own events never write their values.
- Call `builder.Logging.AddZabbixRedaction()`, in `Community.Zabbix.Sdk`, to erase any logged value of that class in every environment.
  An application without that package registers its own redactors ([known limitations](../KNOWN-LIMITATIONS.md#behaviour)).
- A configured secret is a string. It lives as long as the options and the configuration providers that hold it, and can't be zeroed
  ([SECURITY.md](../../SECURITY.md)).

## See also

- [The API client](api-client.md): what each server option and the resilience pipeline do.
- [Agents](zabbix-agents.md), [Proxies](proxies.md), and [Listeners](listeners.md): the options of each hosting row.
- [TLS and PSK](tls-and-psk.md): the `Connection:Tls` and listener TLS keys.
- [Telemetry](telemetry.md): the redaction policy and the log categories.
- [Get started with the SDK](../GETTING-STARTED.md): a server bound from `appsettings.json` in a whole program.
- The [WorkerService sample](../../samples/README.md).
