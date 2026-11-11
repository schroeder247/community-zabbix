# Community.Zabbix.Wire

The Zabbix framed-TCP wire: the frame and the exchange (with the HA node walk and proxy-group redirects), TLS with
certificates and PSK, and `ZabbixWireClient` over it, which sends values (what `zabbix_sender` does), reads an item from an
agent (what `zabbix_get` does) and asks a server (its trapper requests: queue, status, item test, alert test, expression
evaluation, statistics), reading each reply into a record such as `ZabbixServerStatus`. It does not need the JSON-RPC
API package.

```shell
dotnet add package Community.Zabbix.Wire
```

```csharp
using Community.Zabbix;

await using var wire = new ZabbixWireClient("zabbix.example:10051");           // or "zabbix.example", on 10051
ZabbixSenderResponse response = await wire.SendAsync("web-01", new ZabbixItemKey<long>("app.queue.depth"), 17);
Console.WriteLine($"processed {response.Processed}, failed {response.Failed}");
```

`ZabbixWireClient.CreateBuilder().WithServer("zbx-1;zbx-2")` sets more than the server, such as an HA cluster's nodes, a token and
TLS, and `AddZabbixWireClient("prod")` registers a named client in a host with the same verbs. The container owns the client it
hands out, so disposing it leaves it running for the rest of the application.

Requires .NET 10 (`net10.0`). Works with Zabbix 7.0, 7.4 and 8.0 ([known limitations](https://github.com/schroeder247/community-zabbix/blob/main/docs/KNOWN-LIMITATIONS.md)).

Documentation: https://github.com/schroeder247/community-zabbix (README, docs/GETTING-STARTED.md).

Provided "as is", without warranty of any kind: use at your own risk.

This project is not affiliated with, endorsed by or sponsored by Zabbix LLC. Zabbix is a registered trademark of Zabbix LLC.
