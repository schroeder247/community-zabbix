#!/usr/bin/env bash
# This Source Code Form is subject to the terms of the Mozilla Public
# License, v. 2.0. Copyright (c) 2026 schroeder247. SPDX-License-Identifier: MPL-2.0
#
# The live matrix, whole: every live suite against every server lab.sh reports answering, then the sweep, then one
# table of results per version.
#
#   live.sh [--no-build] [--versions 7.0,7.4,8.0] [csharp|pester|examples|sweep ...]   (default: all four)
#
#   csharp   Zabbix.Tests' live classes (Live*: drift, sender, agent, proxy, streams, server facts, defect pins) and the
#            EF store's tests on the lab PostgreSQL
#   pester   Zabbix.PowerShell.Pester's Live.Tests.ps1, per server
#   examples tools/Test-ZbxHelpExamples.ps1 per server: every Get-Help example of the three modules, the help scripts
#            run whole and each generated example on its own
#   sweep    Invoke-LabLeakSweep for zbx-it- (and zbx-doc-ex-, under which every examples run names its
#            objects) on every server; anything found is a failure
#
# The environment is lab.sh's contract (lab.sh env), never hand-set; --versions narrows ZABBIX_TEST_APIS to those labels.
# Results (CTRF JSON, Pester NUnit XML, examples and sweep JSON) land in $LIVE_RESULTS (default /tmp/zbxlab/live-results).
# Exit status: 0 only when nothing failed and the sweep found nothing. LiveDefectTests and the Pester cases named after a
# defect are regression tests: each fails if the defect it names returns.
set -euo pipefail

here="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
root="$(cd "$here/../.." && pwd)"
dotnet="${DOTNET:-$(command -v dotnet || echo "$HOME/.dotnet/dotnet")}"
[[ -x "$dotnet" ]] || dotnet="$HOME/.dotnet/dotnet"
configuration="${LIVE_CONFIGURATION:-Debug}"
results="${LIVE_RESULTS:-/tmp/zbxlab/live-results}"
build=1 versions="" suites=()

while [[ $# -gt 0 ]]; do
  case "$1" in
    --no-build) build=0 ;;
    --versions) versions="$2"; shift ;;
    csharp | pester | examples | sweep) suites+=("$1") ;;
    all) suites=(csharp pester examples sweep) ;;
    *) echo "live.sh: unknown argument '$1' (see the header)" >&2; exit 2 ;;
  esac
  shift
done
[[ ${#suites[@]} -gt 0 ]] || suites=(csharp pester examples sweep)
has() { [[ " ${suites[*]} " == *" $1 "* ]]; }

eval "$("$here/lab.sh" env)"
if [[ -n "$versions" ]]; then
  keep() { tr ';' '\n' <<<"$1" | grep -E "^($(tr ',' '|' <<<"${versions//./\\.}"))=" | paste -sd ';' - || true; }
  ZABBIX_TEST_APIS="$(keep "$ZABBIX_TEST_APIS")" ZABBIX_TEST_TRAPPERS="$(keep "$ZABBIX_TEST_TRAPPERS")"
  export ZABBIX_TEST_APIS ZABBIX_TEST_TRAPPERS
fi
[[ -n "${ZABBIX_TEST_APIS:-}" ]] || { echo "live.sh: no server answers; run $here/lab.sh start" >&2; exit 2; }
labels="$(tr ';' '\n' <<<"$ZABBIX_TEST_APIS" | cut -d= -f1 | paste -sd ' ' -)"
echo "live.sh: servers $labels; results in $results" >&2

rm -rf "$results"
mkdir -p "$results"
status=0

if [[ $build -eq 1 ]]; then
  "$dotnet" build "$root/tests/Zabbix.Tests" -c "$configuration" -warnaserror -v q -nologo >&2
  "$dotnet" build "$root/src/Zabbix.PowerShell.Agent" -c "$configuration" -warnaserror -v q -nologo >&2
  "$dotnet" build "$root/src/Zabbix.PowerShell.Proxy" -c "$configuration" -warnaserror -v q -nologo >&2
fi

if has csharp; then
  echo "== csharp" >&2
  # xunit v3: filters of one kind are ORed.
  "$dotnet" "$root/tests/Zabbix.Tests/bin/$configuration/net10.0/Zabbix.Tests.dll" \
    --filter-class 'Community.Zabbix.Tests.Live*' --filter-class 'Community.Zabbix.Tests.EfStor*' \
    --results-directory "$results" --report-xunit-ctrf --report-xunit-ctrf-filename csharp.ctrf.json \
    --progress off >"$results/csharp.log" 2>&1 || status=1
  grep -E '^(failed|  total|  failed|  succeeded|  skipped)' "$results/csharp.log" >&2 || true
  # A filter that matches nothing passes, and would hide that no test ran.
  if ! grep -qE '^  total: +[1-9]' "$results/csharp.log"; then
    echo "live.sh: the csharp suite ran no tests; check the class filters above" >&2
    status=1
  fi
fi

if has pester; then
  echo "== pester" >&2
  pwsh -NoProfile -Command "
    \$c = New-PesterConfiguration
    \$c.Run.Path = '$root/tests/Zabbix.PowerShell.Pester'
    \$c.Run.PassThru = \$true
    \$c.Output.Verbosity = 'Detailed'
    \$c.TestResult.Enabled = \$true
    \$c.TestResult.OutputPath = '$results/pester.xml'
    \$r = Invoke-Pester -Configuration \$c
    \$r.Tests | ForEach-Object { [pscustomobject]@{ name = \$_.ExpandedPath; status = \$_.Result.ToLowerInvariant(); message = \$_.ErrorRecord.Exception.Message -join ' ' } } |
      ConvertTo-Json -Depth 3 -AsArray | Set-Content '$results/pester.json'
    exit \$r.FailedCount" >"$results/pester.log" 2>&1 || status=1
fi

if has examples; then
  echo "== examples" >&2
  # One run per server; a token given inline in the contract is handed over in a file of its own (the script reads one).
  pwsh -NoProfile -Command "
    Import-Module '$root/tests/lab/Lab.psm1'
    \$failed = 0
    foreach (\$s in Get-LabServer) {
      \$tokenFile = \$s.TokenFile
      if (-not \$tokenFile) { \$tokenFile = '$results/token-' + \$s.Id; Set-Content -Path \$tokenFile -Value (Get-LabServerToken -Server \$s) }
      \$trapper = \$s.ServerHost + ':' + \$s.ServerPort
      \$label = \$s.Label
      \$LASTEXITCODE = 0   # the script sets it only when it fails (exit 1)
      & '$root/tools/Test-ZbxHelpExamples.ps1' -Uri \$s.Url -TokenFile \$tokenFile -Trapper \$trapper -Configuration '$configuration' -ResultPath ('$results/examples-' + \$label + '.json')
      if (\$LASTEXITCODE -ne 0) { \$failed++ }
      if (-not \$s.TokenFile) { Remove-Item \$tokenFile }
    }
    exit \$failed" >"$results/examples.log" 2>&1 || status=1
fi

if has sweep; then
  echo "== sweep" >&2
  pwsh -NoProfile -Command "
    Import-Module '$root/tests/lab/Lab.psm1'
    \$found = foreach (\$s in Get-LabServer) {
      foreach (\$prefix in 'zbx-it-', 'zbx-doc-ex-') { Invoke-LabLeakSweep -ServerId \$s.Label -Prefix \$prefix }
    }
    ConvertTo-Json -InputObject @(\$found) -Depth 3 | Set-Content '$results/sweep.json'
    exit @(\$found).Count" >"$results/sweep.log" 2>&1 || status=1
fi

# One table: per version (the theory argument, the Describe's label), per suite.
python3 - "$results" "$labels" <<'PY'
import json, os, re, sys
root, labels = sys.argv[1], sys.argv[2].split()
rows = {}
failures = []
def add(version, suite, status, name="", message=""):
    r = rows.setdefault((version, suite), {"passed": 0, "failed": 0, "skipped": 0})
    kind = "passed" if status in ("passed", "pass") else "failed" if status in ("failed", "fail", "error") else "skipped"
    r[kind] += 1
    if kind == "failed": failures.append((version, suite, name, " ".join(str(message or "").split())))
def version_of(name):
    m = re.search(r'version: "([0-9.]+)"', name) or re.search(r'against ([0-9]+\.[0-9]+) ', name)
    return m.group(1) if m else "-"
p = os.path.join(root, "csharp.ctrf.json")
if os.path.exists(p):
    for t in json.load(open(p))["results"]["tests"]:
        add(version_of(t["name"]), "csharp", t["status"], t["name"], t.get("message"))
p = os.path.join(root, "pester.json")
if os.path.exists(p):
    for t in json.load(open(p)) or []:
        add(version_of(t["name"]), "pester", t["status"], t["name"], t.get("message"))
for v in labels:
    p = os.path.join(root, f"examples-{v}.json")
    if os.path.exists(p):
        for t in json.load(open(p)) or []:
            # Excluded (with the overlay's reason) and NotInVersion rows are skips.
            status = t.get("Status") or ("Passed" if t["Passed"] else "Failed")
            name = " ".join(str(t[k]) for k in ("Kind", "Command", "Cmdlet", "File", "Name", "Example", "Index") if t.get(k) is not None)
            add(v, "examples", {"Passed": "passed", "Failed": "failed"}.get(status, "skipped"), name, t.get("Error"))
p = os.path.join(root, "sweep.json")
if os.path.exists(p):
    found = json.load(open(p)) or []
    for v in labels:
        n = sum(1 for f in found if str(f.get("Server")) == v)
        rows[(v, "sweep")] = {"passed": 0 if n else 1, "failed": n, "skipped": 0}
print(f"{'version':8} {'suite':8} {'passed':>7} {'failed':>7} {'skipped':>8}")
for (v, s) in sorted(rows):
    r = rows[(v, s)]
    print(f"{v:8} {s:8} {r['passed']:>7} {r['failed']:>7} {r['skipped']:>8}")
# What failed, in the log a reader already has: the results files are one more download away.
for (v, s, name, message) in failures:
    print(f"FAILED {v} {s}: {name}\n    {message[:600]}")
PY

exit $status
