# Community.Zabbix.Proxy

The receiving edges of the Community Zabbix SDK on Kestrel's connection layer: the listener, the trapper (values in), the passive
agent (keys answered), the active and passive proxy (`proxy data`/`proxy config`), and the connector endpoint for a
web application. No web host is started by the listeners.

```shell
dotnet add package Community.Zabbix.Proxy
```

```csharp
using System.Net;
using Community.Zabbix;

// A trapper that relays what it takes to zbx-server-01:
await using ZabbixPushAgent relay = ZabbixPushAgent.CreateBuilder("relay")
    .AddServer("zbx-server-01")
    .AddTrapper(10051, o => o.AllowedPeers.Add("10.0.0.0/8"))
    .Build();
await relay.StartAsync();

// A passive agent that answers the server's polls:
await using ZabbixPassiveAgent agent = ZabbixPassiveAgent.CreateBuilder("web-01")
    .AllowPeer(IPNetwork.Parse("10.0.0.0/24"))
    .AddItem("app.version", _ => "2.4.1")
    .Build();
await agent.StartAsync();

// An active proxy that reports to an HA cluster:
await using ZabbixActiveProxy proxy = ZabbixActiveProxy.CreateBuilder("proxy-01")
    .AddServer("zbx-server-01;zbx-server-02")
    .AllowPeer(IPNetwork.Parse("10.0.0.0/8"))
    .Build();
await proxy.StartAsync();
```

`ZabbixPassiveProxy.CreateBuilder("dmz-proxy").WithVersion("7.4")` builds the passive proxy, and `ZabbixListener.CreateBuilder()` a
listener of your own. In an application with a host, `AddZabbixPushAgent`, `AddZabbixPassiveAgent`, `AddZabbixActiveProxy` and
`AddZabbixPassiveProxy` register the same classes and return a builder with the verbs `CreateBuilder` has.

Requires .NET 10 (`net10.0`). Works with Zabbix 7.0, 7.4 and 8.0 ([known limitations](https://github.com/schroeder247/community-zabbix/blob/main/docs/KNOWN-LIMITATIONS.md)).

Documentation: https://github.com/schroeder247/community-zabbix (README, docs/GETTING-STARTED.md).

Provided "as is", without warranty of any kind: use at your own risk.

This project is not affiliated with, endorsed by or sponsored by Zabbix LLC. Zabbix is a registered trademark of Zabbix LLC.
