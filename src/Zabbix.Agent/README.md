# Community.Zabbix.Agent

Zabbix agents in a .NET application, built with their builders or registered as hosted services on the generic host, with the same
verbs: `ZabbixPushAgent` with its poll source and its send loop (retry, backoff, HA failover at connect), `ZabbixActiveAgent`
(Zabbix's `active checks`, each item on its own update interval, a `ZabbixInterval`), items answered by delegates and providers, the agent health
check; and the store every agent and proxy buffers in: `ZabbixStore<T>` (a
`ChannelWriter<T>` and an `IAsyncEnumerable<T>` with an acknowledged face), `ZabbixMemoryStore<T>` and `ZabbixPersistentStore<T>`,
the base of a durable store (`Community.Zabbix.Agent.Storage.EntityFrameworkCore` is one on EF Core).

```shell
dotnet add package Community.Zabbix.Agent
```

```csharp
using Community.Zabbix;

ZabbixItemKey<long> memory = new("app.memory");

await using ZabbixPushAgent agent = ZabbixPushAgent.CreateBuilder("web-01")
    .AddServer("zbx-server-01")
    .AddPoll(memory, TimeSpan.FromSeconds(30), () => GC.GetTotalMemory(false))
    .Build();
await agent.StartAsync();
```

In an application with a host, `builder.Services.AddZabbixPushAgent("web-01")` registers the same class as a hosted service and
returns a builder with the same verbs, and a health check (`.AddHealthCheck()`).

Requires .NET 10 (`net10.0`). Works with Zabbix 7.0, 7.4 and 8.0 ([known limitations](https://github.com/schroeder247/community-zabbix/blob/main/docs/KNOWN-LIMITATIONS.md)).

Documentation: https://github.com/schroeder247/community-zabbix (README, docs/GETTING-STARTED.md).

Provided "as is", without warranty of any kind: use at your own risk.

This project is not affiliated with, endorsed by or sponsored by Zabbix LLC. Zabbix is a registered trademark of Zabbix LLC.
