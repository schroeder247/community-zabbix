<!--
This Source Code Form is subject to the terms of the Mozilla Public
License, v. 2.0. Copyright (c) 2026 schroeder247. SPDX-License-Identifier: MPL-2.0
-->
# The API client

`ZabbixClient` calls the Zabbix JSON-RPC API on Zabbix 7.0, 7.4, and 8.0. Use it to read and change hosts, items, and every other
API object, and to link to their pages in the web frontend. To send values or read an agent, use
[`ZabbixWireClient`](wire-client.md) instead.

```bash
dotnet add package Community.Zabbix.Sdk
```

```csharp
using Community.Zabbix;

string token = Environment.GetEnvironmentVariable("ZABBIX_API_TOKEN")!;
await using var zabbix = new ZabbixClient("https://zabbix.example/", token);

Console.WriteLine(await zabbix.GetApiVersionAsync());

var hosts = await zabbix.Hosts.GetAsync(q =>
{
    q.Search.VisibleName = "web";
    q.Limit = 10;
});

foreach (ZabbixHost host in hosts)
    Console.WriteLine($"{host.HostId} {host.VisibleName}");
```

Never put a token in your source. Requests go through the platform's HTTP pipeline: `IHttpClientFactory` and the standard resilience
handler.

## Create a client

Pass the frontend's address and an API token. You must dispose a client you construct. It behaves the same as one you
[register in a host](#in-an-application-with-a-host).

- An address whose path names no `.php` script gets the frontend's `api_jsonrpc.php`: `https://zabbix.example/zabbix/` posts to
  `https://zabbix.example/zabbix/api_jsonrpc.php`.
- An address that names a script is used as given. `ZabbixClientOptions.GetApiEndpoint` returns what an address becomes.

To set more than the address and the credential, use the builder:

```csharp
using ILoggerFactory loggers = LoggerFactory.Create(logging => logging.AddConsole().AddZabbixRedaction());

await using ZabbixClient zabbix = ZabbixClient.CreateBuilder()
    .WithEndpoint("https://zabbix.example/")
    .WithToken(token)
    .WithMaxConnectionsPerServer(20)
    .UseLogging(loggers)
    .Build();
```

Each of these gives the same client, with the same HTTP pipeline and resilience:

| To | Use |
| --- | --- |
| Pass the address and a credential | `new ZabbixClient(endpoint, token)`, with a `NetworkCredential` or a `ZabbixCredential` in place of the token if you need one |
| Set more | `ZabbixClient.CreateBuilder()`, its verbs, and `Build()` |
| Share a client in an application with a host | `AddZabbixClient`, with the same verbs. See [In an application with a host](#in-an-application-with-a-host). |

The builder's verbs apply in the order you call them:

| Verb | What it sets |
| --- | --- |
| `WithEndpoint` | The frontend's address or the JSON-RPC endpoint, as a `Uri` or as text. |
| `WithToken`, `WithLogin`, `WithCredential` | The credential: an API token, a user's name and password, or a `ZabbixCredential`. A later one replaces an earlier one. See [Authenticate](#authenticate). |
| `AllowUnencryptedCredentials` | Lets the credential go over plain `http` to another host. |
| `WithFrontend` | Where the web frontend is, when it isn't at the endpoint's folder. |
| `WithMaxConnectionsPerServer`, `WithMaxResponseContentBytes` | The most connections to the server, and the largest reply the client reads. |
| `WithTimeout` | How long a call may take, retries included, or how long one method's calls may take. See [Change retries and timeouts](#change-retries-and-timeouts). |
| `ConfigureResilience`, `ConfigureHttpClientBuilder` | The standard resilience handler's options, and the builder of the client's `HttpClient`. |
| `Configure` | Any option, through a delegate. [Configuration](configuration.md) lists the options. |
| `Bind` | The options in a configuration section, keyed by their names. |
| `UseLogging`, `UseTimeProvider` | Where the client logs, and the clock it reads. Only the builder from `CreateBuilder()` has these: in a host, the client uses the host's. |

- `Build()` checks the options with the rules a host checks at start. If they don't validate, it throws `InvalidOperationException`,
  with each failure in the `OptionsValidationException` inside. A constructor throws the `OptionsValidationException` itself.
- `Build()` runs once. To build another client, create another builder.
- You must dispose the client you build. The logger factory you pass to `UseLogging` stays yours.
- Without `UseLogging`, the client logs nowhere. Its traces and metrics still flow, because activity sources and meters are
  process-wide.
- `AddConsole` comes from the `Microsoft.Extensions.Logging.Console` package.

## Authenticate

| Pass | How it authenticates |
| --- | --- |
| A `string` token, or `WithToken` | Sends an API token (*Users → API tokens*) as `Authorization: Bearer`. `ZabbixClientOptions.ApiToken` does the same. |
| A `NetworkCredential`, or `WithLogin` | Calls `user.login` with its user name and password on the first request that needs it. The session id is the token from then on. |
| A `ZabbixCredential`, or `WithCredential` | Your own, such as one that reads a vault, or `ZabbixCredential.FromApiToken`, `FromLogin`, or `Anonymous`, which sends no token, so only the methods that need no authentication answer. `Anonymous` is the default when you give neither a token nor a credential. |

A credential goes only over `https` or to a loopback host. To send one over plain `http`, call `AllowUnencryptedCredentials`, or
set `ZabbixClientOptions.AllowUnencryptedCredentials`. The client never follows an HTTP redirect, so the token goes only to the configured
endpoint ([SECURITY.md](../../SECURITY.md)).

### Log in with a user name and password

```csharp
await using var client = new ZabbixClient("https://zabbix.example/", new NetworkCredential("api-reader", password));
```

- The client logs in once, however many requests race for it.
- A session the server has ended (`SessionTerminated`) is renewed once per refused request, and the request goes again. That's safe
  for a write, because Zabbix authenticates before it runs the method.
- A refused login is answered from memory for `LoginBackoff` (30 seconds), because Zabbix blocks an account after repeated failures.
- A user whose group requires multi-factor authentication can't log in through the API. The login throws
  `AuthenticationException`. Use an API token.
- To keep the password out of the process between logins, pass a provider: `new ZabbixSessionCredential(user, provider)`, where
  `provider` is a `Func<CancellationToken, ValueTask<string>>`.

### End a session

| What you call | What happens |
| --- | --- |
| `LogOffAsync(cancellationToken)` | Sends `user.logout`, and throws if the server refuses. |
| `await using`, `DisposeAsync`, or a container disposed asynchronously | Sends `user.logout` as a best effort, and logs a failure at Debug (event 4007). A client from a container ends its session when the container is disposed, not when you dispose it. |
| `Dispose` | Sends nothing. The session stays on the server until its idle timeout ends it. |

### Write your own credential

To read a token from a vault, for example, derive from `ZabbixCredential`:

```csharp
internal sealed class VaultCredential(IVault vault) : ZabbixCredential
{
    public override async ValueTask<string?> GetTokenAsync(ZabbixTokenRequestContext context, CancellationToken cancellationToken = default)
        => await vault.ReadTokenAsync(cancellationToken);

    public override bool Invalidate(string token, ZabbixApiException refusal)
        => refusal.Kind is ZabbixApiErrorKind.SessionTerminated or ZabbixApiErrorKind.NotAuthorized;
}
```

- `GetTokenAsync` answers the bearer token for each request. Its `ZabbixTokenRequestContext` carries the transport to log in through,
  the method being called, and the clock.
- `Invalidate` is called when a request that carried a token gets any API error on its first attempt. Answer `true` only when the
  server refused the token before running the method and the next `GetTokenAsync` will give a different token. The request then goes
  once more, so answering `true` for another error could run a write twice.

## Read objects

`ZabbixClient` holds one client per API object: `zabbix.Hosts`, `zabbix.Items`, `zabbix.HostGroups`, and 65 more. Each has the
object's methods, with typed records.

```csharp
var enabled = await zabbix.Hosts.GetAsync(q => q.Filter.Status = [ZabbixHostStatus.Enabled]);
long webHosts = await zabbix.Hosts.CountAsync(q => q.Search.VisibleName = "web");

ZabbixHost host = await zabbix.Hosts["web-01"].GetAsync();         // one host, by name
ZabbixHost same = await zabbix.Hosts[10084].GetAsync();            // the same node, by id
ZabbixItem item = await zabbix.Items[itemId].GetAsync();           // one object of any client, by id
await zabbix.Items[itemId].DeleteAsync();
```

| Method | What it does | What it returns |
| --- | --- | --- |
| `GetAsync` | Reads the objects its parameters select. Pass a lambda that sets them, a `*GetParameters` record, or nothing for every object. | The records |
| `FindAsync` | Reads one object by id | The record, or `null` when there's none you may read |
| `CountAsync` | Counts objects, with the same parameters (`countOutput`) | The count |
| `CreateAsync`, `UpdateAsync` | Changes one object or several | The ids the server answered |
| `DeleteAsync` | Deletes one object or several | For several, the ids the server answered. For one, nothing. |
| `this[id]` | One object: `GetAsync`, `FindAsync`, and `UpdateAsync` and `DeleteAsync` where the API changes and deletes it. It makes no call until you ask. | A node |

- `Hosts["web-01"]` takes a host's name, and `HostGroups["Linux servers"]` a group's. `Hosts[10084]` and `HostGroups[5]` take
  their ids and give the same node. Each node reads, finds, updates, and deletes its object, a host's reaches its items,
  `Hosts["web-01"].Items["system.uptime"]`, and a group's its hosts, `HostGroups["Linux servers"].Hosts["web-01"]`. See
  [The tree and the watcher](tree-and-watcher.md).
- A `Get…Async` throws `ZabbixNotFoundException` when there's nothing to return, and its `Find…Async` returns `null`.
- Every method takes an optional `CancellationToken` last, on every form: `await zabbix.Hosts.GetAsync(cancellationToken)`,
  `await zabbix.Hosts.GetAsync(q => q.Limit = 10, cancellationToken)`.

The parameters of a get:

| Member | What it does |
| --- | --- |
| `Filter` | Matches by equality: `q.Filter.Status = [ZabbixHostStatus.Enabled]` |
| `Search` | Matches a substring: `q.Search.VisibleName = "web"` |
| `Output` | Names the fields that come back: the record's `Zabbix*Fields`, or `ZabbixOutput.Extend` for every field |
| `Select*` | Adds related objects, and names their fields the same way: `q.SelectInterfaces = ZabbixOutput.Extend` |
| `Sort` | Orders by fields of the record, each ascending unless it says `Descending()`: `q.Sort = [ZabbixHostFields.VisibleName]` |
| `Limit` | Caps the objects that come back |

```csharp
var hosts = await zabbix.Hosts.GetAsync(q =>
{
    q.Output = [ZabbixHostFields.HostId, ZabbixHostFields.VisibleName];
    q.SelectInterfaces = [ZabbixHostInterfaceFields.Ip, ZabbixHostInterfaceFields.Port];
    q.Sort = [ZabbixHostFields.VisibleName.Descending(), ZabbixHostFields.HostId];
});
```

- `Output`, each `Select*`, and `Sort` take the fields of the record they return, a `ZabbixField<TRecord>`, so a host's field
  doesn't compile in an item's query. `ZabbixOutput.Extend` fits any output or select.
- A get's reply is always its records. The parameters leave out what would change its shape (`countOutput`, `groupCount`, `groupBy`,
  `preservekeys`, and a select's `count`): `CountAsync` counts.
- `Sort` is one list: the API's `sortfield` and `sortorder`, a direction for each field.
- `Filter` and `Search` are never `null`, and only what you set is sent.
- For a field the SDK doesn't name yet, use the wire name: `q.Filter["a_new_field"] = "x"`, and
  `q.Output = [new ZabbixField<ZabbixHost>("a_new_field")]`. A parameter the SDK doesn't name goes in `AdditionalFields`, which
  refuses the ones that would change the reply's shape.
- A reply is read whole, up to `ZabbixClientOptions.MaxResponseContentBytes` (1 GiB). The API has no paging, so narrow a large read with the
  parameters.
- A field the server sends that a record doesn't model is kept in its `AdditionalFields` (`IZabbixAdditionalFields`).
- For a method the clients don't name, `zabbix.SendAsync(method, parameters, cancellationToken)` sends raw JSON and returns the raw
  `result`.
- `ZabbixJson.Options`, and its `TypeInfoResolver`, are the contract the SDK reads and writes the API with: ids as strings and enums as
  their numbers. `JsonSerializer.Serialize(host, ZabbixJson.Options)` writes a record as the API takes it.

## Create and update objects

A create takes the fields the API requires, and a lambda that sets any of the others. A node changes its object with a lambda, and
fills in the object's id.

```csharp
using System.Net;

ulong linux = await zabbix.HostGroups.CreateAsync("Linux servers");
ulong id = await zabbix.Hosts.CreateAsync("web-02", [linux], h =>
{
    h.Interfaces = [ZabbixInterface.Agent(IPAddress.Parse("10.0.0.2"))];
    h.Tags = [("env", "prod")];
});

await zabbix.Hosts["web-02"].UpdateAsync(h => h.Status = ZabbixHostStatus.Disabled);
await zabbix.Items[itemId].UpdateAsync(i => i.Delay = TimeSpan.FromMinutes(5));
await zabbix.HostGroups["Linux servers"].UpdateAsync(g => g.Name = "Linux");
```

- A request's required fields are `required` members, and its constructor takes them in the order the create does. Its other fields
  have setters, which the lambda sets. What you leave unset isn't sent, so an update changes only what you set.
- A create also takes the request itself, which a target-typed `new` builds:
  `await zabbix.Hosts.CreateAsync(new("web-03", [linux]) { VisibleName = "Web 3" })`. Several requests go in one call as a list.
- A node by name reads its object's id first, and throws `ZabbixNotFoundException` when there's none. A node by id sends the update
  without reading. The lambda can't change the id: a request's required members are set once.
- A value nested in a request needs no `new`:

| Value | How you write it |
| --- | --- |
| A group, a template, or another object named by its id | The id: `Groups = [linux]` |
| A tag | A pair: `Tags = [("env", "prod")]` |
| A user macro | A pair: `Macros = [("{$SNMP_COMMUNITY}", "public")]` |
| A host's interface | `ZabbixInterface.Agent`, `Snmp`, `Ipmi`, or `Jmx`, at an `IPAddress` or a DNS name, on the port Zabbix uses for the kind unless you pass one |
| An item's update interval | A `TimeSpan`: `Delay = TimeSpan.FromMinutes(5)` sends `5m`. For a macro or a flexible interval, `ZabbixInterval.Parse("1m;10s/1-5,09:00-18:00")` |
| How long an item's history and trends are kept, and its timeout | A `TimeSpan`: `History = TimeSpan.FromDays(90)` sends `90d`. For a macro, `ZabbixDuration.Parse("{$HISTORY}")` |
| An interface's port | A number: `Port = 10050`. For a macro, `ZabbixPort.Parse("{$AGENT_PORT}")`, which a factory's interface takes with `with { Port = … }` |
| The hosts that may send to a trapper item | Addresses and blocks: `TrapperHosts = [IPNetwork.Parse("10.0.0.0/8"), IPAddress.IPv6Loopback]`. For a DNS name or a macro, `ZabbixPeer.Parse("monitor.example")`, and for Zabbix's list, `ZabbixPeer.ParseList("10.0.0.0/8, ::1")` |

- The first interface of each type in a host's list is its main one, unless you mark another with
  `with { Main = ZabbixInterfacePriority.Primary }`. Zabbix needs one main interface of each type a host has.
- `ZabbixInterface.Snmp` makes an SNMPv2c interface whose community is the `{$SNMP_COMMUNITY}` macro. Pass a community, or a
  `ZabbixHostInterfaceDetailRequest` for SNMPv1 or SNMPv3.
- To add an interface to a host that exists, pass one with the host's id:
  `await zabbix.HostInterfaces.CreateAsync(hostId, ZabbixInterface.Snmp(IPAddress.Parse("10.0.0.2")))`.
- An interval, a length, a port, and a trapper's hosts read back as the server wrote them, and `ToString()` gives that text.
  `item.Delay?.UpdateInterval` and `item.History?.Length` are `TimeSpan` values, and `null` for a macro. An empty list of trapper
  hosts lets any host send.

## Check what the server's version supports

```csharp
Version version = await zabbix.GetApiVersionAsync();
bool hasRules = ZabbixApiMethods.Supports("ceprule.get", version);
```

`GetApiVersionAsync` reads the server's version once per client. That version gates every method and request field that isn't in all
three versions. A call the version doesn't have throws `ZabbixVersionException`, a `NotSupportedException`, before anything is sent.
Its `Method`, `Field`, `ServerVersion`, `Since`, `RemovedIn`, and `Replacement` say what's refused and what to use instead.

- Versions compare by major and minor.
- To test first, call `ZabbixApiMethods.Supports(method, version)` or `ZabbixApiMethods.SupportsField(method, field, version)`.
- A method the model doesn't know goes to the server, which answers.

Zabbix 8.0 removed `host.massupdate`, `template.massupdate`, `hostgroup.massupdate`, `templategroup.massupdate`, and
`hostinterface.replacehostinterfaces`. The client doesn't refuse them on 8.0. `MassUpdateAsync` and `ReplaceHostInterfacesAsync` use
the methods 8.0 kept, and a call that reads before it writes isn't atomic ([known limitations](../KNOWN-LIMITATIONS.md#behaviour)).

## Handle errors

```csharp
try
{
    await zabbix.HostGroups.CreateAsync("Linux servers");
}
catch (ZabbixApiException e) when (e.Kind == ZabbixApiErrorKind.Validation)
{
    Console.WriteLine(e.Message);   // the method, then what the server said
}
```

| Failure | Exception |
| --- | --- |
| The server answered with an error object | `ZabbixApiException`: `Kind`, `Code`, `ServerMessage`, `Detail`, `Method`, `IsTransient` |
| The server's version lacks the method or field | `ZabbixVersionException`, before the request |
| The request couldn't be sent, the reply is an HTTP error status, the circuit is open, or the concurrency limit is reached | `HttpRequestException` |
| The call passed its deadline | `TimeoutException` |
| The reply isn't JSON-RPC (the frontend's HTML page, when the endpoint lacks `api_jsonrpc.php`), has the wrong shape, or is larger than `MaxResponseContentBytes` | `InvalidDataException`. For a reply that isn't JSON-RPC, the message names the endpoint and the `api_jsonrpc.php` to use. |
| The reply opens as JSON and then isn't, or its `result` doesn't fit the record | `JsonException` |

Filter on `Kind` (`ZabbixApiErrorKind`) instead of parsing the message. It comes from the JSON-RPC code and the start of the server's
`data` text, which the API always writes in English. Its values are `Validation`, `NotAuthorized`, `SessionTerminated`, `TokenExpired`,
`LoginFailed`, `Permission`, `PermissionOrNotFound`, `MethodNotFound`, `AuthorizationHeaderRefused`, `Transient`, `Database`, and
`Unknown`. [Troubleshooting](../TROUBLESHOOTING.md) says what to do about them.

## Change retries and timeouts

Change the pipeline per client, with the verbs of a builder or of a [registered client](#in-an-application-with-a-host):

```csharp
builder.Services.AddZabbixClient("prod")
    .WithEndpoint("https://zabbix.example/")
    .WithToken(token)
    .WithTimeout("configuration.import", TimeSpan.FromMinutes(5))   // this method's attempt and total timeout
    .ConfigureResilience(resilience =>
    {
        resilience.Retry.MaxRetryAttempts = 5;
        resilience.TotalRequestTimeout.Timeout = TimeSpan.FromMinutes(1);
    });
```

`WithTimeout(TimeSpan)` sets how long any call may take, retries included: the attempt and the total timeout together, with the
circuit breaker's sampling duration widened to match. A method `WithTimeout(method, timeout)` names keeps its own.

The pipeline keeps the standard handler's defaults:

| Setting | Default |
| --- | --- |
| `AttemptTimeout` | 10 seconds |
| `TotalRequestTimeout` | 30 seconds |
| `Retry` | Three retries, with exponential backoff and jitter |
| `CircuitBreaker` | On |
| `RateLimiter` | A concurrency limit of 1,000 calls, with no queue |

- The same settings bind from the `Resilience` subsection of a server's configuration, and that subsection reloads while the
  application runs.
- The standard handler validates its own options. The total timeout must exceed the attempt timeout, and the circuit breaker's
  sampling duration must be at least twice the attempt timeout.
- Setting `Retry.ShouldHandle` replaces the read-only gate, so a write could then go twice.
- `ConfigureHttpClientBuilder` adds a delegating handler inside the SDK's pipeline. A primary handler given there replaces the SDK's own.
- Nothing limits the request rate per endpoint ([known limitations](../KNOWN-LIMITATIONS.md#behaviour)).

### What gets retried

Every JSON-RPC call is a POST, so the SDK decides by the method. A read is retried after a transient failure. A write never is, because
a write that failed may still have happened.

- A read is a method the model marks as safe to send twice (`ZabbixHttp.IsIdempotent(method)`). A method the model doesn't know isn't
  retried.
- A transient failure is the standard handler's (a 5xx, 408, or 429 status, a connection failure, or an attempt timeout) or the API's
  `-32603` answer (`ZabbixApiException.IsTransient`).
- An application error (`-32500`, `Database` included) is never retried.
- When the retries run out, you get the last outcome, such as the `ZabbixApiException` itself.

## Link to a page in the web frontend

```csharp
ZabbixFrontend frontend = await zabbix.GetFrontendAsync();

Uri form = frontend.UriFor(host);                                         // any API record with a page converts
Uri latest = frontend.UriFor(host, ZabbixPageView.Latest);
Uri map = frontend.UriFor(new ZabbixPageTarget(ZabbixPageKind.Map, 3UL));   // a bare id
```

`GetFrontendAsync` pairs the frontend's address with the server's version, whose routes the links follow. Zabbix 7.0 opens most forms
from their list, and 7.4 and 8.0 address them directly.

- The frontend's address is the endpoint's folder: `https://zabbix.example/` for `https://zabbix.example/api_jsonrpc.php`. Set
  `ZabbixClientOptions.FrontendUri` when the frontend is elsewhere.
- The record must carry its id, so include the id field in `Output`.
- A view the kind has no page for throws `ArgumentException`. `ZabbixFrontend.ViewsOf(kind)` lists the views it has.
- Without a connected server, `new ZabbixFrontend(baseUri, version)` builds the same links.
- A link needs a signed-in browser.

## In an application with a host

Register the client, and the container shares it, and disposes it:

```csharp
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Community.Zabbix;

HostApplicationBuilder builder = Host.CreateApplicationBuilder(args);
builder.Services.AddZabbixClient("prod")
    .WithEndpoint("https://zabbix.example/")
    .Bind(builder.Configuration.GetSection("Zabbix:Servers:prod"));   // the token, kept out of the source
builder.Logging.AddZabbixRedaction();          // erases [ZabbixSecret] values from logs

using IHost app = builder.Build();
ZabbixClient zabbix = app.Services.GetRequiredService<IZabbixClientFactory>().Create("prod");
```

- `AddZabbixClient("prod")` registers one client under a name and returns an `IZabbixClientBuilder`, which takes the verbs of
  [Create a client](#create-a-client) except `UseLogging` and `UseTimeProvider`.
- `AddZabbixClient()` without a name registers the unnamed client, which is also injected as plain `ZabbixClient`.
- Registering a name again continues setting up the same client. It never adds a second pipeline.
- `Bind` reads the options from a section, keyed by their names. `AddZabbixClients` registers every server a configuration
  declares. See [Configuration](configuration.md).
- Giving both `ZabbixClientOptions.ApiToken` and a credential verb fails validation.
- `IZabbixClientFactory.Create(name)` returns the client registered under the name, built once and shared. It throws
  `KeyNotFoundException` for a name nobody registered, and `TryCreate` asks first.
- A client from the container, injected or returned by the factory, is the container's. Your `Dispose` or `DisposeAsync` leaves
  it running, as disposing an `HttpClient` from `IHttpClientFactory` leaves its handler, and the container's disposal ends it and
  its login session. A client you construct or build is yours to dispose.
- The client is also a keyed singleton: `[FromKeyedServices("prod")] ZabbixClient zabbix`.
- The options are validated when the host starts. A missing `Endpoint`, a token holding whitespace (a token read from a file keeps its
  line end), or a credential bound for plain `http` fails with `OptionsValidationException`.

## Use it from PowerShell

The `Community.Zabbix` module has a command for each API object, built on this client:

```powershell
Connect-ZbxServer -Uri https://zabbix.example/ -ApiToken (Read-Host -AsSecureString 'API token')

Get-ZbxHost -Name 'web-*' | Format-Table TechnicalName, VisibleName, Status
Invoke-ZbxApi -Method host.get -Body @{ output = 'hostid'; limit = 1 }
Get-ZbxHost -Name web-01 | Show-ZbxPage
```

See [PowerShell](powershell.md).

## See also

- [The tree and the watcher](tree-and-watcher.md): read latest values and history by host and item, and watch them.
- [Configuration](configuration.md): bind the same clients from `appsettings.json` and environment variables.
- [Telemetry](telemetry.md): the API's spans, metrics, and log events, and redaction.
- [Get started with the SDK](../GETTING-STARTED.md): a program built step by step.
- [Troubleshooting](../TROUBLESHOOTING.md), [Known limitations](../KNOWN-LIMITATIONS.md), and [SECURITY.md](../../SECURITY.md).
- The [QueryApi and WorkerService samples](../../samples/README.md).
