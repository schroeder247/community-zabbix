# Community.Zabbix.Agent.Storage.EntityFrameworkCore

A durable store for the Community Zabbix SDK on EF Core: one table per store, the record's properties as its columns, and a leased
batch kept until its destination acknowledges it, so a restart loses nothing. It is tested on PostgreSQL and SQLite;
another relational EF Core provider (SQL Server, for example) is expected to work but is not tested. It builds on the store contract in `Community.Zabbix.Agent` and needs nothing of `Community.Zabbix.Proxy`; the
namespace is `Community.Zabbix.Storage.EntityFrameworkCore`.

```shell
dotnet add package Community.Zabbix.Agent.Storage.EntityFrameworkCore
```

```csharp
using Microsoft.EntityFrameworkCore;            // UseNpgsql, from the provider's package (Npgsql.EntityFrameworkCore.PostgreSQL)
using Community.Zabbix;
using Community.Zabbix.Storage.EntityFrameworkCore;

string buffer = Environment.GetEnvironmentVariable("ZABBIX_BUFFER")!;          // the database's connection string
await using ZabbixPushAgent agent = ZabbixPushAgent.CreateBuilder("web-01")
    .AddServer("zbx-server-01")
    .AddPoll(new ZabbixItemKey<long>("app.memory"), TimeSpan.FromSeconds(30), () => GC.GetTotalMemory(false))
    .UseEntityFrameworkStore(db => db.UseNpgsql(buffer))                        // one table per destination
    .Build();
await agent.StartAsync();
```

In an application with a host, `builder.Services.AddZabbixPushAgent("web-01")` registers the same class, and its builder takes
the same `UseEntityFrameworkStore`. A proxy's builder, from `Community.Zabbix.Proxy`, takes it too, for the proxy's history. A
store of your own construction goes to `UseStore((policy, services) => …)`.

Requires .NET 10 (`net10.0`). Works with Zabbix 7.0, 7.4 and 8.0 ([known limitations](https://github.com/schroeder247/community-zabbix/blob/main/docs/KNOWN-LIMITATIONS.md)).

Documentation: https://github.com/schroeder247/community-zabbix (README, docs/GETTING-STARTED.md).

Provided "as is", without warranty of any kind: use at your own risk.

This project is not affiliated with, endorsed by or sponsored by Zabbix LLC. Zabbix is a registered trademark of Zabbix LLC.
