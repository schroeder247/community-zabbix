#!/usr/bin/env bash
# This Source Code Form is subject to the terms of the Mozilla Public
# License, v. 2.0. Copyright (c) 2026 schroeder247. SPDX-License-Identifier: MPL-2.0
#
# The live matrix's lab: three throwaway Zabbix servers (7.0.31, 7.4.15, and 8.0 at 8.0.0rc1's API) and the EF store's
# own PostgreSQL, each a `podman kube play` of a definition in this folder, plus the Admin API token each server's tests
# use.
#
#   lab.sh start [7.0|7.4|8.0|pg|all ...]   create what is missing (default: all), wait until it answers, make tokens
#   lab.sh env [--github]                    print the environment contract for what answers (bash exports, or
#                                            KEY=value lines for $GITHUB_ENV)
#   lab.sh status                            what answers, and what this script created
#   lab.sh stop                              remove ONLY the pods this script created (the ledger), never others
#   lab.sh pki                               (re)make the lab CA, server and client certificates
#
# A target whose port already answers is REUSED, never replaced and never recorded, so `stop` leaves it alone: on a
# shared machine a server on one of these ports may predate these definitions and belong to someone else. A pod that
# exists but does not answer is an error, not something to replace.
#
# State (tokens, the PKI, the ledger, rendered definitions) lives in $ZBX_LAB_STATE, /tmp/zbxlab by default:
#   token-<api port>   the API token (0600), read by the tests through ZABBIX_TEST_APIS's "@file" form
#   pki/               ca.crt, server.{crt,key}, client.{crt,key}; world-readable because the server runs as uid 1997
#                      inside its container and reads the key through a bind mount: a throwaway lab's key, never reuse
#   lab-created        the ledger: one pod name per line, written when this script created it
set -euo pipefail

here="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
state="${ZBX_LAB_STATE:-/tmp/zbxlab}"
pki="$state/pki"
ledger="$state/lab-created"
# How a server inside its pod reaches a listener on this host (the passive-proxy, connector and webhook tests).
# Rootless podman 5 resolves host.containers.internal to 169.254.1.2 in every pod; it reaches a host listener bound to
# 0.0.0.0 (not one bound to 127.0.0.1), which sees the host's LAN address as the peer (measured).
host_from_server="${ZBX_LAB_HOST_FROM_SERVER:-host.containers.internal}"

# label | definition | pod | api port | trapper port — the definitions' own values. ZBX_LAB_TARGETS
# ('label|definition|pod|api|trapper;…') replaces the table, e.g. to run a second set of pods beside servers someone else
# owns; the definition is then rendered with that pod name and those host ports.
default_targets=(
  "7.0|zabbix-7.0.yaml|zbxlab-70|18070|18051"
  "7.4|zabbix-7.4.yaml|zbxlab-74|18074|18053"
  "8.0|zabbix-8.0.yaml|zbxlab-80|18080|18052"
)
if [[ -n "${ZBX_LAB_TARGETS:-}" ]]; then IFS=';' read -r -a targets <<<"$ZBX_LAB_TARGETS"; else targets=("${default_targets[@]}"); fi
pg_port=55432
pg_connection="Host=127.0.0.1;Port=$pg_port;Database=zbxtest;Username=zbxtest;Password=zbxtest;Timeout=3"

log() { printf '%s\n' "$*" >&2; }
die() { log "lab.sh: $*"; exit 1; }

field() { local IFS='|'; read -r -a parts <<<"$1"; printf '%s' "${parts[$2]}"; }

target_for() {
  local t
  for t in "${targets[@]}"; do [[ "$(field "$t" 0)" == "$1" ]] && { printf '%s' "$t"; return 0; }; done
  return 1
}

rpc() { # url method params-json [token]
  # ${auth[@]+...}: macOS's bash 3.2 calls an empty array unbound under set -u.
  local auth=()
  [[ -n "${4:-}" ]] && auth=(-H "Authorization: Bearer $4")
  curl -sS -m 10 -H 'Content-Type: application/json-rpc' ${auth[@]+"${auth[@]}"} \
    -d "{\"jsonrpc\":\"2.0\",\"method\":\"$2\",\"params\":$3,\"id\":1}" "$1"
}

api_version() { rpc "http://localhost:$1/api_jsonrpc.php" apiinfo.version '[]' 2>/dev/null | jq -er '.result' 2>/dev/null; }

pg_answers() { (exec 3<>"/dev/tcp/127.0.0.1/$pg_port") 2>/dev/null; }

make_pki() {
  mkdir -p "$pki"
  if [[ -s "$pki/ca.crt" && -s "$pki/server.crt" && -s "$pki/client.crt" && "${1:-}" != force ]]; then return 0; fi
  log "lab.sh: making the lab PKI in $pki"
  local ext
  ext="$(mktemp)"
  # RSA: a TLS 1.2 handshake with Zabbix's default ciphers needs it, and .NET on macOS stops at TLS 1.2.
  openssl genrsa -out "$pki/ca.key" 2048 2>/dev/null
  openssl req -x509 -new -key "$pki/ca.key" -days 3650 -subj '/O=Zabbix lab/CN=zbxlab-ca' -out "$pki/ca.crt"
  for who in server client; do
    openssl genrsa -out "$pki/$who.key" 2048 2>/dev/null
    openssl req -new -key "$pki/$who.key" -subj "/O=Zabbix lab/CN=zbxlab-$who" -out "$pki/$who.csr"
    printf 'subjectAltName=DNS:localhost,IP:127.0.0.1\nextendedKeyUsage=serverAuth,clientAuth\n' >"$ext"
    openssl x509 -req -in "$pki/$who.csr" -CA "$pki/ca.crt" -CAkey "$pki/ca.key" -CAcreateserial -days 3650 \
      -extfile "$ext" -out "$pki/$who.crt" 2>/dev/null
    rm -f "$pki/$who.csr"
  done
  rm -f "$ext"
  chmod 0755 "$pki"
  chmod 0644 "$pki"/*.crt "$pki"/*.key
}

ledger_add() { mkdir -p "$state"; grep -qx "$1" "$ledger" 2>/dev/null || printf '%s\n' "$1" >>"$ledger"; }

token_valid() { # api-port token
  rpc "http://localhost:$1/api_jsonrpc.php" host.get '{"output":["hostid"],"limit":1}' "$2" 2>/dev/null | jq -e '.result' >/dev/null 2>&1
}

make_token() { # api-port
  local port="$1" file="$state/token-$1" url="http://localhost:$1/api_jsonrpc.php" sid name tokenid
  if [[ -s "$file" ]] && token_valid "$port" "$(tr -d '[:space:]' <"$file")"; then return 0; fi
  sid="$(rpc "$url" user.login '{"username":"Admin","password":"zabbix"}' | jq -er '.result')" \
    || die "Admin/zabbix cannot log in to :$port, so no token can be made; write one to $file yourself"
  name="zbxlab-admin"
  # A token of this name left by an earlier lab (its file lost) is replaced: names are unique per user.
  tokenid="$(rpc "$url" token.get "{\"output\":[\"tokenid\"],\"filter\":{\"name\":\"$name\"}}" "$sid" | jq -r '.result[0].tokenid // empty')"
  [[ -n "$tokenid" ]] && rpc "$url" token.delete "[\"$tokenid\"]" "$sid" >/dev/null
  tokenid="$(rpc "$url" token.create "{\"name\":\"$name\",\"userid\":\"1\"}" "$sid" | jq -er '.result.tokenids[0]')" \
    || die "token.create failed on :$port"
  umask 077
  rpc "$url" token.generate "[\"$tokenid\"]" "$sid" | jq -er '.result[0].token' >"$file" || die "token.generate failed on :$port"
  rpc "$url" user.logout '[]' "$sid" >/dev/null || true
  log "lab.sh: token for :$port written to $file"
}

wait_for_api() { # api-port
  local i
  for ((i = 0; i < 180; i++)); do
    api_version "$1" >/dev/null && return 0
    sleep 2
  done
  die "the API on :$1 did not answer within 6 minutes (podman logs <pod>-server, <pod>-web)"
}

# A fresh server's ids rows (the next id of each table, DB::reserveIds) are created by the first insert into each table,
# and two first inserts at once collide on ids_pkey: the frontend answers "Database error occurred." to one of them. The
# live suites create objects in parallel, so a new pod gets every row up front, at its table's current maximum, as
# DB::refreshIds would write it.
seed_ids() { # pod
  podman exec -i "$1-db" psql -q -v ON_ERROR_STOP=1 -U zabbix -d zabbix >/dev/null <<'SQL'
DO $$
DECLARE k record;
BEGIN
  FOR k IN
    SELECT c.table_name, u.column_name
    FROM information_schema.table_constraints c
    JOIN information_schema.key_column_usage u USING (constraint_schema, constraint_name, table_name)
    JOIN information_schema.columns t ON t.table_schema = c.table_schema AND t.table_name = c.table_name AND t.column_name = u.column_name
    WHERE c.constraint_type = 'PRIMARY KEY' AND c.table_schema = 'public' AND t.data_type = 'bigint'
      AND (SELECT count(*) FROM information_schema.key_column_usage x
           WHERE x.constraint_schema = c.constraint_schema AND x.constraint_name = c.constraint_name) = 1
  LOOP
    EXECUTE format('INSERT INTO ids (table_name, field_name, nextid) SELECT %L, %L, COALESCE(MAX(%I), 0) FROM %I
                    ON CONFLICT DO NOTHING', k.table_name, k.column_name, k.column_name, k.table_name);
  END LOOP;
END $$;
SQL
}

start_zabbix() { # label
  local t def pod api trapper rendered version
  t="$(target_for "$1")" || die "unknown target '$1'"
  def="$(field "$t" 1)" pod="$(field "$t" 2)" api="$(field "$t" 3)" trapper="$(field "$t" 4)"
  if version="$(api_version "$api")"; then
    log "lab.sh: $1 already answers on :$api ($version); reusing it"
  else
    podman pod exists "$pod" && die "pod $pod exists but :$api does not answer; start it (podman pod start $pod) or remove it yourself"
    make_pki
    mkdir -p "$state"
    rendered="$state/rendered-$pod.yaml"
    local d
    for d in "${default_targets[@]}"; do [[ "$(field "$d" 1)" == "$def" ]] && break; done
    sed -e "s#@PKI@#$pki#g" \
      -e "s#^  name: $(field "$d" 2)\$#  name: $pod#" \
      -e "s#hostPort: $(field "$d" 3) }#hostPort: $api }#" \
      -e "s#hostPort: $(field "$d" 4) }#hostPort: $trapper }#" "$here/$def" >"$rendered"
    log "lab.sh: creating $pod from $def"
    podman kube play "$rendered" >/dev/null
    ledger_add "$pod"
    wait_for_api "$api"
    seed_ids "$pod" || die "seeding the ids table of $pod failed (podman exec $pod-db psql)"
    log "lab.sh: $1 answers on :$api ($(api_version "$api")), trapper :$trapper"
  fi
  make_token "$api"
}

start_pg() {
  if pg_answers; then log "lab.sh: :$pg_port already answers; reusing it"; return 0; fi
  podman pod exists zbxtest-pg && die "pod zbxtest-pg exists but :$pg_port does not answer"
  podman kube play "$here/zbxtest-postgres.yaml" >/dev/null
  ledger_add zbxtest-pg
  local i
  for ((i = 0; i < 60; i++)); do
    podman exec zbxtest-pg-db pg_isready -U zbxtest >/dev/null 2>&1 && pg_answers && return 0
    sleep 1
  done
  die "PostgreSQL on :$pg_port did not become ready"
}

# Features a server has only when it was created from these definitions (servers that predate them, reused as they
# are, lack both): read from the running container, so the contract says what is true, not what was intended.
features_of() { # pod
  local env out=()
  env="$(podman inspect "$1-server" --format '{{range .Config.Env}}{{println .}}{{end}}' 2>/dev/null || true)"
  grep -q '^ZBX_TLSCERTFILE=/' <<<"$env" && out+=(cert)
  grep -qE '^ZBX_STARTCONNECTORS=[1-9]' <<<"$env" && out+=(connector)
  local IFS=,
  printf '%s' "${out[*]:-}"
}

cmd_env() {
  local github="${1:-}" apis=() trappers=() features=() t label pod api trapper f
  for t in "${targets[@]}"; do
    label="$(field "$t" 0)" pod="$(field "$t" 2)" api="$(field "$t" 3)" trapper="$(field "$t" 4)"
    api_version "$api" >/dev/null || continue
    [[ -s "$state/token-$api" ]] || continue
    apis+=("$label=http://localhost:$api/api_jsonrpc.php|@$state/token-$api")
    trappers+=("$label=127.0.0.1:$trapper")
    f="$(features_of "$pod")"
    [[ -n "$f" ]] && features+=("$label=$f")
  done
  local IFS=';'
  emit() { if [[ "$github" == --github ]]; then printf '%s=%s\n' "$1" "$2"; else printf "export %s='%s'\n" "$1" "$2"; fi; }
  emit ZABBIX_TEST_APIS "${apis[*]:-}"
  emit ZABBIX_TEST_TRAPPERS "${trappers[*]:-}"
  emit ZABBIX_TEST_FEATURES "${features[*]:-}"
  [[ -s "$pki/ca.crt" ]] && emit ZABBIX_TEST_PKI "$pki"
  emit ZABBIX_TEST_HOST_FROM_SERVER "$host_from_server"
  pg_answers && emit ZABBIX_TEST_POSTGRES "$pg_connection"
  return 0
}

cmd_status() {
  local t label pod api version
  for t in "${targets[@]}"; do
    label="$(field "$t" 0)" pod="$(field "$t" 2)" api="$(field "$t" 3)"
    version="$(api_version "$api" || echo 'not answering')"
    printf '%-4s :%s %-16s %-14s features=%s%s\n' "$label" "$api" "$pod" "$version" "$(features_of "$pod")" \
      "$(grep -qx "$pod" "$ledger" 2>/dev/null && echo ' (created by lab.sh)')"
  done
  printf 'pg   :%s %s\n' "$pg_port" "$(pg_answers && echo answering || echo 'not answering')"
}

cmd_stop() {
  [[ -s "$ledger" ]] || { log "lab.sh: the ledger is empty; nothing of mine to remove"; return 0; }
  local pod rendered api t
  while read -r pod; do
    [[ -n "$pod" ]] || continue
    rendered="$state/rendered-$pod.yaml"
    log "lab.sh: removing $pod"
    if [[ -s "$rendered" ]]; then podman kube down --force "$rendered" >/dev/null || true
    elif [[ "$pod" == zbxtest-pg ]]; then podman kube down --force "$here/zbxtest-postgres.yaml" >/dev/null || true
    else podman pod rm -f "$pod" >/dev/null || true; fi
    for t in "${targets[@]}"; do
      [[ "$(field "$t" 2)" == "$pod" ]] || continue
      api="$(field "$t" 3)"
      rm -f "$state/token-$api" "$rendered"
    done
  done <"$ledger"
  rm -f "$ledger"
}

main() {
  local cmd="${1:-status}"
  shift || true
  case "$cmd" in
    start)
      local wanted=("$@") w
      [[ ${#wanted[@]} -eq 0 || " ${wanted[*]} " == *" all "* ]] && wanted=(7.0 7.4 8.0 pg)
      for w in "${wanted[@]}"; do
        if [[ "$w" == pg ]]; then start_pg; else start_zabbix "$w"; fi
      done
      ;;
    env) cmd_env "${1:-}" ;;
    status) cmd_status ;;
    stop) cmd_stop ;;
    pki) make_pki force ;;
    *) die "usage: lab.sh start|env|status|stop|pki (see the header)" ;;
  esac
}

main "$@"
