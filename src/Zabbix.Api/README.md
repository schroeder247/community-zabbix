# Community.Zabbix.Api

The Zabbix JSON-RPC API's vocabulary as records: every enum, object, parameter set and request of Zabbix 7.0, 7.4 and
8.0, generated at build time from the API description extracted from the Zabbix source. Each member carries the
versions that have it. `Community.Zabbix.Sdk` sends and receives these records; reference this package alone to share the model.

```shell
dotnet add package Community.Zabbix.Api
```

```csharp
using Community.Zabbix;

var request = new ZabbixHostGetParameters
{
    Output = ZabbixOutput.Extend,
    Limit = 10,
};
Console.WriteLine(ZabbixApiMethods.All.Count);   // the methods the description knows, with their versions
```

Requires .NET 10 (`net10.0`). Works with Zabbix 7.0, 7.4 and 8.0 ([known limitations](https://github.com/schroeder247/community-zabbix/blob/main/docs/KNOWN-LIMITATIONS.md)).

Documentation: https://github.com/schroeder247/community-zabbix (README, docs/GETTING-STARTED.md).

Provided "as is", without warranty of any kind: use at your own risk.

This project is not affiliated with, endorsed by or sponsored by Zabbix LLC. Zabbix is a registered trademark of Zabbix LLC.
