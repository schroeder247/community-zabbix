<!--
This Source Code Form is subject to the terms of the Mozilla Public
License, v. 2.0. Copyright (c) 2026 schroeder247. SPDX-License-Identifier: MPL-2.0
-->
# Stores

A store holds values between collection and delivery: sources write into it, and the send loop takes each value out and keeps it
until the destination has it. The memory store and the base class for durable stores are in `Community.Zabbix.Agent` (namespace
`Community.Zabbix.Storage`). The EF Core store, tested on PostgreSQL and SQLite, is in
`Community.Zabbix.Agent.Storage.EntityFrameworkCore`.

```bash
dotnet add package Community.Zabbix.Agent.Storage.EntityFrameworkCore
dotnet add package Npgsql.EntityFrameworkCore.PostgreSQL       # or Microsoft.EntityFrameworkCore.Sqlite
```

## Keep an agent's values across restarts

A push agent keeps its values in memory unless its builder chooses a durable store. `UseEntityFrameworkStore` builds one per
destination, at start:

```csharp
using Microsoft.EntityFrameworkCore;
using Community.Zabbix.Storage.EntityFrameworkCore;

string buffer = Environment.GetEnvironmentVariable("ZABBIX_BUFFER")!;          // the database's connection string
ZabbixItemKey<long> memory = new("app.memory");

await using ZabbixPushAgent agent = ZabbixPushAgent.CreateBuilder("web-01")    // the store's name comes from the agent's
    .AddServer("zabbix.example")
    .AddPoll(memory, TimeSpan.FromSeconds(30), () => GC.GetTotalMemory(false))
    .UseEntityFrameworkStore(db => db.UseNpgsql(buffer))
    .Build();
await agent.StartAsync();
```

Values the server hasn't taken when the process stops stay in the table, and the next process sends them.

- The store's policy carries the agent's `MaxItems` and `Overflow`.
- The store's name is the agent's name. With several destinations, it's the agent's name and the destination's nodes, so each
  destination gets its own table. Give two agents that share a database names of their own.
- `UseStore` chooses any other store, built from the agent's policy and the destination's services:
  `UseStore((policy, services) => new MyStore(policy, services))`. A store that needs the agent's container takes it first:
  `UseStore((provider, policy, services) => …)`. Each destination must get a store of its own: a factory that returns one store
  for two destinations is refused at start.
- `UseMemoryStore()` chooses the in-memory store, the default. A later `Use…Store` replaces an earlier one.
- A [proxy's](proxies.md#keep-values-through-an-outage) builder has the same verbs, and builds one store for its history, named
  after the proxy. Its policy also carries the proxy's offline buffer and admission rule.

## Read from a store

The send loop (`ZabbixSendLoop.RunAsync`, `DrainAsync`) reads a store for you, with batching and backoff. See
[Agents](zabbix-agents.md#send-through-the-send-loop). A consumer of your own does the same. Here `store` is a `ZabbixStore<ZabbixValue>`
and `wire` is a `ZabbixWireClient`:

```csharp
using Community.Zabbix.Hosting;
using Community.Zabbix.Protocol;
using Community.Zabbix.Storage;

while (await store.WaitToLeaseAsync(cancellationToken))
{
    List<ZabbixStoreEntry<ZabbixValue>> batch = [];
    while (batch.Count < 250 && store.TryLease(out ZabbixStoreEntry<ZabbixValue> entry)) batch.Add(entry);
    if (batch.Count == 0) continue;
    ZabbixSendOutcome answer = await wire.DeliverAsync([.. batch.Select(leased => leased.Record)], cancellationToken);
    if (answer is ZabbixSendOutcome.Delivered or ZabbixSendOutcome.Rejected)
        store.Acknowledge(batch[^1].Sequence);                            // the batch has its final answer
    else
    {
        store.Release(batch[0].Sequence - 1);                             // lease it again, with the same sequences
        await Task.Delay(TimeSpan.FromSeconds(5), cancellationToken);
    }
}
```

A store has one consumer. That consumer leases records as above, or reads the store as an `IAsyncEnumerable<T>`, where a record leaves
as it's read.

| Member | What it does |
| --- | --- |
| `WaitToLeaseAsync` | Waits for a record. |
| `TryLease` | Hands out the oldest record not yet leased, as a `ZabbixStoreEntry<T>`: its `Sequence`, its `Stored` time, and the `Record`. A leased record stays in the store until `Acknowledge` covers it. |
| `TryPeek` | Shows the next record without leasing it. |
| `Acknowledge(sequence)` | Removes every record leased at or below the sequence. It's cumulative. |
| `Release(sequence)` | Rewinds: every record leased above the sequence and not acknowledged is handed out again, oldest first, with the same sequence. |
| `TryComplete` | Completes the writer. The stream and the lease wait end once every record has been handed out, and throw the completion error if there is one. |
| `Count`, `DroppedCount` | What the store holds, and what it didn't keep. |

## Write to a store

`ZabbixStore<T>` is a `ChannelWriter<T>` for its producers. Use `WriteAsync` when you must know the record is safe.

| | `TryWrite(item)` | `WriteAsync(item)` |
| --- | --- | --- |
| Memory store | `true` once the record is taken: kept, or shed by a drop mode, as a bounded channel's drop modes are | Returns when the record is kept or dropped by the policy |
| Durable store | `true` once the policy accepted the record, or shed it as above: **accepted, not committed** | Returns when the record is **committed** or dropped by the policy, and throws the backend's fault |
| Full under `Wait` | `false` | Waits for room |
| Refused by `Admit` or `Discard` | `false`, counted | Returns, counted |
| After `TryComplete` | `false` | Throws `ChannelClosedException` |

- A durable store commits on a loop of its own, never on the caller's thread. What's written while a commit is under way goes in the
  next one, as one transaction.
- A record `TryWrite` accepted whose commit then fails is dropped, logged (event 2000), and counted `faulted`.
- A proxy's trapper waits for the commit, so it answers `processed` only for committed values. A relay's trapper answers once a value
  is in its intake buffer. See [Read what a trapper answers](listeners.md#read-what-a-trapper-answers).

## Set a policy

```csharp
using Community.Zabbix.Storage;

var policy = new ZabbixStorePolicy<ZabbixValue> { MaxItems = 1_000_000, MaxAge = TimeSpan.FromHours(24) };
var store = new ZabbixMemoryStore<ZabbixValue>(policy);
```

You state the policy once, in the store's constructor. It's an immutable record, so write a variant with `with`, for instance in a
`UseStore` factory: `policy with { MaxAge = TimeSpan.FromHours(24) }`.

| Member | Default | Meaning |
| --- | --- | --- |
| `MaxItems` | 100,000 (`DefaultMaxItems`) | The cap, counted in records, or `null` for none. It's about 28 hours of one value a second. |
| `Overflow` | `DropOldest` | What a full store does. |
| `Admit` | None | A record it answers `false` for is refused at the door. |
| `Discard` | None | A record it answers `true` for is refused at the door and dropped on the way out, to shed stale records. |
| `MaxAge` | None | A record held longer is dropped on the way out, as Zabbix's `ProxyOfflineBuffer` does. |

`Overflow` takes the channel's own `BoundedChannelFullMode`:

| Mode | What a full store does |
| --- | --- |
| `DropOldest` | Sheds the oldest held record to admit the incoming one. A durable store sheds the oldest record not yet leased, or the incoming one when every held record is leased. |
| `DropWrite` | Refuses the incoming record. |
| `DropNewest` | Evicts the newest held record. A durable store refuses this mode at construction. |
| `Wait` | Drops nothing, and makes `WriteAsync` wait for room. The agents refuse it, because their sources run on a schedule and a slow server must not delay a check. |

Every record a store doesn't keep is counted on the `zabbix.store.dropped` metric (meter `Community.Zabbix.Store`), tagged with
the store's name and the reason: `refused`, `discarded`, `expired`, `drop_oldest`, `drop_newest`, `drop_write`, or `faulted` (the
`ZabbixStoreDropReason` values, in snake case). It's logged as event 2100 at the 1st, 10th, 100th, and later powers of ten of each reason.

## Use the memory store

```csharp
using Community.Zabbix.Storage;

var store = new ZabbixMemoryStore<ZabbixValue>(services: new ZabbixStoreServices { Name = "edge-01" });
```

`new ZabbixMemoryStore<T>(policy, services)` is a channel with the policy as its options. Nothing survives the process: `Acknowledge`
only counts, and what the store holds goes with its disposal. Its cap counts records not yet leased, so a leased batch that isn't yet
acknowledged is held on top of `MaxItems`. A durable store counts it.

A store built outside an agent takes its services as a `ZabbixStoreServices`: its `Name` (the tag on its telemetry, and a durable
store's table or file), a `LoggerFactory`, the host's `Meters`, and the clock (`Time`), each with a default.
`ZabbixStoreServices.Default` is named `default`, logs nothing, and runs on the system clock.

## Rely on a durable store

`ZabbixPersistentStore<T>` is the base of every durable store. The lease cursor lives in memory, and a record stays in the backend
until it's acknowledged, so:

- **A batch in flight when the process stops is sent again.** The next process leases every record still in the backend, oldest
  first, with the sequence it was stored with. Sequences only grow and are never reused.
- **Delivery is at least once.** An acknowledgement leaves `Count` at once and the backend on the loop's next turn, so a record
  acknowledged just before a crash is leased again by the next process. A proxy's upload carries each record's sequence as its id, and
  the server drops the repeat. A sender's values carry no id.
- **The first lease waits for the backend.** After a restart, nothing can be leased until the store has opened its backend and loaded
  ahead of the cursor (`ReadAhead`, 256 records at a time). `WaitToLeaseAsync` waits for it. Until then, `Count` counts only this
  process's writes.
- **Disposal keeps what's leased.** `DisposeAsync` commits what was written, removes what was acknowledged, and releases the backend. A
  leased record that wasn't acknowledged stays for the next process.

For a backend of your own, subclass `ZabbixPersistentStore<T>` and supply `OpenAsync`, `AppendAsync`, `LoadAfterAsync`, and
`RemoveAsync`, and `DisposeStoreAsync` if it holds a connection or a file. `AppendAsync` must commit all or nothing, and every
sequence it returns must exceed every sequence the backend ever gave.

## Use the EF Core store

```csharp
using Microsoft.EntityFrameworkCore;
using Community.Zabbix.Storage;
using Community.Zabbix.Storage.EntityFrameworkCore;

await using var store = new ZabbixEntityFrameworkStore<ZabbixValue>(
    o => o.UseSqlite("Data Source=buffer.db"),
    services: new ZabbixStoreServices { Name = "edge-01" });
Console.WriteLine(store.Table);
```

The constructor takes EF Core's own options builder, so the provider is your choice.

- The columns are generated from the record, `T`.
- The table, `Table`, is `zbx_store_` followed by the store's name folded to lower-case letters, digits, and `_`, and a short hash of
  the name. It's created at first use, not in the constructor.
- Its key, `__seq`, is the database's identity column, so a sequence is never reused, even after the table empties.
- Every database call is asynchronous and made by the store's loop: one `SaveChangesAsync` per commit, and one `DELETE` per
  acknowledgement.
- **A table belongs to one store.** Two stores or two processes on one table lease the same rows, and one's acknowledgement removes
  rows the other still holds. Give each store its own name.
- **An existing table must match the record.** A table left by another record type, or by an earlier shape of the same one, fails the
  first use with `InvalidOperationException`. So does a table that can't be created, such as for a user without the right to create it.
  A database that can't be reached fails with the provider's own exception.

## In an application with a host

```csharp
using Microsoft.EntityFrameworkCore;
using Community.Zabbix.Storage.EntityFrameworkCore;

builder.Services.AddZabbixPushAgent("edge-01")
    .AddServer("zabbix.example")
    .UseEntityFrameworkStore(db => db.UseNpgsql(builder.Configuration.GetConnectionString("buffer")));
```

The builder `AddZabbixPushAgent` returns has the verbs `CreateBuilder` has. See
[Agents](zabbix-agents.md#in-an-application-with-a-host).

## See also

- [Agents](zabbix-agents.md): the agents and the send loop that drain a store.
- [The wire client](wire-client.md): the client a store is drained into.
- [Proxies](proxies.md): a proxy's history store.
- [Known limitations](../KNOWN-LIMITATIONS.md): accepted versus committed, one table per store, and the memory store's cap.
- The [PushAgent sample](../../samples/README.md).
