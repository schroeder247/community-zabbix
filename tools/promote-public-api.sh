#!/usr/bin/env bash
# This Source Code Form is subject to the terms of the Mozilla Public
# License, v. 2.0. Copyright (c) 2026 schroeder247. SPDX-License-Identifier: MPL-2.0
#
# Moves every library's declared public API from PublicAPI.Unshipped.txt into PublicAPI.Shipped.txt: the step of the
# release commit that removes VersionSuffix (CONTRIBUTING.md, "Releasing"). After it, Unshipped holds only
# `#nullable enable`, and what a later change adds or removes shows up there against the shipped baseline.
#
# The merge is PublicApiAnalyzers' own file rule: an Unshipped line `*REMOVED*<symbol>` takes <symbol> out of Shipped,
# every other line is added to it, and Shipped is written as `#nullable enable` followed by its symbols in ordinal
# order without duplicates. Nothing else changes, so the next build reports no RS0016/RS0017 when the baseline was
# current before (run tools/refresh-public-api.sh first if it may not be).
#
# Run it only for a release: DesignConformanceTests.PublicApi fails a tree whose Shipped files are populated while a
# VersionSuffix is still set.
#
# Usage: tools/promote-public-api.sh [project-name ...]
#   No arguments: every project under src/ that has a PublicAPI.Unshipped.txt.
set -euo pipefail

root="$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd)"

projects=()
if [ "$#" -gt 0 ]; then
  projects=("$@")
else
  for unshipped in "$root"/src/*/PublicAPI.Unshipped.txt; do
    projects+=("$(basename "$(dirname "$unshipped")")")
  done
fi

for project in "${projects[@]}"; do
  directory="$root/src/$project"
  unshipped="$directory/PublicAPI.Unshipped.txt"
  shipped="$directory/PublicAPI.Shipped.txt"
  if [ ! -f "$unshipped" ] || [ ! -f "$shipped" ]; then
    echo "promote-public-api: '$directory' has no PublicAPI.Shipped.txt and PublicAPI.Unshipped.txt" >&2
    exit 1
  fi
  merged="$(mktemp)"
  {
    # Shipped's symbols, less what Unshipped marks removed, plus what Unshipped adds.
    grep -vxF '#nullable enable' "$shipped" | grep -v '^[[:space:]]*$' || true
    grep -vxF '#nullable enable' "$unshipped" | grep -v '^[[:space:]]*$' | grep -v '^\*REMOVED\*' || true
  } | grep -vxF -f <(sed -n 's/^\*REMOVED\*//p' "$unshipped") | LC_ALL=C sort -u >"$merged" || true
  { echo '#nullable enable'; cat "$merged"; } >"$shipped"
  echo '#nullable enable' >"$unshipped"
  rm -f "$merged"
  echo "promote-public-api: $project: $(wc -l <"$shipped" | tr -d ' ') line(s) in PublicAPI.Shipped.txt"
done
