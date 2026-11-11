<!--
This Source Code Form is subject to the terms of the Mozilla Public
License, v. 2.0. Copyright (c) 2026 schroeder247. SPDX-License-Identifier: MPL-2.0
-->
# Community Zabbix documentation

The full documentation, as Markdown. GitHub renders every page here, and the
[documentation site](https://schroeder247.github.io/community-zabbix/) publishes the same pages with search and the .NET
API reference.

## Start here

| Page | What it covers |
| --- | --- |
| [README](../README.md) | What the SDK and the modules are, the key concepts, and an example of each task |
| [Install](INSTALL.md) | nuget.org and the PowerShell Gallery, a GitHub release, GitHub Packages, and source |
| [Getting started (.NET)](GETTING-STARTED.md) | A program built step by step: create a host, send a value, read it back, and run an agent |
| [Getting started (PowerShell)](../src/Zabbix.PowerShell/GETTING-STARTED.md) | The same with the modules |
| [Samples](../samples/README.md) | Small programs, one task each |
| [Troubleshooting](TROUBLESHOOTING.md) | What an error means and how to fix it, and how to turn on logging |

## Guides

Each guide starts with the piece on its own and, where it registers in a host, ends with that.

| Guide | What it covers |
| --- | --- |
| [The API client](guides/api-client.md) | `ZabbixClient`: credentials, the generated clients, errors, retries, version gating, and frontend links |
| [The tree and the watcher](guides/tree-and-watcher.md) | Latest values and history by host and item, and watching them |
| [The wire client](guides/wire-client.md) | `ZabbixWireClient`: sending values, reading an agent's items, and asking a server for its status and queue |
| [Agents](guides/zabbix-agents.md) | The push, active, and passive agents, and their lifetime, status, and health |
| [Items](guides/items.md) | The keys an agent answers: handlers, providers, and PowerShell scriptblocks |
| [Plugins](guides/plugins.md) | Item keys loaded from a folder |
| [Bundled items](guides/bundled-items.md) | Zabbix's own keys that every agent answers, by family and platform, and the `AllowKey` and `DenyKey` rules |
| [Stores](guides/stores.md) | Keeping values until the server has them: memory, persistent, and EF Core stores |
| [Proxies](guides/proxies.md) | The active and passive proxies |
| [Listeners](guides/listeners.md) | The trapper, the connector endpoint, and the pieces beneath them |
| [TLS and PSK](guides/tls-and-psk.md) | Certificate and pre-shared-key encryption for the wire, the agents, and the listeners |
| [Telemetry](guides/telemetry.md) | Logs, traces, and metrics, OpenTelemetry, redaction, and the modules' OTLP export |
| [Configuration](guides/configuration.md) | Binding servers, agents, listeners, and plugins from `appsettings.json` and other sources |
| [PowerShell](guides/powershell.md) | The three modules: connections, the object commands, sessions, and help |

## Reference

| Reference | What it is |
| --- | --- |
| [Community.Zabbix commands](reference/Community.Zabbix/README.md) | One page per cmdlet of the `Community.Zabbix` module |
| [Community.Zabbix.Agent commands](reference/Community.Zabbix.Agent/README.md) | One page per cmdlet of the agent module |
| [Community.Zabbix.Proxy commands](reference/Community.Zabbix.Proxy/README.md) | One page per cmdlet of the proxy module |
| `Get-Help about_Community.Zabbix` | The modules' conceptual help topics (sessions, properties, the pipeline, filters, error handling, agents), shipped in each module's `en-US` folder |
| [.NET API reference](https://schroeder247.github.io/community-zabbix/api/) | Every public type and member of the eight libraries, on the documentation site |
| Package pages, as nuget.org shows them | [Abstractions](../src/Zabbix.Abstractions/README.md), [Api](../src/Zabbix.Api/README.md), [Sdk](../src/Zabbix.Sdk/README.md), [Wire](../src/Zabbix.Wire/README.md), [Agent](../src/Zabbix.Agent/README.md), [Proxy](../src/Zabbix.Proxy/README.md), [Agent.Storage.EntityFrameworkCore](../src/Zabbix.Agent.Storage.EntityFrameworkCore/README.md), [Agent.Plugins](../src/Zabbix.Agent.Plugins/README.md) |
| [Generator diagnostics](generator-diagnostics.md) | The source generator's diagnostics, ZBX0001 to ZBX0008 |
| [Features](FEATURES.md) | What each package and module does |
| [Known limitations](KNOWN-LIMITATIONS.md) | Known gaps and deliberate limits, each with its context |

## The project

| Page | What it covers |
| --- | --- |
| [Changelog](../CHANGELOG.md) | What each release adds and limits |
| [Security](../SECURITY.md) | How to report a vulnerability, the scope, and what the wire's TLS checks |
| [License, trademark, and warranty](LEGAL.md) | The MPL-2.0 in plain English, the trademark notice, and the disclaimer of warranty |
| [Third-party notices](../THIRD-PARTY-NOTICES.md) | The Zabbix sources the API model is extracted from, and the licences of redistributed components |
| [Code of conduct](../CODE_OF_CONDUCT.md) | Expected behaviour and how to report a problem |

## Contributing

| Page | What it covers |
| --- | --- |
| [Contributing](../CONTRIBUTING.md) | Reporting problems, the gates, the conventions, pull requests and the release steps |
| [Testing](TESTING.md) | The test suites, the live lab and its environment, regenerating the API model |
| [Architecture](design/architecture.md) | The one-function model, the control and data planes, the layers |
| [Design decisions](design/decisions.md) | The decisions and their reasons, Zabbix's trademark and licences, rejected shapes, measured facts |
| [The PowerShell module contract](design/powershell-module.md) | The cmdlet bases, the runspace context, what the generator emits, and the modules on the SDK's public API |
| [PowerShell binding](design/powershell-binding.md) | PowerShell's binding seams and which ones the modules use |
| [Platform survey](design/platform-survey.md) | The Microsoft libraries the agents, proxies and listeners are built on, and why |
| [The API model extractor](../tools/ZabbixApiExtract/README.md) | How the API model is extracted from the pinned Zabbix sources |
| [Architecture decision records](adr/README.md) | ADR 0001 to 0013: layout, package ids, the module split, conventions, the documentation set, one package per protocol, entry points without a container, item providers and plugins, bundled items, clients, agents and proxies as classes you construct, one builder shape and one meaning per verb |
