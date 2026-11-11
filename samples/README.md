<!--
This Source Code Form is subject to the terms of the Mozilla Public
License, v. 2.0. Copyright (c) 2026 schroeder247. SPDX-License-Identifier: MPL-2.0
-->
# Samples

Small programs that use the SDK, one task each. They build with the solution, so they stay current.

| Sample | What it shows | Needs |
| --- | --- | --- |
| [GettingStarted](https://github.com/schroeder247/community-zabbix/tree/main/samples/GettingStarted) | The whole program from [Get started](../docs/GETTING-STARTED.md): create a host, send a value, read it back, and clean up | A lab server, and its address and token as arguments |
| [QueryApi](https://github.com/schroeder247/community-zabbix/tree/main/samples/QueryApi) | List hosts and read their latest values through the API | `ZABBIX_URL`, `ZABBIX_API_TOKEN` |
| [WatchValues](https://github.com/schroeder247/community-zabbix/tree/main/samples/WatchValues) | Print each new value of a host group | `ZABBIX_URL`, `ZABBIX_API_TOKEN` |
| [SendValues](https://github.com/schroeder247/community-zabbix/tree/main/samples/SendValues) | Send a value to a trapper item | `ZABBIX_SERVER` |
| [ReadAgentItem](https://github.com/schroeder247/community-zabbix/tree/main/samples/ReadAgentItem) | Read an item from an agent | An agent, as an argument |
| [AskServer](https://github.com/schroeder247/community-zabbix/tree/main/samples/AskServer) | Ask a server for its status and queue | `ZABBIX_SERVER`, `ZABBIX_API_TOKEN` |
| [PushAgent](https://github.com/schroeder247/community-zabbix/tree/main/samples/PushAgent) | Sample a value on a schedule and send it | `ZABBIX_SERVER` |
| [ActiveAgent](https://github.com/schroeder247/community-zabbix/tree/main/samples/ActiveAgent) | Answer your own item keys in active checks | `ZABBIX_SERVER` |
| [PassiveAgent](https://github.com/schroeder247/community-zabbix/tree/main/samples/PassiveAgent) | Answer the server's polls | Nothing |
| [ItemPlugin](https://github.com/schroeder247/community-zabbix/tree/main/samples/ItemPlugin) | A plugin an agent loads from a folder, and the provider inside it | An agent to load it |
| [WorkerService](https://github.com/schroeder247/community-zabbix/tree/main/samples/WorkerService) | Named clients and a hosted agent bound from `appsettings.json` | `Zabbix__Servers__prod__ApiToken` |

To run one, set what it needs and start it from the repository root:

```bash
export ZABBIX_SERVER=zbx-server-01
dotnet run --project samples/SendValues
```

The samples reference the projects in this repository. In your own application, add the packages instead, as
[Install](../docs/INSTALL.md) shows.

Provided "as is", without warranty of any kind: use at your own risk.
