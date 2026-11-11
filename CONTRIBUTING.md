<!--
This Source Code Form is subject to the terms of the Mozilla Public
License, v. 2.0. Copyright (c) 2026 schroeder247. SPDX-License-Identifier: MPL-2.0
-->
# Contributing to Community Zabbix

Community Zabbix is maintained by one person in their spare time. Bug reports, questions and small fixes are welcome.
For anything bigger than a small fix, open an issue first, so the approach is agreed before you spend time on it.
Issues and pull requests are answered on a best-effort basis; there is no response-time commitment.

## Reporting bugs, requesting features, asking questions

Use the issue forms on GitHub. A useful bug report gives the Zabbix server version, the .NET (`dotnet --version`) or
PowerShell (`$PSVersionTable.PSVersion`) version, a minimal reproduction and the relevant log output, with tokens,
passwords and pre-shared keys redacted. A question is a blank issue. A security problem goes through
[SECURITY.md](SECURITY.md), never a public issue.

## License of contributions

This project is licensed under the [Mozilla Public License Version 2.0 (MPL-2.0)](LICENSE). By submitting a
contribution (code, documentation, tests, examples) you agree that it is licensed under MPL-2.0. You keep the copyright
on it. No sign-off line is required on commits.

## Build, test and the gates

The .NET 10 SDK is pinned by `global.json`. Before you open a pull request, run what CI runs:

```bash
dotnet build Zabbix.slnx -warnaserror                  # 0 warnings: every analyzer rule is an error
dotnet format Zabbix.slnx --verify-no-changes          # formatting
dotnet test --solution Zabbix.slnx                     # the test suites
dotnet pack Zabbix.slnx -c Release -warnaserror        # package validation
```

- **A behaviour change comes with a test.** Each test project sets a minimum test count (`--minimum-expected-tests` in
  its `.csproj`); raise it when you add tests.
- **On Linux, the `Zabbix.Tests` count includes 14 PostgreSQL tests.** Set `ZABBIX_TEST_POSTGRES` to the lab's PostgreSQL
  (`tests/lab/lab.sh start pg`), or the run skips them and fails its count. Tests against real Zabbix servers are
  opt-in; [docs/TESTING.md](docs/TESTING.md) explains the lab.
- **The public API is tracked.** A public member added or changed fails the build until `tools/refresh-public-api.sh`
  records it in the library's `PublicAPI.Unshipped.txt`; review that diff.
- **Dependencies are locked.** A package added, removed or bumped regenerates the lock files in the same commit:
  `dotnet restore Zabbix.slnx --force-evaluate`.
- **The API model is extracted, not written.** `schema/zabbix-api.json` comes from the Zabbix sources that
  `tools/zabbix-sources.lock` pins; regenerate it with `tools/regenerate-schema.sh` and never edit it by hand
  ([tools/ZabbixApiExtract/README.md](tools/ZabbixApiExtract/README.md)).

## Design standard

The build enforces most conventions: analyzers and `.editorconfig` style as errors, namespaces that follow folders, one
public type per file, central package versions and the banned-API list (inject `TimeProvider`; tests use
`FakeTimeProvider`). Nothing a build writes is committed except the cmdlet reference in `docs/reference/`. [ADR 0004](docs/adr/0004-conventions.md) lists the
conventions, and [docs/design/decisions.md](docs/design/decisions.md) and
[docs/design/architecture.md](docs/design/architecture.md) explain the design; a change that departs from them says
which section it changes and why.

Comments explain why the code is as it is, and a public `<summary>` is written in Microsoft's reference voice
("Gets …", "Represents …"). `DesignConformanceTests.Comments` fails a comment that cites an issue tracker id, a branch
or a date instead of the reason.

The Zabbix sources are AGPL-3.0 and Zabbix's documentation is proprietary. Cite a Zabbix file and line and say what the
code does in your own words; never paste Zabbix's code, comments, messages or documentation text. Names, numbers and
wire formats are facts and are quoted as they are. The `summary` sentences in `schema/overlay.json` are the project's
own words, and `tools/check-doc-wording.py` checks them against the API reference.

PowerShell help is generated: the build writes the MAML help and the reference pages from each cmdlet's doc comments
and its example script in `src/Zabbix.PowerShell*/Examples/`. Edit those, never the generated files, and commit the
reference pages the build rewrites under `docs/reference/`. The `about_`
topics in each module's `en-US/` folder are hand-written.

## Writing the documentation

The guides and the README follow the [Microsoft Writing Style Guide](https://learn.microsoft.com/style-guide/top-10-tips-style-voice)
and the [Azure SDK README template](https://github.com/Azure/azure-sdk/blob/main/docs/policies/README-TEMPLATE.md):
[ADR 0011](docs/adr/0011-documentation-structure.md) has the reasons.

- **Lead with what the reader can do.** A guide opens with what the piece is for, in a sentence or two, and the smallest sample
  that works.
- **Name each section for a task.** Write "Send a value", not "Sending" or "The sender". Use sentence case and no end punctuation.
- **Show, then explain.** Put the sample first, then only what the reader needs to use it. Prefer short sentences, lists, and tables.
- **Talk to the reader.** Write "you", start instructions with a verb, and use contractions. Write "you must" when the reader must
  do something. Cut "you can" and "there is" when the sentence works without them.
- **Keep the design out.** Zabbix's source files, how a class works inside, and why it was built that way belong in
  [docs/design/](docs/design/decisions.md). A guide says what happens, what to do, and what can go wrong.
- **Describe the product as it is.** No "now", "new", "previously", or process ids.
- **One task per sample.** Every C# sample must compile: `DocumentationTests` compiles them and checks every link and heading. A
  whole program goes in [samples/](samples/README.md), which the solution builds.

## Source file headers

Every source file (`.cs`, `.ps1`, `.psm1`, `.psd1`, `.sh`, `.py`, `.yml`, the MSBuild files and every hand-written
`.md`) starts with the MPL-2.0 header in the file's own comment syntax; `DesignConformanceTests.Headers` fails one
without it. A package's `README.md` (its nuget.org page) and the code of conduct carry none, and generated files carry
a "do not edit" line instead.

```csharp
// This Source Code Form is subject to the terms of the Mozilla Public
// License, v. 2.0. Copyright (c) 2026 schroeder247. SPDX-License-Identifier: MPL-2.0
```

Code copied from a third-party MPL-2.0-compatible source keeps its original notice, with your attribution beside it.

## The Zabbix name

Provided "as is", without warranty of any kind: use at your own risk.

This project is not affiliated with, endorsed by or sponsored by Zabbix LLC. Zabbix is a registered trademark of Zabbix LLC.

Every artefact that ships or is shown to users carries those two sentences word for word, and
`DesignConformanceTrademarkTests` fails one that lacks them or words them differently. A contribution never adds the
Zabbix logo, Zabbix's red, or wording that suggests an official link ("official", "certified", "approved",
"endorsed").

## Pull requests

- Keep a pull request to one change, and don't reformat code it doesn't touch.
- Add a line under `## [Unreleased]` in [CHANGELOG.md](CHANGELOG.md) for a user-visible change, and mark a breaking
  change as one.
- A Zabbix API quirk you find goes in a comment with the Zabbix source line that shows it.

## Releasing

Maintainer notes. A release is a `v<version>` tag of `main`; `.github/workflows/release.yml` builds, tests, packs and
publishes it. `tools/Test-ZbxReleaseVersion.ps1`, run by CI and by the release, checks that one version names
everything:

- The packages' version is `VersionPrefix` in `Directory.Build.props`, plus `-VersionSuffix` for a prerelease
  (`1.1.0-beta.1`). Each module manifest's `ModuleVersion` is `VersionPrefix`; a prerelease's manifests add
  `PSData.Prerelease`, the suffix without its dots (`beta1`).
- The tag is `v` and that version. A release tag is refused while a `VersionSuffix` is set.
- `CHANGELOG.md` has the version's heading dated (`## [1.1.0] - YYYY-MM-DD`); a tag without one is refused.

The release commit dates the changelog heading and runs `tools/promote-public-api.sh`, which moves every library's
`PublicAPI.Unshipped.txt` into `PublicAPI.Shipped.txt`. Tag that commit.

The packages go to GitHub Packages, and to nuget.org by
[Trusted Publishing](https://learn.microsoft.com/nuget/nuget-org/trusted-publishing) when the repository variable
`NUGET_USER` is set. The modules go to the PowerShell Gallery when the secret `PSGALLERY_API_KEY` is set,
`Community.Zabbix` first. A feed that is not configured is skipped without failing the run, so check the log.

Once, before the first release: make the repository public; turn on private vulnerability reporting; enable GitHub
Pages with GitHub Actions as its source, set the variable `DEPLOY_PAGES` to `true` and run `docs.yml`; add a nuget.org
trusted-publishing policy for `schroeder247/community-zabbix` and `release.yml`; and create a Gallery key scoped to
`Community.Zabbix*` with an expiry.

After each release: set `ZabbixPackageValidationBaseline` in `Directory.Build.props` to the version just published,
add a new `## [Unreleased]` heading, and move the `--version` pins in `docs/INSTALL.md`.

## Code of conduct

Be respectful. The full text, and how to report a problem, is [CODE_OF_CONDUCT.md](CODE_OF_CONDUCT.md).
