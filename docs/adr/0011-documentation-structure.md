<!--
This Source Code Form is subject to the terms of the Mozilla Public
License, v. 2.0. Copyright (c) 2026 schroeder247. SPDX-License-Identifier: MPL-2.0
-->
# 0011. The documentation follows Microsoft's structure and voice

- **Status:** Accepted
- **Date:** 2026-11-04
- **Amends:** [0005](0005-documentation-set.md) (what the user-facing set holds)

## Context

The documentation was correct and hard to use. The README ran to 600 lines, with installation from four sources, a feature list,
and the legal text ahead of what a reader came for. The guides opened on how a piece works inside, and cited Zabbix's source
files where a reader needed to know what to do. Nothing checked the samples, so they could drift from the API.

## What the sources say

| Source | Rule | What it decides |
| --- | --- | --- |
| [Azure SDK README template](https://github.com/Azure/azure-sdk/blob/main/docs/policies/README-TEMPLATE.md) | Introduction, getting started, key concepts, examples, troubleshooting, next steps, contributing | The README's sections, in that order |
| [Package readme on NuGet.org](https://learn.microsoft.com/nuget/nuget-org/package-readme-on-nuget-org) | What it does, how to start, code samples, links to more, where to leave feedback | The package pages |
| [Azure SDK documentation guidelines](https://azure.github.io/azure-sdk/general_documentation.html) | Build and test snippets in CI, one operation in a snippet, cover the champion scenarios, a `TROUBLESHOOTING.md` | `DocumentationTests`, `samples/`, `docs/TROUBLESHOOTING.md` |
| [Microsoft Writing Style Guide](https://learn.microsoft.com/style-guide/top-10-tips-style-voice) | Get to the point fast, be brief, start with a verb, use contractions, sentence-case headings, the serial comma | The voice, in [CONTRIBUTING.md](../../CONTRIBUTING.md#writing-the-documentation) |
| [Developer content](https://learn.microsoft.com/style-guide/developer-content/) | Reference and code examples are the foundation; skip what a developer knows | Samples first; design detail stays in `docs/design/` |

`Azure.Messaging.ServiceBus`'s README, read as an example, has the same outline.

## Decision

1. **The README** has the template's sections. Installation from other sources, the feature list, and the legal text move to
   `docs/INSTALL.md`, `docs/FEATURES.md`, and `docs/LEGAL.md`. It still links every guide, in reading order, as a table of contents.
2. **A guide is a set of tasks.** It opens with what the piece is for and the smallest sample, and each section is a task, sample
   first. It ends with the piece in a host and from PowerShell, where they apply.
3. **Each guide covers one thing.** The passive agent moves to `zabbix-agents.md`, with the other agents. `listeners-and-proxies.md`
   splits into `proxies.md` and `listeners.md`, and `items.md` gives plugins their own `plugins.md`. The agents' guide isn't named
   `agents.md`, because on a case-insensitive file system AI coding tools read that name as an `AGENTS.md` instructions file.
4. **`docs/TROUBLESHOOTING.md`** says what an error means and what to do about it.
5. **`samples/`** holds small programs, one task each, in the solution, so the build compiles them.
6. **Tests hold it.** `DocumentationTests` compiles every C# sample in the reader's pages against the libraries, and resolves every
   link and heading in every page.

The design documents and the ADRs aren't rewritten. They're for maintainers, and they can cite Zabbix's source.

## Rejected

- **Generating snippets from tests, as Azure's tooling does.** It takes a tool to keep them in step. Compiling each sample where
  it stands holds the same promise, and leaves a sample free to assume names of the application's own.
- **Running the samples in CI.** They need a server, and the live suite already exercises what they call. They compile, and that
  catches what drifts.

## Consequences

- A renamed member, page, or heading fails the build's tests rather than reaching a reader.
- A sample may assume a variable, such as `apiToken`. The check only reports what it says of the SDK.
