# Community.Zabbix.Agent.Plugins

Plugins for agents built with the Community Zabbix SDK: item providers an agent loads from a folder you name, as Zabbix's
own agent loads modules. Each plugin is listed by name and loaded into a context of its
own, so plugins can depend on different versions of one package; a file in the folder that nothing lists is never
opened. It takes the item contract in `Community.Zabbix.Abstractions` and nothing else, and works with the active and
the passive agent alike. Loading assemblies at run time rules out native AOT, so an application that loads no plugins
leaves this package out.

```shell
dotnet add package Community.Zabbix.Agent.Plugins
```

```csharp
using Community.Zabbix;

// /opt/zabbix-dotnet/plugins/SqlServer/ holds the plugin's dotnet publish output, SqlServer.dll among it.
await using ZabbixActiveAgent agent = ZabbixActiveAgent.CreateBuilder("web-01")
    .AddServer("zbx-server-01")
    .AddPlugins(new ZabbixPluginOptions { Path = "/opt/zabbix-dotnet/plugins", Load = { "SqlServer" } })
    .Build();
await agent.StartAsync();
```

`AddPlugins(configuration.GetSection("Zabbix:ActiveAgents:web-01:Plugins"))` on the agent's builder, the one
`CreateBuilder` or `AddZabbixActiveAgent` returns, reads the same list (`Path`, `Load`) and each plugin's settings under its
name. A running agent loads plugins with `agent.Items.AddPlugins(…)`.

A plugin is a class library that references `Community.Zabbix.Abstractions`, implements `ZabbixItemProvider` and names
its `ZabbixPluginEntry` with `[assembly: ZabbixPlugin(typeof(…))]` (namespace `Community.Zabbix`). A listed plugin that
cannot be loaded stops the agent from starting, and on Linux and macOS a plugin whose files other accounts can write is
refused. [The plugins guide](https://github.com/schroeder247/community-zabbix/blob/main/docs/guides/plugins.md)
shows a plugin end to end.

Requires .NET 10 (`net10.0`). Works with Zabbix 7.0, 7.4 and 8.0 ([known limitations](https://github.com/schroeder247/community-zabbix/blob/main/docs/KNOWN-LIMITATIONS.md)).

Documentation: https://github.com/schroeder247/community-zabbix (README, docs/GETTING-STARTED.md).

Provided "as is", without warranty of any kind: use at your own risk.

This project is not affiliated with, endorsed by or sponsored by Zabbix LLC. Zabbix is a registered trademark of Zabbix LLC.
