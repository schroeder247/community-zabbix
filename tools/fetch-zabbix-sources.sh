#!/usr/bin/env bash
# This Source Code Form is subject to the terms of the Mozilla Public
# License, v. 2.0. Copyright (c) 2026 schroeder247. SPDX-License-Identifier: MPL-2.0
#
# Fetches the Zabbix sources the API model is extracted from, as tools/zabbix-sources.lock pins them: for each version a
# shallow, sparse, blob-filtered clone of the pinned commit into $ZABBIX_SOURCES/<version> (default: .zabbix-sources/ at
# the repository root, which git ignores), holding only the paths the extractor reads, and the database schema:
#
#   ui/include/*.php           defines.inc.php (the API's constants), schema.inc.php (the database schema the frontend
#                              reads: field lengths, the tables a request is stored in), and the other includes
#   ui/include/classes/        the API services (classes/api/services), CApiServiceFactory, CApiInputValidator,
#                              CAudit and the helpers the services call
#   create/src/schema.tmpl     the database schema as the server is built from it; read by no tool yet, kept for the
#                              reserved Zabbix.Data package (docs/adr/0006-protocol-split.md)
#
# A checkout whose HEAD is already the pinned commit is kept; any other is replaced. A fetched commit that is not the
# pinned one, or a tag that no longer names it, fails the run: the pin is the review boundary.
#
#   tools/fetch-zabbix-sources.sh [version ...]      default: every version in the lock
set -euo pipefail

root="$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd)"
lock="$root/tools/zabbix-sources.lock"
dest="${ZABBIX_SOURCES:-$root/.zabbix-sources}"
paths=('/ui/include/*.php' '/ui/include/classes/' '/create/src/schema.tmpl')

repository="$(awk '$1 == "repository" { print $2 }' "$lock")"
[[ -n "$repository" ]] || { echo "fetch-zabbix-sources: no repository line in $lock" >&2; exit 1; }
mapfile -t pins < <(awk '!/^#/ && NF == 3 && $1 != "repository" { print $1 " " $2 " " $3 }' "$lock")
wanted=("$@")

mkdir -p "$dest"
for pin in "${pins[@]}"; do
  read -r version ref commit <<<"$pin"
  if (( ${#wanted[@]} )) && [[ ! " ${wanted[*]} " == *" $version "* ]]; then continue; fi
  checkout="$dest/$version"
  if [[ -d "$checkout/.git" ]] && [[ "$(git -C "$checkout" rev-parse HEAD 2>/dev/null)" == "$commit" ]]; then
    echo "fetch-zabbix-sources: $version is $ref ($commit), kept"
    continue
  fi
  echo "fetch-zabbix-sources: $version <- $repository $ref ($commit)"
  if [[ "$ref" != "master" ]]; then
    named="$(git ls-remote "$repository" "refs/tags/$ref^{}" | awk '{ print $1 }')"
    [[ -n "$named" ]] || named="$(git ls-remote "$repository" "refs/tags/$ref" | awk '{ print $1 }')"
    if [[ "$named" != "$commit" ]]; then
      echo "fetch-zabbix-sources: the tag $ref names ${named:-nothing}, not the pinned $commit; review the lock" >&2
      exit 1
    fi
  fi
  rm -rf "$checkout"
  git init -q "$checkout"
  git -C "$checkout" remote add origin "$repository"
  git -C "$checkout" sparse-checkout set --no-cone "${paths[@]}"
  git -C "$checkout" fetch -q --depth 1 --filter=blob:none origin "$commit"
  git -C "$checkout" -c advice.detachedHead=false checkout -q FETCH_HEAD
  actual="$(git -C "$checkout" rev-parse HEAD)"
  if [[ "$actual" != "$commit" ]]; then
    echo "fetch-zabbix-sources: $version checked out $actual, not the pinned $commit" >&2
    exit 1
  fi
done
