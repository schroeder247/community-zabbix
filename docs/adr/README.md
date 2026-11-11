<!--
This Source Code Form is subject to the terms of the Mozilla Public
License, v. 2.0. Copyright (c) 2026 schroeder247. SPDX-License-Identifier: MPL-2.0
-->
# Architecture decision records

An architecture decision record (ADR) is a short document that records one decision that shapes the repository or the
design — its context, the decision itself, and its consequences — so a later reader can see why things are the way they
are without reconstructing the discussion. ADRs are numbered in the order they are written (`0001-…`, `0002-…`), four
digits and a kebab-case title; a number is never reused. An accepted ADR is not rewritten: a later decision that changes
it is a new ADR whose status says which one it supersedes, and the old one's status is changed to "Superseded by NNNN".

An ADR yields to Microsoft's published guidance: where a decision here disagrees with the Framework Design Guidelines,
the library guidance, the Microsoft.Extensions documentation or the PowerShell cmdlet development guidelines, the
guidance wins, and the ADR gains a dated amendment that cites it ([the precedence rule](../design/decisions.md#precedence)).

| ADR | Decision |
| --- | --- |
| [0001](0001-repository-layout.md) | The SDK is the repository root, a standard .NET layout; an earlier, unreleased line is archived outside it. |
| [0002](0002-package-ids.md) | The packages, their assemblies and the root namespace are the `Community.Zabbix.*` family (amended). |
| [0003](0003-powershell-module-split.md) | The PowerShell surface is three modules, `Community.Zabbix`, `Community.Zabbix.Agent` and `Community.Zabbix.Proxy` (amended); the two siblings require `Community.Zabbix`. |
| [0004](0004-conventions.md) | The tree follows Microsoft's own repository conventions, each rule held by the build or a test. |
| [0005](0005-documentation-set.md) | The documentation is written for users; the engineering record is kept out of the tree, in the maintainer's archive (amended). |
| [0006](0006-protocol-split.md) | One package per protocol, one identity with two views, `Zabbix.Data` reserved, and the Zabbix sources pinned. |
| [0007](0007-container-free-runners.md) | Every entry point works without a container: clients that ask only for what connecting needs, and a `ZabbixRunner` for the hosted components. Superseded in part by [0010](0010-agent-proxy-client-classes.md) (§3 to §5). |
| [0008](0008-item-providers-and-plugins.md) | Agents answer keys from item providers: a contract in `Zabbix.Abstractions`, providers registered from code, and plugins loaded from a folder the configuration names. |
| [0009](0009-bundled-items.md) | Agents answer Zabbix's own keys out of the box, from the .NET libraries, the OS's own files and a short list of documented OS calls, below every other key, with Zabbix's `AllowKey` and `DenyKey` rules. |
| [0010](0010-agent-proxy-client-classes.md) | Clients, agents and proxies are classes you construct: one lifecycle (`ZabbixComponent`), one table of keys (`Items`), registrations that call the constructors, and what a reader names in the root namespace. |
| [0011](0011-documentation-structure.md) | The documentation follows Microsoft's structure and voice: the README's sections, guides as tasks, `samples/`, and tests that compile every sample and resolve every link |
| [0012](0012-typed-bcl-fluent-api.md) | The public API is typed, built on the BCL, and fluent: `ZabbixItem<T>`, `AddMeter`, BCL types at the surface, and a fluent generated API |
| [0013](0013-builders-verbs-and-typed-values.md) | One builder shape for every component, one meaning per verb, delegates and generics for items, lambdas for writes, and types for what was text |
