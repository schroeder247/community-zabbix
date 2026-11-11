<!--
This Source Code Form is subject to the terms of the Mozilla Public
License, v. 2.0. Copyright (c) 2026 schroeder247. SPDX-License-Identifier: MPL-2.0
-->
# The tree and the watcher

The tree reads monitored values by name, through three levels: the server, its hosts, and their items. Every node answers the same
reads and the same `Watch`. A read of a typed item returns its type, and a read that mixes items yields `ZabbixValue`. It's part of
`ZabbixClient`, in `Community.Zabbix.Sdk`.

```bash
dotnet add package Community.Zabbix.Sdk
```

`zabbix` is a `ZabbixClient`, built or registered as [The API client](api-client.md) shows.

```csharp
double load = await zabbix.Hosts["web-01"].GetLastAsync(ZabbixItemKeys.Cpu.Load());

await foreach (ZabbixValue value in zabbix.Hosts["web-01"].GetLatestAsync())
    Console.WriteLine($"{value.Key} = {value.Value} at {value.Time:u}");
```

## Choose what to read

| Node | Reached by | A read covers |
| --- | --- | --- |
| `ZabbixClient` | The root | Every item on the server |
| `ZabbixHostScope` | `zabbix.AllHosts`, `zabbix.HostGroups["Linux servers"].Hosts` | Every item of every host in the set |
| `ZabbixHostGroupNode` | `zabbix.HostGroups["Linux servers"]` | Every item of every host in the group |
| `ZabbixHostNode` | `zabbix.Hosts["web-01"]`, `zabbix.HostGroups["Linux servers"].Hosts["web-01"]`, `zabbix.AllHosts["web-01"]` | Every item of one host |
| `ZabbixItemNode` | `host.Item("key")`, `host.Item(ZabbixItemKey)` | One item |

- Name a host by its technical name (the API's `host` field), a group by its name, and an item by its key.
  `Item(string)` throws `FormatException` for text that isn't an item key.
- Building a node makes no call.
- A host reached through its group is read only while it's in the group.
- A host's or a group's node reads its object with `GetAsync`, or `FindAsync` for `null` when there's none, changes it with
  `UpdateAsync(h => h.Status = ZabbixHostStatus.Disabled)`, and deletes it with `DeleteAsync`. An item's node reads and changes
  its item the same way.
- The level you call a read at is both the scope of the request and the size of its reply. `client.GetLatestAsync()` reads every item
  on the server.

## Read the latest values

```csharp
ZabbixValue? uptime = await zabbix.Hosts["web-01"].Item("system.uptime").GetLastValueAsync();
```

- `GetLatestAsync` is one `item.get` with each item's last value, read whole and then yielded. An item that has never collected yields
  nothing.
- `ZabbixItemNode.GetLastValueAsync` returns the item's last value, or `null` when the item exists and has never collected.

## Read history

```csharp
ZabbixItemNode load = zabbix.Hosts["web-01"].Item("system.cpu.load[all,avg1]");

await foreach (ZabbixValue value in load.GetHistoryAsync(DateTimeOffset.UtcNow.AddHours(-1)))
    Console.WriteLine(value);
```

- `GetHistoryAsync(from, till)` reads `history.get` between two instants, both inclusive to the nanosecond. Omit `till` to read up to
  the current time.
- Values come in clock order across all the items and value types beneath the node.
- The read goes page by page, so a long history never sits in memory whole. A second that fills a page is read whole, so nothing is
  truncated.

## Read a result twice

Each enumeration reads the server again: two `await foreach` over one sequence are two full reads. Copy a result into a list to use it
twice.

```csharp
List<ZabbixValue> values = await zabbix.Hosts["web-01"].GetLatestAsync().ToListAsync();
```

## List hosts and items

```csharp
await foreach (ZabbixHostNode host in zabbix.HostGroups["Linux servers"].Hosts)
    await foreach (ZabbixItemNode item in host)
        Console.WriteLine($"{host.Name} {item.Key}");
```

| What you call | On | What you get |
| --- | --- | --- |
| `await foreach` | A scope | Its hosts, from one `host.get` |
| `await foreach` | A host | Its items |
| `GetAsync` | A host or an item | The `ZabbixHost` or `ZabbixItem` as the API returns it at that moment |
| `GetItemsAsync` | A scope, a host, or an item | The item records beneath it. For the whole server, call `client.AllHosts.GetItemsAsync()`. |

## Handle a missing host, group, or item

```csharp
ZabbixHostNode web = zabbix.Hosts["web-01"];
if (!await web.ExistsAsync()) return;
```

- The first read of a node whose host, group, or item doesn't exist throws `ZabbixNotFoundException`, a `KeyNotFoundException`. Its
  `ObjectType` (`host`, `hostgroup`, or `item`) and `Name` say what's missing. `ExistsAsync` asks first.
- A host that exists and has no items isn't missing. It yields nothing.
- An item whose key the SDK can't read is left out and logged once (event 5005). The other items are answered.

## Watch values

```csharp
using var stop = new CancellationTokenSource(TimeSpan.FromMinutes(5));

await using IZabbixWatcher watcher = zabbix.HostGroups["Linux servers"].Watch(new ZabbixWatchPlan(TimeSpan.FromSeconds(30)));
try
{
    await foreach (ZabbixValue changed in watcher.WithCancellation(stop.Token))
        Console.WriteLine(changed);
}
catch (OperationCanceledException) when (stop.IsCancellationRequested)
{
    Console.WriteLine($"{watcher.SucceededPolls} polls, {watcher.FailedPolls} failed, {watcher.DroppedCount} dropped");
}
```

`Watch` turns a node into a running stream. Each tick reads the history written since the newest clock the watcher has seen, for every
item beneath the node, and yields each value once, whatever each item's own `delay`.

`ZabbixWatchPlan` is the policy. Its interval applies to every node it watches.

| Member | Default | Meaning |
| --- | --- | --- |
| `Interval` | Required | How often history is read. |
| `RefreshInterval` | 1 minute | How often the item set is read again (`item.get`), so added items join and deleted ones leave. |
| `AllowedLateness` | One `Interval` | How late a value may reach the server's history and still be yielded. |
| `MaxItems` | 4096 | How many values each reader may fall behind before its buffer sheds. |
| `WhenFull` | `DropOldest` | What a full reader buffer does. `Wait` throws `ArgumentException`, because a reader must never pace the poll. |

What to expect:

- The watcher starts polling as soon as you call `Watch`, on its own timer, whether or not anyone reads.
- The first tick reads the item set and yields each item's latest value. For an item that collected after you called `Watch`, it yields
  every value since then. Later ticks yield what the items collected since.
- Each `await foreach` is its own reader with its own bounded buffer, attached when its enumerator is created. It sees the values that
  arrive from then on.
- A slow reader loses counted values (`DroppedCount`, logged as event 5009). It never delays the poll.
- `Current` holds the latest value of every item seen so far, by host and key.
- A failed tick never ends the watch. It's logged (event 5003), counted in `FailedPolls`, and skipped, and the next tick tries again.
  A group that doesn't exist yet is such a failure.
- A value that reaches the server's history later than `AllowedLateness` after the watcher read a newer value, such as a proxy
  delivering a backlog, isn't seen by the watcher. `GetHistoryAsync` over that window reads it.

## Read a typed item

A `ZabbixItemKey<T>` declares a key once, with its type. `ZabbixItemKeys` has one for each of Zabbix's own keys, and you can declare your own.

```csharp
ZabbixItemKey<long> requests = new("app.requests");

long count = await zabbix.Hosts["web-01"].GetLastAsync(requests);
double free = await zabbix.Hosts["web-01"].GetLastAsync(ZabbixItemKeys.Disk.FreePercent("/"));

if (await zabbix.Hosts["web-01"].FindLastAsync(requests) is { } last)
    Console.WriteLine($"{last.Value} at {last.Time:u}");

await foreach (ZabbixSample<long> sample in zabbix.Hosts["web-01"].GetHistoryAsync(requests, TimeSpan.FromHours(1)))
    Console.WriteLine($"{sample.Time:u} {sample.Value}");
```

- `GetLastAsync` throws `ZabbixNotFoundException` when the item has no value, and `FindLastAsync` returns `null`.
- A value that isn't a `T` throws `FormatException`.

## Use a value, its key, and its time

The reads that mix items yield `ZabbixValue`, and its `As<T>` reads the value as a type:

```csharp
ZabbixItemKey key = ZabbixItemKey.Create("vfs.fs.size", "/", "pfree");   // vfs.fs.size[/,pfree]
ZabbixValue? last = await zabbix.Hosts["web-01"].Item(key).GetLastValueAsync();

if (last is not null)
    Console.WriteLine($"{key.Name}: {last.As<double>():0.0} % free at {last.Time:u}");
```

Every read and watch yields `ZabbixValue(Host, Key, Value, Time)`:

| Member | What it holds |
| --- | --- |
| `Host` | The host's technical name. |
| `Key` | A `ZabbixItemKey`. Its verbatim text is its identity, as the server matches it: `k["a"]` and `k[a]` are two keys. `Name` and `Parameters` are the parsed view. `ZabbixItemKey.Parse`, `TryParse`, and `Create` build one. |
| `Value` | The value as text, the form Zabbix carries it in. `As<T>()` reads it as a type. |
| `Time` | A `DateTimeOffset`, to its 100 ns. Zabbix stores nanoseconds, and the SDK keeps them when it forwards a value. |
| `IsSupported` | `false` when `Value` is the reason an item isn't supported. |

## Cancel a read and stop a watch

Every read takes a `CancellationToken` last, which reaches the socket: `host.GetHistoryAsync(requests, TimeSpan.FromHours(1),
cancellationToken)`. `WithCancellation` on the sequence passes one too.

| What you do | What happens |
| --- | --- |
| Cancel a watcher's enumeration | The `await foreach` throws `OperationCanceledException` and that reader detaches. The watcher keeps running for its other readers. |
| Dispose the watcher (`await using`, `DisposeAsync`) | The poll stops, and every reader's loop ends normally. |
| Dispose the client | All of its watchers stop. After that, `Watch` on any of its nodes, and `Hosts["name"]`, `HostGroups["name"]`, and the reads on the client itself, throw `ObjectDisposedException`. |
| Abandon an enumerator without disposing it | It keeps its buffer until the watcher is disposed. `await foreach` always disposes it. |

## Read what the tree doesn't cover

The tree has no trends. Read them through the API client:

```csharp
ZabbixItem cpu = await zabbix.Hosts["web-01"].Item("system.cpu.load[all,avg1]").GetAsync();

var hours = await zabbix.Trends.GetAsync(q =>
{
    q.ItemIds = [cpu.ItemId!.Value];
    q.TimeFrom = DateTimeOffset.UtcNow.AddDays(-7);
});
foreach (ZabbixTrendRecord hour in hours)
    Console.WriteLine($"{hour.Clock:u}: avg {hour.ValueAverage}, max {hour.ValueMax}");
```

- The tree has no template or proxy nodes and no query language. A node selects by group, host, and key only. For anything else, use
  the API's parameters.
- A watch has one interval. A node can't poll at its own.

[Known limitations](../KNOWN-LIMITATIONS.md#behaviour) has the full list.

## In an application with a host

A registered client has the same tree. Run a watch in a hosted service, and pass it the stopping token. The container disposes the
client, which stops all of its watchers.

```csharp
internal sealed class GroupWatch(IZabbixClientFactory clients) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        ZabbixHostGroupNode group = clients.Create("prod").HostGroups["Linux servers"];
        await using IZabbixWatcher watcher = group.Watch(new ZabbixWatchPlan(TimeSpan.FromSeconds(30)));

        await foreach (ZabbixValue value in watcher.WithCancellation(stoppingToken))
            Console.WriteLine(value);
    }
}
```

## Use it from PowerShell

`Watch-ZbxItem` in the `Community.Zabbix` module is built on this watcher, and keeps the watch running as a session under `-Name`:

```powershell
Watch-ZbxItem -Name cpu -HostName 'web-01' -Key 'system.cpu.load[all,avg1]' -Interval '30s'
Receive-ZbxWatch -Name cpu | Select-Object -First 5      # waits for each value
Stop-ZbxWatch -Name cpu
```

See [PowerShell](powershell.md).

## See also

- [The API client](api-client.md): create a client, authenticate, handle errors, and change retries.
- [Get started with the SDK](../GETTING-STARTED.md): a watch in a whole program.
- [Known limitations](../KNOWN-LIMITATIONS.md).
- The [QueryApi and WatchValues samples](../../samples/README.md).
