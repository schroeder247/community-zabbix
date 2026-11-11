<!--
This Source Code Form is subject to the terms of the Mozilla Public
License, v. 2.0. Copyright (c) 2026 schroeder247. SPDX-License-Identifier: MPL-2.0
-->
# Building and testing

How to build the repository, run the test suites, and run the live tests against real Zabbix servers. Every gate below
is also run by CI (`.github/workflows/ci.yml`).

## Build and the gates

The .NET 10 SDK is pinned by `global.json`.

```bash
dotnet build Zabbix.slnx -warnaserror                        # every analyzer rule is an error, CS1591 included
dotnet format Zabbix.slnx --verify-no-changes                # the format gate
dotnet test --solution Zabbix.slnx                           # both suites, with their executed-test floors
dotnet pack Zabbix.slnx -c Release -warnaserror              # package validation
dotnet pack Zabbix.slnx -c Release -warnaserror -m:1 -p:ZabbixSbom=true  # as CI packs: with each package's SPDX SBOM
```

- **The clean-tree gate.** The JSON serializer context, the module manifests' export lists, the MAML help and the
  types and format views are written under `obj/` and `bin/`; the cmdlet reference pages are written to the committed
  `docs/reference/` ([ADR 0004](adr/0004-conventions.md)). After a Debug or Release build and the format check,
  `git status` must be empty, so a help change must commit its regenerated reference pages; CI fails the build and names
  the files otherwise.
- **The public-API baseline.** Each library tracks its surface in `PublicAPI.Shipped.txt`/`PublicAPI.Unshipped.txt`
  (RS0016/RS0017 are errors). After an intentional change run `tools/refresh-public-api.sh [project ...]` and review
  the diff. Do not run `dotnet format analyzers` without `--diagnostics RS0016 RS0017`: it would also apply other
  analyzers' code fixes. `PublicAPI.Shipped.txt` stays empty until the first release; the release commit runs
  `tools/promote-public-api.sh`, which moves every library's Unshipped surface into Shipped (CONTRIBUTING.md,
  "Releasing").
- **The Native AOT composition.** The trim and AOT analyzers see each library alone. `tests/probes/Zabbix.AotSmoke`
  composes the API client, the agent with a trapper listener and the sender in one console; CI's `aot` job publishes
  it with `dotnet publish tests/probes/Zabbix.AotSmoke/Zabbix.AotSmoke.csproj -c Release -warnaserror` (any IL2xxx or
  IL3xxx warning fails) and runs the binary, which must print `aot smoke: ok`.
- **The documentation site.** `docfx docfx.json --warningsAsErrors` builds the API reference from the Release build of
  the libraries and the cmdlet reference from the Release build of the modules; build those first, as
  `.github/workflows/docs.yml` does.

## The suites

| Project | What it covers | Floor (`--minimum-expected-tests` in the `.csproj`) |
| --- | --- | --- |
| `tests/Zabbix.Tests` | The SDK: the API client and model, the tree, the wire, the hosts and listeners, the stores, telemetry, and the design conformance tests (layout, public API, the dependency policy, enforcement, telemetry, source comments, licence headers) | `tests/Zabbix.Tests/Zabbix.Tests.csproj` |
| `tests/Zabbix.PowerShell.Tests` | The three modules, in-process: binding, the generated cmdlets, help, manifests, format views | `tests/Zabbix.PowerShell.Tests/Zabbix.PowerShell.Tests.csproj` |
| `tests/Zabbix.PowerShell.Pester` | `Live.Tests.ps1`: the modules against a live server (run by `live.sh`) | — |
| `tests/probes/Zabbix.Analyzers.Probe` | Not a suite and not in `Zabbix.slnx`: a library-shaped project whose every marked line breaks a rule the build claims to enforce. `DesignConformanceTests` builds it, expects the build to fail, and holds each `// expect: ID` mark to the error reported at that line | — |

Each test project pins `--minimum-expected-tests` in its `.csproj` to the number of tests it executes (skipped tests
not counted); a run that executes fewer fails. `Zabbix.Tests` pins one number per system, because each skips the platform
calls of the others. `dotnet test` applies the floor. When a test project is run as a DLL,
pass the floor yourself, read from the same `.csproj` so the number lives in one place:

```bash
floor=$(grep "IsOSPlatform('Linux')" tests/Zabbix.Tests/Zabbix.Tests.csproj | grep -oP 'minimum-expected-tests \K\d+')
dotnet exec tests/Zabbix.Tests/bin/Debug/net10.0/Zabbix.Tests.dll --minimum-expected-tests "$floor"
```

On Linux the `Zabbix.Tests` floor counts the EF store's PostgreSQL tests: set `ZABBIX_TEST_POSTGRES` (below) to the lab's
PostgreSQL (`tests/lab/zbxtest-postgres.yaml`, started by `tests/lab/lab.sh start pg`) before running it. Without it
those 14 tests skip and the run fails its floor; CI's `build` job starts its own PostgreSQL for them on Linux. The
Windows and macOS floors leave them out, as those legs have no PostgreSQL.

When you add tests, raise the floor in the same change. Both suites run under
`Microsoft.Testing.Extensions.HangDump`: a run in which no test finishes for 10 minutes writes a dump, fails and names
the tests still running.

Test code is under its own banned-symbol list, `tests/BannedSymbols.txt` (RS0030 is an error): no `Thread.Sleep`, no
`Task.Delay(int)` polling loop and no local-clock read. A test waits on a signal the code under test gives: a log
record through `FakeLogCollector.GetLogsAsync`, a `TaskCompletionSource` a fake completes, a channel. A site with no
signal to wait on carries a `#pragma warning disable RS0030` with the reason beside it.

CI's `build` job runs on Linux, Windows and macOS. Windows and macOS build the solution and run both suites without
PostgreSQL, on the paths only they reach: the sockets, SChannel, the bundled items' platform calls and the modules under
their pwsh. The gates that read or package the tree (shellcheck, the format check, the clean-tree gate, the pack, the
versions and the module install) run on Linux.

Apart from that PostgreSQL, the default run touches no external system. Every live test reads its servers from the
environment below; when a variable is unset the test is skipped, with the remedy in the skip message.

## Property tests

`tests/Zabbix.Tests/PropertyTests.cs` states the laws between the parsers and their formatters as properties over
generated inputs, with [CsCheck](https://github.com/AnthonyLloyd/CsCheck): an item key made from its parts reads back
as the same parts; a frame reads back as its payload, compressed or not, whole or split across segments, and every
proper prefix asks for more; a `ServerActive` list formats and reads back as itself; an update interval is due strictly
after now and moves by whole weeks with the clock; an exact instant keeps a date to the tick and orders as
`(seconds, nanoseconds)`; a record written with `ZabbixJson.Options` reads back equal; a PSK reads back as its bytes.
The same class fuzzes the frame reader: mutated frames and random bytes are answered with a payload, a request for
more, or an `InvalidDataException`, and nothing else. A failing property is shrunk to its smallest input and printed
with a seed; `CsCheck_Seed=<seed>` in the environment replays it.

## PostgreSQL for the EF store

`ZABBIX_TEST_POSTGRES` is a connection string to a PostgreSQL database the tests own (never a Zabbix database). When it
is unset, the `ZabbixEntityFrameworkStore<T>` tests run on SQLite only, and the PostgreSQL ones skip (so, on Linux, the
`Zabbix.Tests` floor is not met). `tests/lab/lab.sh start pg` starts one on port 55432
(`tests/lab/zbxtest-postgres.yaml`):

```bash
export ZABBIX_TEST_POSTGRES='Host=127.0.0.1;Port=55432;Database=zbxtest;Username=zbxtest;Password=zbxtest;Timeout=3'
```

## The lab

`tests/lab/lab.sh` creates throwaway Zabbix servers with `podman kube play`: 7.0.31 (API port 18070, trapper 18051),
7.4.15 (18074, 18053) and 8.0 (18080, 18052), plus the PostgreSQL above, and makes an Admin API token for each. The
8.0 server is not a release image: Docker Hub has no `8.0.0rc1` image, so `tests/lab/zabbix-8.0.yaml` pins Zabbix's
`alpine-trunk` images by their index digest (trunk moves daily; the digest keeps the lab reproducible): a master build
from after the `8.0.0rc1` tag, with rc1's API. Its server reports `zabbix_server (Zabbix) 8.0.0rc2` (revision
`0f4cbcd`, 1 October 2026) and its API `8.0.0`.

```bash
tests/lab/lab.sh start [7.0|7.4|8.0|pg|all ...]   # create what is missing and wait until it answers
eval "$(tests/lab/lab.sh env)"                    # export the environment contract for what answers
tests/lab/lab.sh status                           # what answers, and what the script created
tests/lab/lab.sh stop                             # remove only the pods the script created
tests/lab/live.sh                                 # every live suite against every server, then the leak sweep
```

`tests/lab/Lab.psm1` is the lab from PowerShell, for the Pester live suite and `live.sh`: `Get-LabServer` reads the
environment contract below, `Get-LabServerToken` a server's token, and `Invoke-LabLeakSweep -ServerId 7.0 -Prefix
zbx-it-` finds (and, without `-ReportOnly`, deletes) every object whose name starts with the prefix, over
raw JSON-RPC, so it works however broken the module under test is.

A server whose port already answers is reused and never removed, and the live suites create and delete objects on it. You
must check that nothing else on your machine answers on the lab's ports. If something does, such as another project's Zabbix, set
`ZBX_LAB_TARGETS` to free ports:

```bash
export ZBX_LAB_TARGETS='7.0|zabbix-7.0.yaml|zbxlab-70|28070|28051;7.4|zabbix-7.4.yaml|zbxlab-74|28074|28053;8.0|zabbix-8.0.yaml|zbxlab-80|28080|28052'
```

On macOS, install Podman and start its VM (`podman machine init --memory 6144`, then `podman machine start`), and set
`ZBX_LAB_STATE` to a folder under `/private` or your home folder, which the VM can read: its `/tmp` isn't the Mac's. Run one
version at a time with `--versions`, as CI does.

A server the script creates gets every table's row in
the `ids` table at its start (the frontend creates one on the first insert into a table, and the live suites' parallel
first inserts would collide on it, answering "Database error occurred."). The state (tokens, the lab's PKI, the list of pods
the script created) lives in `$ZBX_LAB_STATE`, `/tmp/zbxlab` by default.

`live.sh [--no-build] [--versions 7.0,7.4,8.0] [csharp|pester|examples|sweep ...]` runs the C# live classes and the
EF store tests on the lab's PostgreSQL, the Pester live suite, every Get-Help example (below) and a sweep for objects a
live test or an example left behind; results (CTRF JSON, NUnit XML, examples and sweep JSON) land in `$LIVE_RESULTS`. It exits 0 only when nothing failed and the
sweep found nothing. CI's `live` job runs it nightly and on demand.

## The environment contract

`lab.sh env` prints these variables; `tests/Zabbix.Tests/LiveMatrix.cs` reads them.

| Variable | Form | Meaning |
| --- | --- | --- |
| `ZABBIX_TEST_APIS` | `label=url\|token;…` | The servers, labelled `7.0`, `7.4`, `8.0`. A token starting with `@` is read from that file, so the secret is never in the environment |
| `ZABBIX_TEST_TRAPPERS` | `label=host:port;…` | Each server's trapper |
| `ZABBIX_TEST_FEATURES` | `label=cert,connector;…` | What a server was started with beyond the image defaults |
| `ZABBIX_TEST_PKI` | folder | The lab CA (`ca.crt`, `client.crt`, `client.key`) |
| `ZABBIX_TEST_HOST_FROM_SERVER` | host name | How a server inside its pod reaches a listener on this machine (`host.containers.internal` under rootless podman; the listener must bind `0.0.0.0`) |
| `ZABBIX_TEST_POSTGRES` | connection string | The EF store's database |

Every object a live test creates is named with the prefix `zbx-it-`, and the sweep deletes by it.

## The API model and its pinned sources

`schema/zabbix-api.json`, the description the generator builds the API model from, is extracted from the Zabbix sources
`tools/zabbix-sources.lock` pins (a tag and its commit per version, from `https://github.com/zabbix/zabbix`, the
official mirror of git.zabbix.com):

```bash
tools/regenerate-schema.sh            # fetch the pinned sources, extract, merge, verify; review git diff schema/
tools/regenerate-schema.sh --check    # the same, failing when the model is not what the pinned sources give
```

`tools/fetch-zabbix-sources.sh` makes a shallow, sparse clone of each pinned commit into `.zabbix-sources/<version>`
(ignored by git; about 15 MB a version: `ui/include/*.php`, `ui/include/classes/` and `create/src/schema.tmpl`) and
refuses a commit or a tag that does not match the lock. The per-version facts the extractor writes
(`zabbix-api-<version>.json`) land there too; they are intermediates. CI's `schema-drift` workflow
(`.github/workflows/schema-drift.yml`, weekly, on demand, and on every pull request that changes `schema/`, the
extractor or the lock) runs the check, so a pin that no longer gives the
committed model shows up as a failed run with the diff. It needs only `git` and `python3`.

## Help examples

The help scripts in `src/Zabbix.PowerShell*/Examples/*.ps1` (one per hand-written command, and one for each major
generated command) reach `Get-Help`; the generator writes every other generated command's examples. `HelpTests` check
that every example binds and leaves no mandatory parameter unbound, and `HelpQualityTests` that the text keeps the
cmdlet-help voice and no example carries a placeholder. `tools/Test-ZbxHelpExamples.ps1` runs every example against a
live server and deletes what each created: each help script whole, from the fixtures, and each generated example on
its own, from the fixtures and with the example that creates the object it works on as its setup (the module build
writes the generated examples to `src/Zabbix.PowerShell/obj/<configuration>/net10.0/examples/generated-examples.json`).
A generated example the server cannot run is marked in `schema/overlay.json` (`objects.<object>.help.notRun`, by the
example's title or its command's verb) with the reason, and reported as excluded, and what an object's examples need
of the lab beyond the objects they name is its `liveSetup` (a host dashboard needs its template linked to the host); a
command the server's version does not have is reported as such. `live.sh examples` runs it against every lab server, so CI's nightly `live` job runs it
too.
