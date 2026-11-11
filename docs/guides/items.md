<!--
This Source Code Form is subject to the terms of the Mozilla Public
License, v. 2.0. Copyright (c) 2026 schroeder247. SPDX-License-Identifier: MPL-2.0
-->
# Items

An item key names what the Zabbix server wants from an agent: `agent.ping`, `vfs.fs.size[/,pfree]`, `app.queue.depth[orders]`. The
active and passive agents look up the key's name in one table, `Items`, and the handler there answers with a value or with the
reason the key isn't supported. The table is in the `Community.Zabbix.Agent` package.

```bash
dotnet add package Community.Zabbix.Agent
```

```csharp
using Community.Zabbix;

ZabbixItemKey<string> version = new("app.version");

await using ZabbixActiveAgent agent = ZabbixActiveAgent.CreateBuilder("web-01")   // the host, as the server knows it
    .AddServer("zbx-server-01")
    .AddItem(version, () => "2.4.1")
    .Build();
await agent.StartAsync();
```

A `ZabbixItemKey<T>` declares a key once, with its type, so the handler can't answer with the wrong one.

Every way of adding keys ends in that table, in C# and in PowerShell:

| To | C# | PowerShell |
| --- | --- | --- |
| Answer one key | `AddItem` on the agent's builder | `Register-ZbxAgentHandler -Name web -Key app.health -ScriptBlock { 'ok' }` |
| Add keys while the agent runs | `agent.Items.Add(…)` | `Register-ZbxAgentHandler` after `Start-`: a session's table is always live |
| Ship several keys as one unit | A `ZabbixItemProvider`, added with `AddItems`, or `agent.Items.Add(provider)` while the agent runs | Several `New-ZbxAgentHandler` bindings piped to `Register-ZbxAgentHandler` |
| [Load keys from a folder](plugins.md) | `AddPlugins(section)`, or `agent.Items.AddPlugins(options)` while the agent runs | `Start-ZbxActiveAgent -Name web -HostName web-01 -Server zbx-server-01 -Plugin Queues -PluginPath /opt/plugins` |
| Try a key without a server | `agent.Items.AnswerAsync(key, timeout)` | `Test-ZbxAgentItem 'app.queue.depth[orders]' -Session web` |
| Use the built-in keys: the four every agent answers, and [Zabbix's own keys](bundled-items.md) | `ZabbixItemDictionary.BuiltIn(hostname)`; `KeyRules`, `BundledItems` | Answered by every session; `-KeyRule`, `-NoBundledItems` |

The push agent (`ZabbixPushAgent`) answers no keys. It sends the values its own polls and sources collect to trapper items. See
[Agents](zabbix-agents.md#send-values-on-a-schedule).

## Answer a typed item

A delegate answers a key, its shape says what it needs, and the key's type fixes the type of the value it returns:

```csharp
ZabbixItemKey<long> queueDepth = new("app.queue.depth");
ZabbixItemKey<long> diskFree = new("app.disk.free");
ZabbixItemKey<long> pending = new("orders.pending");

ZabbixActiveAgentBuilder builder = ZabbixActiveAgent.CreateBuilder("web-01").AddServer("zbx-server-01");
builder.Services.AddSingleton<IOrderQueue, OrderQueue>();
builder
    .AddItem(queueDepth, () => Environment.ProcessorCount)                              // a value at hand
    .AddItem(diskFree, key => new DriveInfo(key[0]).AvailableFreeSpace)                 // app.disk.free[/]: the key's parameters
    .AddItem(pending, (IOrderQueue queue) => queue.Count);                              // a service from the agent's container

interface IOrderQueue
{
    long Count { get; }
}

sealed class OrderQueue : IOrderQueue
{
    public long Count => 0;
}
```

| Delegate | For |
| --- | --- |
| `Func<T>` | A value at hand: `() => queue.Count` |
| `Func<ZabbixItemKey, T>` | A key with parameters: `key => orders.Count(key[0])` |
| `Func<ZabbixItemKey, CancellationToken, ValueTask<T>>` | A read that waits on I/O, within the check's deadline: `(key, ct) => orders.CountAsync(key[0], ct)` |
| `Func<TService, T>` | A service from the agent's container: `(IOrderQueue queue) => queue.Count` |
| `Func<TService, CancellationToken, ValueTask<T>>` | A service, and a read that waits on I/O |

- `AddItem` returns the builder, so calls chain, and the same verbs are on the builder `AddZabbixActiveAgent` and
  `AddZabbixPassiveAgent` return.
- A service is resolved for each check in a scope of its own, as a web host does for a request, so a scoped service such as a
  `DbContext` works. A service the container doesn't have fails `Build()`.
- A parameter the key doesn't have, or one your code can't read, throws, which makes the answer "not supported", with the reason.
- The value is written as Zabbix reads it: `1` and `0` for a `bool`, seconds for a `TimeSpan`, and Unix seconds for a `DateTimeOffset`.

More than one service, or state kept between checks, is a [provider](#ship-a-set-of-keys-as-a-provider).

## Answer a key with a handler

For a key named only at run time, give its name and answer with text, or with the whole answer:

```csharp
ZabbixActiveAgentBuilder builder = ZabbixActiveAgent.CreateBuilder("web-01")
    .AddServer("zbx-server-01")
    .AddItem("app.version", _ => "2.4.1")                                     // text
    .AddItem("app.orders", async (key, ct) => (await Orders.CountAsync(ct)).ToString())    // asynchronous, within the deadline
    .AddItem("app.feature", (key, cancellationToken) => ValueTask.FromResult(
        Features.IsOn(key[0])
            ? ZabbixAgentAnswer.Supported(key, "1")
            : ZabbixAgentAnswer.NotSupported(key, $"no feature named {key[0]}")));   // the answer, not supported included
```

`Orders` and `Features` stand for your application's own code.

- A handler receives the whole key, a `ZabbixItemKey`, with its parameters (`key[0]`, `key.Parameters`).
- It answers text, also asynchronously, or a `ZabbixAgentAnswer` when it decides for itself that a key isn't supported.
- `agent.Items.Add` takes the same handlers while the agent runs, and a whole number or a number too, formatted for the invariant
  culture.

The passive agent (`ZabbixPassiveAgent`, in `Community.Zabbix.Proxy`) has the same verbs and the same `Items`. See
[Agents](zabbix-agents.md#answer-the-servers-polls).

## Add a key while the agent runs

```csharp
await agent.StartAsync();

agent.Items
    .Add("app.late", _ => "added after the start")            // answered from the next check on
    .Add(new ZabbixItemKey<long>("app.later"), () => 1);
agent.Items.Remove("app.late");                               // not supported from the next check on
```

The agent answers from `Items` as it stands at each check. `Items.Add` takes the delegates `AddItem` takes, except the ones that take a service, and
returns `Items`, so the calls chain. Each agent owns its table, and [one set of rules](#the-rules-every-key-follows) holds for every
key in it.

## Ship a set of keys as a provider

A `ZabbixItemProvider` is a set of keys that belong together, with a connection, settings, or a lifetime they share. It lists its
keys and answers them. A library implements it against `Community.Zabbix.Abstractions` alone:

```csharp
using System.Globalization;
using Community.Zabbix;

public sealed class QueueItems(IQueueReader queues) : ZabbixItemProvider    // IQueueReader: the library's own client
{
    public override IReadOnlyList<ZabbixItemDefinition> Items { get; } =
        [new("app.queue.depth", "Messages waiting in a queue") { TestKey = "app.queue.depth[orders]" }];

    public override async ValueTask<ZabbixAgentAnswer> GetValueAsync(ZabbixItemKey key, CancellationToken cancellationToken)
        => ZabbixAgentAnswer.Supported(key, (await queues.DepthAsync(key[0], cancellationToken)).ToString(CultureInfo.InvariantCulture));
}
```

The active and passive agents' builders take it with `AddItems`, and a running agent through `Items`:

```csharp
ZabbixActiveAgentBuilder builder = ZabbixActiveAgent.CreateBuilder("web-01")
    .AddServer("zbx-server-01")
    .AddItems(new QueueItems(new QueueReader("Server=mq-01")));       // a provider you built, which stays yours

agent.Items.Add(new QueueItems(new QueueReader("Server=mq-02")));     // the same, while the agent runs
```

| On an agent's builder | Builds the provider | Who disposes it |
| --- | --- | --- |
| `AddItems(provider)` | You did | You: it stays yours |
| `AddItems(services => …)` | Your factory, from the agent's container | The agent's container |
| `AddItems<TProvider>()` | The agent's container, with constructor injection | The agent's container |

A constructor's parameters come from the agent's container, so you must register what the provider needs there first
(`builder.Services.AddSingleton<IQueueReader>(…)`).

- **A key name is one provider's.** `Items.Add` refuses a provider that lists a name already held, with `ArgumentException`,
  naming both providers, and adds none of its keys. Two providers of one builder that list one name fail `Build()`, or the host's
  start.
- **The table reads `Items` once**, when the provider is added. The agent asks `GetValueAsync` only for the names listed, with the
  whole key and its parameters.
- **Checks run concurrently.** A provider answers concurrent checks, so you must make it thread-safe.
- **`ZabbixItemDefinition`** names a key without parameters (`app.queue.depth`), says what it measures, and can give a whole key
  to try it with (`TestKey`), as Zabbix's loadable modules list their keys.

## Try a key without a server

```csharp
ZabbixItemDictionary table = ZabbixItemDictionary.BuiltIn("web-01").Add("app.version", _ => "2.4.1");
ZabbixAgentAnswer answer = await table.AnswerAsync(ZabbixItemKey.Parse("app.version", null), TimeSpan.FromSeconds(3));
Console.WriteLine(answer.IsSupported ? answer.Value : answer.Error);     // 2.4.1
```

`AnswerAsync` answers as an agent does:

| The key | The answer |
| --- | --- |
| Has no handler | "This agent does not answer the key." |
| Has a handler that runs past the deadline | "The key did not answer before its deadline." |
| Has a handler that throws | Not supported, with its message |

To try a provider, call its own `GetValueAsync`. To test the whole path a server takes, start a passive agent on port `0` and call
[`ZabbixWireClient.GetAgentItemAsync`](wire-client.md#read-an-item-from-an-agent) on the loopback address.

## The rules every key follows

```csharp
agent.Items["agent.hostname"] = (key, cancellationToken) =>           // the indexer replaces; Add refuses a name held
    ValueTask.FromResult(ZabbixAgentAnswer.Supported(key, "web-01"));
```

- **One table.** `Items` behaves as a dictionary does. `Add` refuses a name held, with `ArgumentException`, and the indexer, like
  a PowerShell registration, replaces. A name has no parameters: use `app.queue.depth`, not `app.queue.depth[orders]`.
- **The built-in keys fill what's free.** When the agent starts, it adds the four keys every agent answers and, unless
  `BundledItems` is off, the [bundled items](bundled-items.md), under every name left free. So a key you added first answers
  instead of Zabbix's. After the start those names are held, so you must use the indexer to replace one.
- **Providers follow the same rule.** `Add(provider)` adds a provider's keys all or none.
- **Allowed keys.** The agent's `KeyRules`, Zabbix's `AllowKey` and `DenyKey` lines, decide which keys it answers. A denied key is
  answered as one nobody answers, and `system.run` is denied unless a rule allows it.
- **Options.** The agent reads its options, `KeyRules` and `BundledItems` among them, when it starts. `Build()`, or `StartAsync` for
  an agent you construct, throws `InvalidOperationException` saying what to set when they don't validate.
- **Not supported.** A key with no handler isn't supported. A handler that throws makes the check not supported with its message
  (event 3211, logged the first time each key throws).
- **Deadlines and concurrency.** Each check runs within the item's timeout, or the agent's `CheckTimeout` (3 s) for a key without
  one, which reaches the handler's cancellation token. The active agent runs up to `MaxConcurrentChecks` checks at once.

## In an application with a host

The builders `AddZabbixActiveAgent` and `AddZabbixPassiveAgent` return have the verbs `CreateBuilder` has:

```csharp
ZabbixItemKey<string> version = new("app.version");

builder.Services.AddZabbixActiveAgent("web-01")
    .AddServer("zbx-server-01")
    .AddItems(new QueueItems(new QueueReader("Server=mq-01")))
    .AddItem(version, () => "2.4.1");
```

- Providers are added first and each `AddItem` then replaces, so your own answer wins whichever order you registered them in.
- A provider that lists a name another provider holds fails the agent's start.
- A delegate that takes a service resolves it from the application's container.
- An active and a passive agent of one name answer the same items, as one Zabbix agent does.
- To add a key while the agent runs, resolve the agent and do the same:
  `GetRequiredKeyedService<ZabbixActiveAgent>("web-01").Items.Add(…)`.

### Offer one registration for both agents

A library offers one registration through `IZabbixItemsBuilder`, which every active and passive agent's builder is. Name its
options after the agent, so two agents in one container keep their own. The factory form reads them for the agent the provider
belongs to:

```csharp
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using Community.Zabbix;

public sealed class QueueOptions
{
    public string ConnectionString { get; set; } = "";
}

public static class QueueItemsRegistration
{
    public static TBuilder AddQueueItems<TBuilder>(this TBuilder builder, Action<QueueOptions> configure)
        where TBuilder : IZabbixItemsBuilder
    {
        string agent = builder.Name;
        builder.Services.Configure(agent, configure);                    // this agent's options
        builder.AddItems(services => new QueueItems(new QueueReader(
            services.GetRequiredService<IOptionsMonitor<QueueOptions>>().Get(agent).ConnectionString)));
        return builder;
    }
}
```

The caller writes `.AddQueueItems(o => o.ConnectionString = "Server=mq-01")` on either agent's builder, and the chain keeps its
type. On a running agent, `Items.Register` runs the same registration, in a small container that's released with the agent, and
returns `Items`:

```csharp
agent.Items.Register(items => items.AddQueueItems(o => o.ConnectionString = "Server=mq-01"));
```

## Answer keys from PowerShell

A session of the `Community.Zabbix.Agent` module is an agent built as above, with the module's logging, clock, and meters.
`Register-ZbxAgentHandler` turns a scriptblock into a handler and adds it to the agent's `Items`, so a scriptblock is answered
exactly as a C# handler is, from the next check on:

```powershell
Import-Module Community.Zabbix.Agent
Start-ZbxPassiveAgent -Name web -HostName web-01 -AllowedPeer '127.0.0.1', '10.0.0.0/24' -Port 10050
Register-ZbxAgentHandler -Name web -Key app.health -ScriptBlock { 'ok' }
Register-ZbxAgentHandler -Name web -Key app.queue.depth -ScriptBlock { param($key) @(Get-ChildItem "/var/spool/app/$($key[0])").Count }
Test-ZbxAgentItem 'app.queue.depth[orders]' -Session web                 # as the agent answers it, without a server
```

- **Its key and value.** A scriptblock's first argument is the parsed `ZabbixItemKey`, and its last output, as text, is the value.
  A scriptblock that throws answers not supported with its message until it answers again.
- **Where it runs.** In the session's own runspace, with `Community.Zabbix` and the agent module imported, but not your variables,
  functions, or connections. A scriptblock that calls the API must connect itself. `$script:` variables persist between calls, and
  a session's scriptblocks run one at a time.
- **Several keys as a unit.** `New-ZbxAgentHandler` builds a key and scriptblock binding. A script that writes several is a set of
  keys, piped into one session or several: `.\QueueKeys.ps1 | Register-ZbxAgentHandler -Name web`.
- **C# providers in a session.** `-Plugin`, `-PluginPath`, and `-PluginSetting` on `Start-ZbxActiveAgent` and
  `Start-ZbxPassiveAgent` load [plugins](plugins.md) as `AddPlugins` does. A scriptblock registered under a plugin's key replaces
  it.

## See also

- [Agents](zabbix-agents.md): the active agent's schedule, upload, and sessions, the passive agent, and the push agent.
- [Plugins](plugins.md): item keys loaded from a folder.
- [Bundled items](bundled-items.md): Zabbix's own keys every agent answers, by family and platform.
- [PowerShell](powershell.md): the agent and proxy sessions.
- [Known limitations](../KNOWN-LIMITATIONS.md).
- The [ActiveAgent, PassiveAgent, and ItemPlugin samples](../../samples/README.md).
