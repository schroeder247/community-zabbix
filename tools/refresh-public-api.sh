#!/usr/bin/env bash
# This Source Code Form is subject to the terms of the Mozilla Public
# License, v. 2.0. Copyright (c) 2026 schroeder247. SPDX-License-Identifier: MPL-2.0
#
# Refreshes every library's PublicAPI.Unshipped.txt after an intentional public-API change.
# `dotnet format analyzers` runs the PublicApiAnalyzers code fix (AddAllMissingApiFix / RemoveUnnecessaryApiFix) and
# writes the new symbol lines straight into the project's PublicAPI.Unshipped.txt — the only supported way to grow
# the file (hand-editing a `->` return-type-encoded line is how the baseline drifts from what the compiler sees).
#
# --diagnostics RS0016 RS0017 is REQUIRED, not optional: omitting it applies every analyzer's registered code fix the
# workspace can find, not just PublicApiAnalyzers' — it silently adds empty-argument
# `[RequiresDynamicCode]`/`[RequiresUnreferencedCode]` attributes (IL3050/IL2026's own fixer) to unrelated methods in
# Zabbix and Zabbix.Proxy, which do not compile (both attributes require a message). `--diagnostics RS0016` alone
# (no RS0017) does nothing ("Formatted 0 of N files") — RS0016's add-fix and RS0017's remove-fix are requested
# together or dotnet-format skips the file. RS0026 (overload ambiguity) and RS0027
# (optional-parameter overload must have the most parameters) have no code fix: they are fixed in source, the generated
# clients included, except for the optional CancellationToken the libraries suppress them for (see Directory.Build.targets).
#
# PublicAPI.Shipped.txt is never touched by this script: the shipped file moves from empty to populated only at the
# first release, through tools/promote-public-api.sh, in that release's own commit.
#
# One pass does not always converge: on a record struct's compiler-synthesized members (operator ==/!=,
# Equals, GetHashCode, Deconstruct) reported at the SAME diagnostic span as the record header — dotnet-format's Fix
# All batches one code action per unique (file, span) and applied only the first, leaving the rest missing after a
# single run. The loop below reruns until a build of the project reports no RS0016/RS0017, capped so a genuine
# unfixable diagnostic (RS0026/RS0027, which have no code fix) does not spin forever. A build that then still fails, for
# any other reason (a compile error, another analyzer), fails the script with those errors: a refresh is done only when
# the project builds.
#
# Portable: macOS (BSD userland) and Linux (GNU) alike, so the script edits a file only through a temporary file it
# copies back (keeping the file's own mode), never sed -i, whose argument differs between the two. Every refreshed
# PublicAPI.Unshipped.txt is left sorted as the analyzers' own code fix sorts it (DeclarePublicApiFix's
# IgnoreCaseWhenPossibleComparer: ordinal ignoring case, then ordinal), after `#nullable enable`, so the fallbacks
# below, which append, leave the order the code fix would have.
#
# --include-generated is required: Zabbix.Api and Zabbix (ZabbixApiGenerator/ZabbixCmdletGenerator output, thousands
# of fields) would otherwise report ~20,000 RS0016 errors on every build and never converge: without the flag,
# dotnet-format's MSBuildWorkspace loads only the SDK's own non-generated documents, so it never sees a source
# generator's trees at all.
#
# Usage: tools/refresh-public-api.sh [project-name ...]
#   No arguments: every library under the public-API rule, which is every project under src/ with a
#   PublicAPI.Unshipped.txt (Directory.Build.targets declares the files by their presence, one pair per library). The
#   list below orders them so a library is refreshed before the libraries that reference it (a referenced library's
#   RS0016 would otherwise fail the build of the one being refreshed); the script refuses to run when the list and
#   the files on disk disagree.
set -euo pipefail

root="$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd)"
dotnet="${DOTNET_HOST_PATH:-dotnet}"

libraries=(
  Zabbix.Abstractions
  Zabbix.Api
  Zabbix.Sdk
  Zabbix.Wire
  Zabbix.Agent
  Zabbix.Proxy
  Zabbix.Agent.Storage.EntityFrameworkCore
  Zabbix.Agent.Plugins
  Zabbix.PowerShell
  Zabbix.PowerShell.Agent
  Zabbix.PowerShell.Proxy
)

tracked=()
for unshipped in "$root"/src/*/PublicAPI.Unshipped.txt; do
  tracked+=("$(basename "$(dirname "$unshipped")")")
done
if [ "$(printf '%s\n' "${libraries[@]}" | sort)" != "$(printf '%s\n' "${tracked[@]}" | sort)" ]; then
  echo "refresh-public-api: the ordered list does not name every src/*/PublicAPI.Unshipped.txt project:" >&2
  diff <(printf '%s\n' "${libraries[@]}" | sort) <(printf '%s\n' "${tracked[@]}" | sort) >&2 || true
  exit 1
fi

if [ "$#" -gt 0 ]; then
  libraries=("$@")
fi

# Builds the project; prints the build's error lines and returns how many RS0016/RS0017 errors it reported via $remaining,
# and every other error via $others.
check() {
  local output
  output="$("$dotnet" build "$1" -clp:ErrorsOnly -v:q 2>&1)" && built=0 || built=$?
  remaining="$(grep -Ec 'error RS0016|error RS0017' <<<"$output" || true)"
  others="$(grep -E ': error ' <<<"$output" | grep -Ev 'error RS0016|error RS0017' | sed -E 's/ \[[^]]*\]$//' | sort -u || true)"
  errors="$output"
}

# Sorts a PublicAPI.Unshipped.txt as the analyzers' code fix does (ordinal ignoring case, then ordinal), its
# `#nullable enable` first and no blank line; perl, which both macOS and Linux have, so the order is the same on both.
sort_api() {
  local file="$1" sorted
  sorted="$(mktemp)"
  {
    if head -n 1 "$file" | grep -qxF '#nullable enable'; then echo '#nullable enable'; fi
    grep -vxF '#nullable enable' "$file" | grep -v '^[[:space:]]*$' \
      | perl -e 'my @lines = <STDIN>; print sort { uc($a) cmp uc($b) or $a cmp $b } @lines'
  } >"$sorted"
  cat "$sorted" >"$file"
  rm -f "$sorted"
}

# Deletes the lines numbered on standard input from a file, through a temporary file (no sed -i); none named, none deleted.
delete_lines() {
  local file="$1" numbers kept
  numbers="$(mktemp)"
  cat >"$numbers"
  if [ -s "$numbers" ]; then
    kept="$(mktemp)"
    awk 'NR == FNR { drop[$1]; next } !(FNR in drop)' "$numbers" "$file" >"$kept"
    cat "$kept" >"$file"
    rm -f "$kept"
  fi
  rm -f "$numbers"
}

# A build with no RS0016/RS0017 left that still fails, for any other reason, is not a refreshed surface.
done_or_fail() {
  if [ -n "$others" ] || [ "$built" -ne 0 ]; then
    echo "refresh-public-api: $library has no RS0016/RS0017 left but its build fails:" >&2
    if [ -n "$others" ]; then echo "$others" >&2; else echo "$errors" >&2; fi
    exit 1
  fi
}

for library in "${libraries[@]}"; do
  project="$root/src/$library/$library.csproj"
  if [ ! -f "$project" ]; then
    echo "refresh-public-api: no such project '$project'" >&2
    exit 1
  fi
  echo "refresh-public-api: $library"
  unshipped="$(dirname "$project")/PublicAPI.Unshipped.txt"
  for pass in 1 2 3 4 5; do
    "$dotnet" format analyzers "$project" --no-restore --include-generated --diagnostics RS0016 RS0017 -v minimal
    sort_api "$unshipped"
    check "$project"
    if [ "$remaining" -eq 0 ]; then
      done_or_fail
      break
    fi
    if [ "$pass" -eq 5 ]; then
      # What the code fix does not converge on, done as the analyzer names it, and only in this project's own file (a
      # referenced project's lines are that project's to refresh): first the RS0017 lines are removed, then the RS0016
      # symbols are added, the file sorted after each, and the project built again, up to three times.
      #
      # RS0017 (a declared symbol the compilation no longer has) that dotnet-format reports none of: after a
      # regenerated model drops members from records that stay, the build reports RS0017 while
      # `dotnet format analyzers --diagnostics RS0016 RS0017` finds 0 diagnostics to fix on every pass, even after a
      # clean rebuild; and a changed extension-block member, whose lines the remove fix and the add fix trade pass after
      # pass. The lines are removed exactly as the compiler names them (file and line of each RS0017), never guessed.
      #
      # RS0016 that the code fix cannot converge on: a positional record's synthesized members (Equals, GetHashCode,
      # ToString, ==, !=, <Clone>$, Deconstruct) are all reported at the record header's one span, and dotnet-format
      # merges their insertions into conflicting edits of the same lines, so each pass adds some and drops another
      # (the seven members and Deconstruct trade places pass after pass). The lines are then appended exactly
      # as the analyzer names them in its own message ('Symbol '<the line>' is not part of the declared public API',
      # the string the code fix writes), never composed by hand, and only for this project's own sources.
      own="$(cd "$(dirname "$project")" && pwd)/PublicAPI.Unshipped.txt"
      dir="$(cd "$(dirname "$project")" && pwd)/"
      for fallback in 1 2 3; do
        if grep -q 'error RS0017' <<<"$errors"; then
          grep -oE '[^ ]*PublicAPI\.Unshipped\.txt\([0-9]+,[0-9]+\): error RS0017' <<<"$errors" \
            | sed -E 's/\(([0-9]+),[0-9]+\).*/ \1/' | sort -u \
            | while read -r file line; do
                if [ "$(cd "$(dirname "$file")" && pwd)/$(basename "$file")" = "$own" ]; then echo "$line"; fi
              done \
            | sort -n -u | delete_lines "$own"
          check "$project"
        fi
        if ! grep -q 'error RS0017' <<<"$errors" && grep -q 'error RS0016' <<<"$errors"; then
          grep -E 'error RS0016' <<<"$errors" \
            | grep -F "$dir" \
            | sed -nE "s/.*error RS0016: Symbol '(.*)' is not part of the declared public API.*/\1/p" \
            | sort -u >> "$own"
          sort_api "$own"
          check "$project"
        fi
        if [ "$remaining" -eq 0 ]; then
          done_or_fail
          break 2
        fi
        echo "refresh-public-api: $library: fallback $fallback left $remaining RS0016/RS0017 error(s)"
      done
      echo "refresh-public-api: $library still has $remaining RS0016/RS0017 error(s) after 5 passes" >&2
      exit 1
    fi
  done
done
