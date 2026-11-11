<!--
This Source Code Form is subject to the terms of the Mozilla Public
License, v. 2.0. Copyright (c) 2026 schroeder247. SPDX-License-Identifier: MPL-2.0
-->
# 0001. Repository layout: the rebuild is the root, the 26.x line is archived outside the repository

- **Status:** Accepted
- **Date:** 2026-10-05

## Context

Until 2026-10-05 the repository held two products side by side. The root (`src/`, `tests/`, `docs/`, `cookbook/`, the
docfx site under `api/` and `api-next/`, `ZabbixClient.sln`, `scripts/`, `tools/`) was the 26.x line: the `Zabbix.Client*`
packages and the `Zabbix.Client.PowerShell` module, frozen and never published publicly. `next/` held the rebuild —
the product, `Zabbix.slnx`, whose first public release is 1.0.0.

The split cost something on every change: every path in the product carried a `next/` prefix; CI built and tested the
frozen line on three operating systems on every push although nothing in it changed; the release workflow, the docs
workflow and Dependabot ran from a subdirectory; the docs site published two API references; and a reader arriving at
the root found the archived product first. The only live dependency on the 26.x tree was the PowerShell parity harness
(`tests/Zabbix.PowerShell.Parity/`), which runs the 26.x module's own Pester suites (`tests/Integration`, `tests/Unit`,
`tests/Help`) unchanged against the new module.

## Decision

1. The 26.x line is archived outside the repository (a copy of its tree and a bundle of the history that held it, kept
   by the owner). Nothing of it remains in the repository: no tag, no branch, no changes, no dependency updates and no
   releases. It was never in production, so nothing migrates from it.
2. The rebuild is the repository root, laid out as a standard .NET repository: `src/` (the libraries, the generator,
   the PowerShell module), `tests/` (the test projects, the podman lab, the Pester live suite, and the analyzer probe
   under `tests/probes/`), `tools/`, `schema/`, `docs/`, `assets/` (the package icon), `Zabbix.slnx`, `global.json` and the
   `Directory.Build.*` files. The README keeps the legal sections (warranty disclaimer, trademark notice, licence
   summary) of the old root README.
3. The parity harness, which ran the 26.x module's own Pester suites against the new modules, went with the line it
   compared against; its lab functions (the servers from the environment contract, their tokens, the leak sweep) live
   on in `tests/lab/Lab.psm1`, which the Pester live suite and `live.sh` import.
4. CI builds, tests, formats and packs the root solution only (job `build`).

## Consequences

- Paths lose the `next/` prefix: `docs/X` rather than `next/docs/X`, `src/…` rather than `next/src/…`. Documents dated
  before 2026-10-05 that say `next/` mean what is now the root; point-in-time records (the audits, the review files) are
  not rewritten beyond their paths. (Those documents and records are in the maintainer's archive, not in this
  repository: [ADR 0005](0005-documentation-set.md), amendment.)
- The 26.x sources, docs and history are reachable only in the owner's archive, not from a clone.
- The analyzer probe moved under `tests/`, so the test-only `.editorconfig` section is scoped to the test projects
  (`[tests/Zabbix*/**.cs]`) and the probe imports the root `Directory.Build.props` directly: it is still analysed as a
  library, as the rules it proves require.
- One solution, one API reference (`api/`), one changelog: the docs site, Dependabot and the release workflow run from
  the root.
