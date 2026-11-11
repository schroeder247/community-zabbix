<!--
This Source Code Form is subject to the terms of the Mozilla Public
License, v. 2.0. Copyright (c) 2026 schroeder247. SPDX-License-Identifier: MPL-2.0
-->
# 0002. Package ids: the `Zabbix.*` family, assembly name = package id

- **Status:** Accepted; the package table is amended by [0006](0006-protocol-split.md) (`Zabbix.Wire` added, the EF store renamed `Zabbix.Agent.Storage.EntityFrameworkCore`, `Zabbix.Configuration.Yaml` removed) and by [0008](0008-item-providers-and-plugins.md) (`Zabbix.Agent.Plugins` added); the root namespace is amended below; the package ids are amended below (2026-10-12: `Community.` + the assembly name), and the assembly names and the root namespace after them (2026-11-03: `Community.Zabbix` throughout)
- **Date:** 2026-10-05

## Context

The SDK's root library was built as the assembly `Zabbix` and packed under a different id, `Zabbix.Client`, because the
ids it would naturally take are not available:

- `Zabbix` and `ZabbixClient` on nuget.org belong to other projects.
- `Zabbix.Client` is the id of this repository's archived 26.x line (`adr/0001-repository-layout.md`).
  Its 26.0.0 was published to GitHub Packages; a feed that holds both would resolve `Zabbix.Client` to 26.0.0, which outranks
  1.0.0, and a reader of either package page would take one product for the other.

An id that differs from its assembly name also costs on every tool that maps one to the other: a deps.json entry is
named after the package, a loaded assembly after the file, and the conformance tests had to load libraries by asset
file name to stay correct.

The same release splits the hosts (the agents, the send loop, the poll and meter sources, the HA resolver) out of the
SDK into their own package, so an application that only calls the API does not take `Microsoft.Extensions.Hosting`,
health checks and service discovery.

## Decision

1. The root library is the assembly and package **`Zabbix.Sdk`** (folder `src/Zabbix.Sdk`): the API (`ZabbixClient`,
   `AddZabbix`, the transport, the generated clients), the tree, the wire protocol, the in-memory store, redaction.
2. The family, each assembly name equal to its package id:

   | Package | Holds |
   | --- | --- |
   | `Zabbix.Abstractions` | the contracts |
   | `Zabbix.Api` | the generated API vocabulary |
   | `Zabbix.Sdk` | the server, clients, tree, wire, memory store |
   | `Zabbix.Agent` | the hosts: the agents, the send loop, the poll and meter sources, the HA resolver |
   | `Zabbix.Proxy` | the listeners, trapper, passive agent, proxies, connector |
   | `Zabbix.Storage.EntityFrameworkCore` | the durable store on EF Core |
   | `Zabbix.Configuration.Yaml` | the YAML configuration source |

3. The root namespace stays `Zabbix` in every package; the package boundary is not a namespace boundary.

## Consequences

- `dotnet add package Zabbix.Sdk` replaces `Zabbix.Client`; a host adds `Zabbix.Agent` (or `Zabbix.Proxy`, which brings
  it). No 1.x package uses the 26.x ids, so the two lines never compete for a version on one feed.
- The PowerShell module ships `Zabbix.Sdk.dll` and `Zabbix.Agent.dll` beside its own assembly.
- `InternalsVisibleTo` names the new assemblies (`Zabbix.Sdk`, `Zabbix.Agent`); a type that crosses only between the
  family's assemblies stays internal.
- Before `Zabbix.Sdk` is first published, its id on nuget.org is checked again; if it was taken meanwhile, this ADR is
  superseded rather than the id silently changed.

## Amendment (2026-10-07): the root namespace

Decision 3 keeps the root namespace `Zabbix`. The Framework Design Guidelines ask to "prefix namespace names with a
company name to prevent namespaces from different companies from having the same name"
([names of namespaces](https://learn.microsoft.com/dotnet/standard/design-guidelines/names-of-namespaces)), and
`Zabbix` is the product's own name, which its vendor or another library may also use. The guidance wins: the namespace
is a divergence to close before 1.0 ([decisions register](../design/decisions.md#documented-divergences-to-be-closed)),
and the prefix is the owner's choice. Package ids are not namespaces and are unaffected.

## Amendment (2026-10-12): the package ids carry `Community.`

Zabbix's trademark policy (<https://www.zabbix.com/trademark>) needs a licence for a name that pairs "Zabbix" with one of
its listed words ("agent", "proxy" among them) or suggests an official link, and allows "zabbix" inside source-code
identifiers as nominative fair use. Every package id is `Community.` followed by the assembly name: `Community.Zabbix.Abstractions`, `Community.Zabbix.Api`, `Community.Zabbix.Sdk`, `Community.Zabbix.Wire`,
`Community.Zabbix.Agent`, `Community.Zabbix.Proxy` and `Community.Zabbix.Agent.Storage.EntityFrameworkCore`.
`Community` means community software for Zabbix, as in Ansible's `community.zabbix` collection; it names no company.

This replaces "assembly name = package id" in the title and in decision 2. The assembly names, the root namespace
`Zabbix` and every type name stay: they are identifiers, which the policy names as fair use, and `using Zabbix;` is what
a developer expects. A deps.json and a lock file name a project by its package id, so the conformance tests load each
library by its runtime asset's file name (`tests/Zabbix.Tests/Conformance/Libraries.cs`) and read restore closures by
package id (`DesignConformanceTests.Dependencies.cs`). This also settles the earlier amendment's namespace question: the
namespace keeps `Zabbix` (docs/design/decisions.md, "Zabbix's trademark and licences").

## Amendment (2026-11-03): assemblies, namespaces and telemetry carry `Community.Zabbix`

Every assembly takes its package's id as its name: `Community.Zabbix.Abstractions`, `Community.Zabbix.Api`,
`Community.Zabbix.Sdk`, `Community.Zabbix.Wire`, `Community.Zabbix.Agent`, `Community.Zabbix.Proxy`,
`Community.Zabbix.Agent.Storage.EntityFrameworkCore` and `Community.Zabbix.Agent.Plugins`. The PowerShell modules'
assemblies follow: `Community.Zabbix.PowerShell`, `Community.Zabbix.PowerShell.Agent` and
`Community.Zabbix.PowerShell.Proxy` (the module `Community.Zabbix.Agent` ships the library `Community.Zabbix.Agent.dll`,
so a module's assembly cannot take the module's own name).

A file in an application's output or a module folder then names the package it came from, and every tool that maps a
package to its assembly (a deps.json, a lock file, the plugin loader's shared assemblies) sees one name, which restores
"assembly name = package id" to the title and to decision 2. `InternalsVisibleTo` names the `Community.Zabbix.*`
assemblies.

The root namespace follows: `Community.Zabbix`, with the layers beneath it (`Community.Zabbix.Protocol`,
`Community.Zabbix.Hosting`, `Community.Zabbix.Diagnostics`, `Community.Zabbix.PowerShell`, …). This replaces decision 3's
`Zabbix` and closes the first amendment's question the way the Framework Design Guidelines answer it: a prefix the
project owns, so `Zabbix` alone stays free for the vendor and for other libraries, and a developer types the name they
installed (`using Community.Zabbix;`). The telemetry names follow the namespace, as a library's activity sources and
meters conventionally do: `Community.Zabbix.Api`, `Community.Zabbix.Wire` and the other layers, subscribed to together
as `Community.Zabbix.*`, the prefix the log categories (the emitting types' names) already carry.

The project folders (`src/Zabbix.Sdk`), the type names (`ZabbixClient`, `ZabbixHost`), the cmdlet noun prefix `Zbx`, the
configuration section `Zabbix` and the metric and span names (`zabbix.wire.exchange.duration`) stay: they are
identifiers and wire-level words, which the earlier amendment keeps for the reasons it gives.
