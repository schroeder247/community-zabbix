<!--
This Source Code Form is subject to the terms of the Mozilla Public
License, v. 2.0. Copyright (c) 2026 schroeder247. SPDX-License-Identifier: MPL-2.0
-->
# Get started with the SDK

In this walk-through, you build a program that connects to a Zabbix server, creates a host with a trapper item, sends it a value,
reads the value back, handles an error, and cleans up. Then you watch values, run an agent, and answer item keys. Last, you
register the same pieces in an application with a host.

The whole program is the [GettingStarted sample](https://github.com/schroeder247/community-zabbix/blob/main/samples/GettingStarted/Program.cs).

> [!CAUTION]
> Provided "as is", without warranty of any kind: use at your own risk. The programs create and delete objects on the server
> you give them. Run them against a lab server, not production.

## Prerequisites

- The .NET 10 SDK.
- A Zabbix 7.0, 7.4, or 8.0 server you may create objects on. To run one, see [Run a lab server](#run-a-lab-server).
- An API token with write access to hosts. In the frontend, go to *Users > API tokens > Create API token*. Save it to a file named
  `zabbix.token`, and keep it out of your source.

## Create the project

```bash
dotnet new console -o Hello -f net10.0 && cd Hello
dotnet add package Community.Zabbix.Sdk
dotnet add package Community.Zabbix.Wire
```

`Community.Zabbix.Sdk` is the API client. `Community.Zabbix.Wire` is the client that sends values. To use a build of your own,
see [Use your own build](INSTALL.md#use-your-own-build).

## Connect

Replace `Program.cs` with this program. It takes the frontend's address, the token file, and the server's trapper address.

```csharp
using Community.Zabbix;

var endpoint = new Uri(args[0]);                               // https://zabbix.example/
string token = (await File.ReadAllTextAsync(args[1])).Trim();  // zabbix.token
string trapper = args[2];                                      // zabbix.example:10051

await using var zabbix = new ZabbixClient(endpoint, token);
Console.WriteLine($"Zabbix API {await zabbix.GetApiVersionAsync()}");
```

The address is the frontend's, and the client calls its `api_jsonrpc.php`. To log in with a user name and password instead of a
token, pass a `NetworkCredential`. The SDK reads the server's version once, and uses it to refuse a field or method that
version doesn't have, with `ZabbixVersionException`, before anything is sent.

## Create a host and an item

Add this to the program. It creates a host group, a host in it, and a trapper item on the host.

```csharp
string hostName = "gs-web-01";
ulong groupId = await zabbix.HostGroups.CreateAsync("Getting started");
ulong hostId = await zabbix.Hosts.CreateAsync(hostName, [groupId]);
await zabbix.Items.CreateAsync(new ZabbixItemCreateRequest
{
    HostId = hostId,
    Name = "Requests",
    Key = "app.requests",
    Type = ZabbixItemType.Trapper,
    ValueType = ZabbixItemValueType.Unsigned,
    TrapperHosts = "0.0.0.0/0,::/0",                  // who may send to it
});
Console.WriteLine($"created {hostName} ({hostId})");
```

- `ZabbixClient` has one client per API object, 68 in all: `zabbix.Hosts`, `zabbix.Items`, and so on.
- `CreateAsync` takes the fields the API requires, or a request with any of the others, and returns the new id. An item needs more
  than its required fields here, so this one takes a request. The request's `required` members are the fields the API requires.
- `TrapperHosts` lists who may send to the item. `0.0.0.0/0,::/0` accepts everyone, so use it only on a lab server. On 8.0, an item
  created through the API accepts only localhost unless you set this.

## Read it back

`GetAsync` takes a lambda that sets the API's parameters. `Filter` matches exactly, `Search` matches a substring, and `Output` picks
the fields that come back.

```csharp
var hosts = await zabbix.Hosts.GetAsync(q =>
{
    q.Filter.TechnicalName = [hostName];
    q.Output = [ZabbixHostFields.HostId, ZabbixHostFields.TechnicalName];
});
foreach (ZabbixHost host in hosts)
    Console.WriteLine($"host:   {host.HostId} {host.TechnicalName}");

var items = await zabbix.Items.GetAsync(q => q.HostIds = [hostId]);
Console.WriteLine($"items:  {string.Join(", ", items.Select(i => i.Key))}");
```

## Send a value

Send the item a value over the Zabbix protocol, as `zabbix_sender` does. The server learns about a new item at its next
configuration refresh (`CacheUpdateFrequency`), and counts a value sent before then as failed. So the program repeats the send until
the server processes it.

A `ZabbixItemKey<T>` declares an item's key once, with its type. Passing anything but a `long` here doesn't compile.

```csharp
ZabbixItemKey<long> requests = new("app.requests");

using var wire = new ZabbixWireClient(trapper);
ZabbixSenderResponse sent;
do
{
    sent = await wire.SendAsync(hostName, requests, 42);
    if (sent.Processed == 0) await Task.Delay(TimeSpan.FromSeconds(5));
}
while (sent.Processed == 0);
Console.WriteLine($"sent:   processed={sent.Processed} failed={sent.Failed}");
```

## Read the value

Read it back with the same item. `FindLastAsync` returns the value as a `long` with its time, or `null` when the item has no value yet.
The server writes history asynchronously, so the program tries for a while.

```csharp
for (int attempt = 0; attempt < 30; attempt++)
{
    if (await zabbix.Hosts[hostName].FindLastAsync(requests) is { } last)
    {
        Console.WriteLine($"read:   {last.Value} at {last.Time:u}");
        break;
    }
    await Task.Delay(TimeSpan.FromSeconds(1));
}
```

## Handle an error

When the server answers with an error, the call throws `ZabbixApiException`. Use `Kind` to tell errors apart, rather than reading the
message.

```csharp
try
{
    await zabbix.HostGroups.CreateAsync("Getting started");
}
catch (ZabbixApiException duplicate)
{
    Console.WriteLine($"error:  {duplicate.Kind}: {duplicate.Message}");
}
```

The SDK retries reads on a transient failure, and never retries a write. See [Troubleshooting](TROUBLESHOOTING.md) for the other
failures.

## Clean up

Delete what the program created. In the sample, this runs in a `finally` block.

```csharp
await zabbix.Hosts.DeleteAsync(hostId);
await zabbix.HostGroups.DeleteAsync(groupId);
Console.WriteLine("deleted");
```

## Run it

```bash
dotnet run -- https://zabbix.example/ zabbix.token zabbix.example:10051
```

On Zabbix 7.0.31, it prints this. Only the version, the ids, and the time differ on 7.4 and 8.0.

```text
Zabbix API 7.0.31
created gs-web-01 (11961)
host:   11961 gs-web-01
items:  app.requests
sent:   processed=1 failed=0
read:   42 at 2026-09-28 04:40:29Z
error:  Validation: hostgroup.create: Invalid params. Host group "Getting started" already exists.
deleted
```

## Open an object's page

Every object has a page in the web frontend. Add this after the program reads the host:

```csharp
ZabbixFrontend frontend = await zabbix.GetFrontendAsync();
Console.WriteLine(frontend.UriFor(hosts[0]));                         // the host's form
Console.WriteLine(frontend.UriFor(hosts[0], ZabbixPageView.Latest));  // its latest data
```

If the frontend isn't at the API's address, set `ZabbixOptions.FrontendUri`. In PowerShell, `Get-ZbxHost -Name gs-web-01 | Show-ZbxPage`
opens the same page.

## Watch values

`Watch` turns the tree into a stream. Each tick reads the history written since the last one, for a host, a group, or the whole
server, and yields each new value once.

```csharp
using var stop = new CancellationTokenSource(TimeSpan.FromMinutes(5));
await using IZabbixWatcher watcher = zabbix.HostGroups["Linux servers"].Watch(new ZabbixWatchPlan(TimeSpan.FromSeconds(30)));
try
{
    await foreach (ZabbixValue changed in watcher.WithCancellation(stop.Token))
        Console.WriteLine(changed);
}
catch (OperationCanceledException)
{
    Console.WriteLine($"stopped: {watcher.SucceededPolls} polls, {watcher.FailedPolls} failed, {watcher.DroppedCount} dropped");
}
```

A failed tick doesn't end the watcher. A group that doesn't exist yet counts in `FailedPolls`, and the next tick tries again. A one-time
read, such as `GetLatestAsync`, of a name that matches nothing throws `ZabbixNotFoundException`. To check first, call `ExistsAsync`.

## Run an agent

A push agent samples on a schedule and sends what it collects, keeping each value in a store until the server has it. Add the package:

```bash
dotnet add package Community.Zabbix.Agent
```

Replace `Program.cs` with this program. It runs until you press Enter, and disposing the agent sends what it still holds.

```csharp
using Community.Zabbix;

ZabbixItemKey<long> requests = new("app.requests");

await using ZabbixPushAgent agent = ZabbixPushAgent.CreateBuilder("web-01")       // the host, as Zabbix knows it
    .AddServer("zbx-server-01")
    .AddPoll(requests, TimeSpan.FromSeconds(30), () => Environment.ProcessorCount)
    .Build();
await agent.StartAsync();

Console.ReadLine();
```

If your application already counts things with `System.Diagnostics.Metrics`, the agent can send those instead:

```csharp
using System.Diagnostics.Metrics;
using Community.Zabbix;

Meter meter = new("MyApp");
Counter<long> requests = meter.CreateCounter<long>("app.requests");

await using ZabbixPushAgent agent = ZabbixPushAgent.CreateBuilder("web-01")
    .AddServer("zbx-server-01")
    .AddMeter("MyApp")
    .Build();
await agent.StartAsync();

requests.Add(1);
Console.ReadLine();
```

See [Agents](guides/zabbix-agents.md) for several servers, the send loop, and the stores.

## Answer item keys

The server can also name the keys it wants. An active agent asks which keys its host has, and answers them. Replace `Program.cs`:

```csharp
using Community.Zabbix;

ZabbixItemKey<string> version = new("app.version");
ZabbixItemKey<long> diskFree = new("app.disk.free");

await using ZabbixActiveAgent agent = ZabbixActiveAgent.CreateBuilder("web-01")
    .AddServer("zbx-server-01")
    .AddItem(version, () => "2.4.1")
    .AddItem(diskFree, key => new DriveInfo(key[0]).AvailableFreeSpace)          // key[0]: the / in app.disk.free[/]
    .Build();
await agent.StartAsync();

Console.ReadLine();
```

On the server, `web-01` must have items of type "Zabbix agent (active)" with these keys. See [Items](guides/items.md) for keys added
while the agent runs, a library's keys, and PowerShell scriptblocks.

## Use a host

In an ASP.NET Core application or a worker service, the container builds these pieces, hands them to the classes that ask, and disposes
them. Add the host's package:

```bash
dotnet add package Microsoft.Extensions.Hosting
```

Replace `Program.cs` with this program, which registers a server by name:

```csharp
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Community.Zabbix;

HostApplicationBuilder builder = Host.CreateApplicationBuilder(args);
builder.Services.AddZabbix("prod", options =>
{
    options.Endpoint = new Uri("https://zabbix.example/");
    options.ApiToken = File.ReadAllText("zabbix.token").Trim();
});
builder.Logging.AddZabbixRedaction();                 // erases the token from logs

using IHost app = builder.Build();
ZabbixClient zabbix = app.Services.GetRequiredService<IZabbixClientFactory>().Create("prod");
Console.WriteLine($"API version: {await zabbix.GetApiVersionAsync()}");
```

To read the same options from `appsettings.json`, replace the `AddZabbix` call with `AddZabbixClients`:

```csharp
builder.Services.AddZabbixClients(builder.Configuration);
```

```json
{
  "Zabbix": {
    "Servers": {
      "prod": { "Endpoint": "https://zabbix.example/" }
    }
  }
}
```

Keep the token out of the file: set `Zabbix__Servers__prod__ApiToken` in the environment or in user secrets.

The wire client registers by name too, with the verbs of its builder. Add this before `builder.Build()`:

```csharp
builder.Services.AddZabbixWireClient("prod").AddServer("zabbix.example:10051");
```

Then `app.Services.GetRequiredService<IZabbixWireClientFactory>().Create("prod")` returns it.

An agent is a hosted service in the same application. Add this before `builder.Build()`, and end the program with `await app.RunAsync()`:

```csharp
builder.Services.AddZabbixPushAgent("web-01")
    .AddServer("zbx-server-01")
    .AddPoll(new ZabbixItemKey<long>("app.requests"), TimeSpan.FromSeconds(30), () => Environment.ProcessorCount)
    .AddHealthCheck();
```

The API's standard resilience handler logs one `Polly` line per call at `Information`. To quiet it, raise the `Polly` category to
`Warning`.

## Next steps

- [The guides](INDEX.md#guides): each piece in depth.
- [The samples](../samples/README.md): small programs, one task each.
- [Troubleshooting](TROUBLESHOOTING.md) and [Known limitations](KNOWN-LIMITATIONS.md).
- [Get started with PowerShell](../src/Zabbix.PowerShell/GETTING-STARTED.md): the same server from PowerShell.

## Run a lab server

Zabbix's container images run a lab server in three containers. [Zabbix's container guide](https://www.zabbix.com/documentation/current/en/manual/installation/containers)
has the other versions and settings.

```bash
docker network create zabbix
docker run -d --name zabbix-db --network zabbix -e POSTGRES_USER=zabbix -e POSTGRES_PASSWORD=zabbix postgres:16
docker run -d --name zabbix-server --network zabbix -p 10051:10051 \
  -e DB_SERVER_HOST=zabbix-db -e POSTGRES_USER=zabbix -e POSTGRES_PASSWORD=zabbix zabbix/zabbix-server-pgsql:alpine-7.0-latest
docker run -d --name zabbix-web --network zabbix -p 8080:8080 -e ZBX_SERVER_HOST=zabbix-server \
  -e DB_SERVER_HOST=zabbix-db -e POSTGRES_USER=zabbix -e POSTGRES_PASSWORD=zabbix zabbix/zabbix-web-nginx-pgsql:alpine-7.0-latest
```

The frontend is `http://localhost:8080/`, with the user `Admin` and the password `zabbix`. The trapper is `localhost:10051`. Over plain
`http`, the SDK sends a credential only to a loopback host, unless you set `ZabbixOptions.AllowUnencryptedCredentials`.
