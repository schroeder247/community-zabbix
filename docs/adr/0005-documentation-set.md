<!--
This Source Code Form is subject to the terms of the Mozilla Public
License, v. 2.0. Copyright (c) 2026 schroeder247. SPDX-License-Identifier: MPL-2.0
-->
# 0005. The documentation set is written for users; the engineering record lives in history

- **Status:** Accepted; amended 2026-10-30 (the published history; guides, reference and a contributing section; below)
- **Date:** 2026-10-05

## Context

The SDK was built through a series of audits, reviews and status ledgers, and their documents sat beside the user
documentation under `docs/`: a 750-line status ledger, two audit reports with their findings tables, a seven-file
review, two parity reports and a 1,700-line per-command parity checklist, a plan for a separate application, research
notes and an archive. The README, the changelog and the guides cited them, and about 240 comments in `src/` and
`tools/` cited their finding ids, review branches, dates and section numbers. A professional review before the first
release found that the code's comments and the documentation spoke to the people who had reviewed the code rather than
to the people who would use it: a newcomer could not tell which of twenty documents to read, and an API summary often
opened with a label or a finding id instead of saying what the member does.

## Decision

1. **The user-facing set** is small and current:

   | Document | For |
   | --- | --- |
   | `README.md` | What the SDK and the modules are, how to install them, a quick start, telemetry |
   | `docs/GETTING-STARTED.md`, `src/Zabbix.PowerShell/GETTING-STARTED.md` | Walk-throughs for a .NET developer and a PowerShell user |
   | `CHANGELOG.md` | What a release adds and limits |
   | `docs/KNOWN-LIMITATIONS.md` | What does not work yet, and the deliberate limits |
   | `docs/TESTING.md`, `CONTRIBUTING.md` | Building, the test suites and the live lab; how to contribute |
   | `docs/design/` | The design for maintainers: architecture, decisions and rejected shapes, the platform survey, the PowerShell binding survey |
   | `docs/adr/` | The decisions that shape the repository |
   | `docs/INDEX.md` | The map of the above |

2. **The engineering record is not on `main`.** The status ledger (`docs/STATUS.md`), the two audits (the
   compliance plan and the C# conformance review), the review (`docs/review-2026-09-27/`), the
   parity reports and checklist (`docs/PARITY-REPORT.md`, `docs/PARITY-PARAMETERS.md`, `src/Zabbix.PowerShell/PARITY.md`),
   the Windows application plan and research (`docs/WINDOWS-APP-PLAN.md`, `docs/research/`) and `docs/archive/` are
   deleted from `main`. They remain in git history (where the rebuild's documents are under `next/`, for example
   `next/docs/STATUS.md`). The design documents that describe the built system moved to `docs/design/`:
   `ARCHITECTURE-TARGET.md` → `architecture.md`, `PHILOSOPHY-APPLIED.md` → `decisions.md` (reduced to the decisions
   register), `PLATFORM-SURVEY.md` → `platform-survey.md`, and `research/powershell-binding-seams-2026-09-25.md` →
   `powershell-binding.md`. The status ledger's open items became `docs/KNOWN-LIMITATIONS.md`, and its gates and
   environment facts became `docs/TESTING.md`.

3. **No process ids in source.** A comment in `src/` or `tools/` explains the code to a maintainer and, in a public
   `<summary>`, to a user in Microsoft's reference voice. It does not cite review or finding ids, review branches,
   session dates, waves, the design standard's section numbers, or the deleted documents; it keeps the reason and the
   measured result. A citation of Zabbix's own source, of Microsoft documentation or of an RFC is not process and stays.
   A public summary does not open with a bold label. `DesignConformanceTests.Comments` scans the sources and fails
   naming each offending line.

## Consequences

- A reader of `main` sees only documents that are kept current. A fact that only a deleted document held is found
  with `git log -- <path>`.
- A change that alters behaviour updates the user document that describes it; a new limitation goes into
  `docs/KNOWN-LIMITATIONS.md` and the changelog, not into a status file.
- Earlier ADRs keep their text; where they name a deleted document, that document is in history.

## Amendment (2026-10-30): the published history starts at the first release

The public repository was recreated with a single initial commit before the first release, so its history starts
there. The development history this ADR and the earlier ADRs call "git history" (the deleted documents, the commits
that name a deletion, `next/` and the 26.x line) is kept in the maintainer's archive, a mirror of the earlier
repository, and is not published. `git log -- <path>` finds a deleted document in that archive, not in this
repository. The decision stands: the published documents are the ones kept current.

## Amendment (2026-10-30): guides, reference and a contributing section

The README is a short quick start. The user documentation is `docs/`: the walk-throughs, ten guides under
`docs/guides/`, the cmdlet reference under `docs/reference/` (ADR 0004, amendment) and the known limitations, mapped by
`docs/INDEX.md`. GitHub renders all of it, and the documentation site publishes the same pages with the .NET API
reference. The design documents and the ADRs are published under a Contributing section. The PowerShell module
contract moved from `src/Zabbix.PowerShell/README.md` to `docs/design/powershell-module.md`, and the PHP research note
was removed.
