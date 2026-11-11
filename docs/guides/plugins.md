<!--
This Source Code Form is subject to the terms of the Mozilla Public
License, v. 2.0. Copyright (c) 2026 schroeder247. SPDX-License-Identifier: MPL-2.0
-->
# Plugins

A plugin is a class library an active or passive agent loads from a folder you name, as Zabbix's agent loads modules
(`LoadModulePath`, `LoadModule`). It adds [item keys](items.md) to the agent without the application referencing them. Loading is
in the `Community.Zabbix.Agent.Plugins` package.

```bash
dotnet add package Community.Zabbix.Agent.Plugins
```

```csharp
using Community.Zabbix;

await using ZabbixActiveAgent agent = ZabbixActiveAgent.CreateBuilder("web-01")
    .AddServer("zbx-server-01")
    .AddPlugins(new ZabbixPluginOptions { Path = "/opt/zabbix-dotnet/plugins", Load = { "Queues" } })
    .Build();
await agent.StartAsync();
```

The agent loads `/opt/zabbix-dotnet/plugins/Queues/Queues.dll`, and answers the keys the plugin registers.

## Write a plugin

A plugin is a class library that references `Community.Zabbix.Abstractions`, holds
[item providers](items.md#ship-a-set-of-keys-as-a-provider), and names its entry, which registers them:

```csharp
using Microsoft.Extensions.Configuration;
using Community.Zabbix;

[assembly: ZabbixPlugin(typeof(QueuePlugin))]

public sealed class QueuePlugin : ZabbixPluginEntry
{
    public override void Register(IZabbixItemsBuilder items, IConfiguration settings)
        => items.AddItems(new QueueItems(new QueueReader(settings["ConnectionString"])));
}
```

- The entry registers its providers as a library's registration does, so one library ships both ways.
- The entry needs a public parameterless constructor. The agent creates it once.
- `settings` is the plugin's own section of the configuration, empty when it has none.
- A plugin's keys follow [the rules every key follows](items.md#the-rules-every-key-follows). A name another provider holds is
  refused with `ArgumentException`.

## Publish a plugin to the folder

Reference `Community.Zabbix.Abstractions` with `ExcludeAssets="runtime"`, so the publish output leaves it out. A copy that's there
is ignored.

```xml
<PackageReference Include="Community.Zabbix.Abstractions" Version="1.0.0" ExcludeAssets="runtime" />
```

Then publish to a subfolder named after the plugin:

```bash
dotnet publish Queues -c Release -o /opt/zabbix-dotnet/plugins/Queues
```

The folder holds one subfolder per plugin, `<Path>/<Name>/`, with the output of `dotnet publish` for the plugin's project, its
assembly `<Name>.dll` among it.

## Choose what loads

| `ZabbixPluginOptions` | Default | Meaning |
| --- | --- | --- |
| `Path` | None | The folder the plugins are in. Required when `Load` names a plugin. |
| `Load` | Empty | The plugins to load, by name, in order. A file in the folder that nothing lists is never opened. |
| `Settings` | None | Where each plugin's settings are: the section named after the plugin is what its entry receives. Without it, each plugin gets an empty section. |

`AddPlugins` also takes a configuration section. A running agent loads plugins through its `Items`:
`agent.Items.AddPlugins(options)`, which returns `Items`.

## In an application with a host

A section of the configuration holds the list and the settings. `Load` is an array or a comma-separated list, and each plugin's
settings are the subsection named after it:

```json
{
  "Zabbix": {
    "ActiveAgents": {
      "web-01": {
        "ServerActive": "zbx-server-01",
        "Plugins": {
          "Path": "/opt/zabbix-dotnet/plugins",
          "Load": [ "Queues" ],
          "Queues": { "ConnectionString": "Server=mq-01" }
        }
      }
    }
  }
}
```

```csharp
IConfigurationSection row = builder.Configuration.GetSection("Zabbix:ActiveAgents:web-01");
builder.Services.AddZabbixActiveAgent("web-01")
    .Bind(row)                                                            // ServerActive
    .AddPlugins(row.GetSection("Plugins"));
```

You must call `AddPlugins` yourself, on the active or passive agent's builder. `AddZabbixAgents` and `AddZabbixProxies` don't read
the `Plugins` section.

## Handle a plugin that won't load

`AddPlugins` loads the plugins when you call it, on a builder or on `Items`. It throws `InvalidOperationException`, saying why, so
the agent doesn't start, when:

- A listed plugin is missing, doesn't load, or names no entry.
- A plugin was built against a later major version of `Community.Zabbix.Abstractions`.
- `Load` names plugins and `Path` isn't set, or `Load` lists a plugin twice or holds a name that isn't a folder name.
- A plugin's entry can't be created, or its `Register` fails.
- On Linux or macOS, other accounts can write to a plugin's files, or to a folder above it whose sticky bit doesn't stop them
  replacing the plugin.

The permission check reads the file mode, so it runs on Linux and macOS. On Windows, the folder's access list is what keeps a
plugin the agent's.

## Know what a plugin shares with the agent

- **Its dependencies are its own.** Each plugin has its own load context, so two plugins, or a plugin and the agent, can depend on
  different versions of one package. The SDK's assemblies (`Community.Zabbix.*`) and the `Microsoft.Extensions` abstractions,
  options, and primitives are the agent's when the agent has them.
- **Its process is the agent's**, with the agent's account. A handler that throws answers not supported, as any handler does. A
  crash on a thread of the plugin's own ends the process.
- **Nothing unloads.** To update a plugin, restart the process.
- **Native AOT is out.** Loading assemblies at run time rules it out, so an application that loads no plugins leaves the package
  out.

## Load plugins from PowerShell

`-Plugin`, `-PluginPath`, and `-PluginSetting` on `Start-ZbxActiveAgent` and `Start-ZbxPassiveAgent` load plugins as `AddPlugins`
does:

```powershell
Start-ZbxActiveAgent -Name web -HostName web-01 -Server zbx-server-01 -Plugin Queues -PluginPath /opt/zabbix-dotnet/plugins `
    -PluginSetting @{ Queues = @{ ConnectionString = 'Server=mq-01' } }
```

`-Plugin` needs `-PluginPath`. A plugin that can't load fails the start. A scriptblock registered under a plugin's key replaces
it.

## See also

- [Items](items.md): handlers, providers, and the rules every key follows.
- [Bundled items](bundled-items.md): the keys an agent answers without a plugin.
- [Configuration](configuration.md): the agents' sections.
- [Known limitations](../KNOWN-LIMITATIONS.md): what a plugin in the agent's process can and can't do.
- The [ItemPlugin sample](../../samples/README.md): a plugin and the provider inside it.
