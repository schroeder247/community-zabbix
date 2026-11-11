#!/usr/bin/env bash
# This Source Code Form is subject to the terms of the Mozilla Public
# License, v. 2.0. Copyright (c) 2026 schroeder247. SPDX-License-Identifier: MPL-2.0
#
# Regenerates the API model from the pinned Zabbix sources: fetch (tools/fetch-zabbix-sources.sh, the lock in
# tools/zabbix-sources.lock), extract each version and merge them into schema/zabbix-api.json (tools/ZabbixApiExtract),
# then verify the model against a second reading of the same sources. The per-version facts
# (zabbix-api-<version>.json) are intermediates and land beside the sources, in $ZABBIX_SOURCES (default:
# .zabbix-sources/, ignored by git); only the merged model is checked in.
#
#   tools/regenerate-schema.sh            regenerate; review `git diff schema/zabbix-api.json`
#   tools/regenerate-schema.sh --check    regenerate and fail when the model differs from the committed one (CI)
#
# Needs git and python3 (standard library only).
set -euo pipefail

root="$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd)"
export ZABBIX_SOURCES="${ZABBIX_SOURCES:-$root/.zabbix-sources}"
check=false
for argument in "$@"; do
  case "$argument" in
    --check) check=true ;;
    *) echo "regenerate-schema: unknown argument '$argument' (usage: tools/regenerate-schema.sh [--check])" >&2; exit 2 ;;
  esac
done

"$root/tools/fetch-zabbix-sources.sh"
cd "$root/tools/ZabbixApiExtract"
python3 -m zabbix_api_extract --sources "$ZABBIX_SOURCES" --facts "$ZABBIX_SOURCES" --schema "$root/schema"
python3 -m zabbix_api_extract verify --sources "$ZABBIX_SOURCES" --schema "$root/schema"

if $check; then
  # against HEAD, not the index: a staged model is still not the committed one
  if ! git -C "$root" diff --exit-code --stat HEAD -- schema/zabbix-api.json; then
    echo "regenerate-schema: schema/zabbix-api.json differs from what the pinned sources give (above);" \
         "regenerate it with tools/regenerate-schema.sh and review the model change, or fix the pin" >&2
    exit 1
  fi
  echo "regenerate-schema: schema/zabbix-api.json is what the pinned sources give"
fi
