# Community.Zabbix.Abstractions

The contracts every package of the Community Zabbix SDK shares: the value record `ZabbixValue` and its clock, the item key, the
options, the credential, the API transport (`IZabbixTransport`), the API error kinds, the data classifications, the
telemetry names, and the item contract an agent answers keys through (`ZabbixItemProvider`, `ZabbixAgentAnswer`, the
plugin entry). Reference it to implement a transport, to write item providers or a plugin, or to code against the SDK
without its implementation. The item contract is in the root namespace, `Community.Zabbix`
([the items guide](https://github.com/schroeder247/community-zabbix/blob/main/docs/guides/items.md)); the store contract
is in `Community.Zabbix.Agent`, the wire's endpoints and TLS options in `Community.Zabbix.Wire`.

```shell
dotnet add package Community.Zabbix.Abstractions
```

```csharp
using Community.Zabbix;

var value = new ZabbixValue("web-01", "system.cpu.load[all,avg1]", "0.42", DateTimeOffset.UtcNow);
Console.WriteLine($"{value.Host} {value.Key.Name}({value.Key.Parameters[0]}) = {value.Value} at {value.Time}");
```

Requires .NET 10 (`net10.0`). Works with Zabbix 7.0, 7.4 and 8.0 ([known limitations](https://github.com/schroeder247/community-zabbix/blob/main/docs/KNOWN-LIMITATIONS.md)).

Documentation: https://github.com/schroeder247/community-zabbix (README, docs/GETTING-STARTED.md).

Provided "as is", without warranty of any kind: use at your own risk.

This project is not affiliated with, endorsed by or sponsored by Zabbix LLC. Zabbix is a registered trademark of Zabbix LLC.
