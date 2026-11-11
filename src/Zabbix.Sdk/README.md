# Community.Zabbix.Sdk

The Community Zabbix SDK: `ZabbixClient` and `AddZabbixClient`, the JSON-RPC API over the platform's HTTP pipeline with generated
clients for Zabbix 7.0, 7.4 and 8.0, and the host/item tree and its watcher. The framed-TCP wire (`ZabbixWireClient`, TLS and PSK) is the
`Community.Zabbix.Wire` package.

```shell
dotnet add package Community.Zabbix.Sdk
```

```csharp
using Community.Zabbix;

string token = Environment.GetEnvironmentVariable("ZABBIX_API_TOKEN")!;          // never a token in source
await using var zabbix = new ZabbixClient("https://zabbix.example/", token);
await foreach (ZabbixValue value in zabbix.Hosts["web-01"].GetLatestAsync())
    Console.WriteLine($"{value.Key} = {value.Value}");
```

The address is the frontend's; the client calls its `api_jsonrpc.php`. `ZabbixClient.CreateBuilder()` builds a client with
more to set (`WithTimeout`, `ConfigureResilience`, `UseLogging`), and in an application with a host,
`AddZabbixClient("prod")` registers the same client with the same verbs, and `IZabbixClientFactory.Create("prod")` returns it.
A client from the container is shared: disposing it leaves it running, and the container disposes it.

A client that logs in with a user name and password (a `NetworkCredential`) ends its session with `user.logout`
when it is disposed with `await using` or `DisposeAsync` (a container disposed asynchronously does this for the
clients it holds); the synchronous `Dispose` sends nothing and leaves the session to expire on the server.

Requires .NET 10 (`net10.0`). Works with Zabbix 7.0, 7.4 and 8.0 ([known limitations](https://github.com/schroeder247/community-zabbix/blob/main/docs/KNOWN-LIMITATIONS.md)).

Documentation: https://github.com/schroeder247/community-zabbix (README, docs/GETTING-STARTED.md).

Provided "as is", without warranty of any kind: use at your own risk.

This project is not affiliated with, endorsed by or sponsored by Zabbix LLC. Zabbix is a registered trademark of Zabbix LLC.
