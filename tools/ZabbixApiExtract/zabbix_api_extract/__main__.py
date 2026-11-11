# This Source Code Form is subject to the terms of the Mozilla Public
# License, v. 2.0. Copyright (c) 2026 schroeder247. SPDX-License-Identifier: MPL-2.0
"""python3 -m zabbix_api_extract [--sources DIR] [--facts DIR] [--schema DIR] [--legacy FILE] [--suggest FILE]
python3 -m zabbix_api_extract verify [--sources DIR] [--schema DIR] [--all] [--codes A,B]

Reads each version's Zabbix source (the pinned checkouts tools/fetch-zabbix-sources.sh makes), writes the facts of each
version (zabbix-api-<version>.json, an intermediate, beside the sources by default) and schema/zabbix-api.json (the
merged description the generator reads); `verify` holds that description to a second, independent reading of the
source (verify.py). See README.md."""
from __future__ import annotations

import argparse
import json
import os
import sys

from .facts import VersionFacts
from .merge import VERSIONS, Merger
from .php import Sym


def main(argv: list[str] | None = None) -> int:
    argv = sys.argv[1:] if argv is None else argv
    if argv[:1] == ['verify']:
        from .verify import run
        return run(argv[1:])
    here = os.path.dirname(os.path.abspath(__file__))
    next_dir = os.path.normpath(os.path.join(here, '..', '..', '..'))
    parser = argparse.ArgumentParser(prog='zabbix_api_extract')
    parser.add_argument('--sources', default=default_sources(next_dir),
                        help='a directory with one Zabbix source checkout per version (7.0, 7.4, 8.0); '
                             'default: $ZABBIX_SOURCES, else .zabbix-sources/ (tools/fetch-zabbix-sources.sh)')
    parser.add_argument('--facts', help='where the per-version facts are written; default: the sources directory')
    parser.add_argument('--schema', default=os.path.join(next_dir, 'schema'), help='where the merged description is written')
    parser.add_argument('--legacy', help='an earlier description (JSON) to take naming suggestions from (names and sentences only)')
    parser.add_argument('--suggest', help='write the names the overlay does not decide yet to this file')
    args = parser.parse_args(argv)
    if not os.path.isdir(args.sources):
        parser.error(f'no Zabbix sources at {args.sources}: run tools/fetch-zabbix-sources.sh, or give --sources')
    facts_dir = args.facts or args.sources

    facts: dict[str, dict] = {}
    defines: dict[str, dict[str, int]] = {}
    schemas: dict[str, dict] = {}
    failed = False
    for version in VERSIONS:
        root = os.path.join(args.sources, version)
        extractor = VersionFacts(root, version)
        facts[version] = extractor.extract()
        interp = extractor.interp
        values = {}
        for name in interp.defines:
            value = interp.define(name)
            if isinstance(value, int) and not isinstance(value, bool):
                values[name] = int(value)
        # class constants too (CCepRuleHelper::WINDOW_NONE): the enums some newer services validate with
        for cdef in list(interp.classes.values()):
            for cname in cdef.consts:
                try:
                    value = interp.class_const(cdef.name, cname)
                except Exception:   # noqa: BLE001 - a constant the interpreter cannot evaluate is not an enum member
                    continue
                if isinstance(value, int) and not isinstance(value, bool):
                    values[f'{cdef.name}::{cname}'] = int(value)
        defines[version] = values
        facts[version]['maskedPaths'] = masked_paths(interp)
        schemas[version] = {t: extractor.table(t) for t in interp.schema} if isinstance(interp.schema, dict) else {}
        path = os.path.join(facts_dir, f'zabbix-api-{version}.json')
        write_json(path, dict(facts[version], **{'$comment': f'The Zabbix {version} API as its source ({facts[version].get("zabbix")}) states it, '
                                                  'extracted by tools/ZabbixApiExtract. Facts only: names come later, from overlay.json.'}))
        for problem in facts[version]['problems']:
            print(f'{version}: {problem}', file=sys.stderr)

    with open(os.path.join(args.schema, 'overlay.json'), encoding='utf-8') as handle:
        overlay = json.load(handle)
    legacy = None
    if args.legacy:
        with open(args.legacy, encoding='utf-8') as handle:
            legacy = json.load(handle)
    # the server's own C defines a read field's values are (event.flags: ZBX_EVENT_NORMAL/_COPIED, cep/zbx_cep.h), which
    # no PHP file declares: the overlay states them with their source, and they join every version's defines
    for name, value in overlay.get('serverDefines', {}).items():
        if not name.startswith('_'):
            for version in VERSIONS:
                defines[version].setdefault(name, value)
    merger = Merger(facts, defines, overlay, legacy, schemas)
    description = merger.run()
    # what each version's model was read from: the lock's ref and the checkout's commit, beside the API version the
    # source declares (8.0.0rc1 declares ZABBIX_API_VERSION 8.0.0, so the define alone does not name the release)
    pins = read_lock(os.path.join(next_dir, 'tools', 'zabbix-sources.lock'))
    description['sources'] = {v: source_of(os.path.join(args.sources, v), pins.get(v), facts[v].get('zabbix')) for v in VERSIONS}
    write_json(os.path.join(args.schema, 'zabbix-api.json'), description)
    for problem in merger.problems:
        print(f'merge: {problem}', file=sys.stderr)
    # a merge problem (an enum name two families want, ...) is a model the generator would name by guess: fail the run
    failed |= bool(merger.problems)
    # an overlay entry nothing reads documents a record or field the description no longer has: remove it
    stale = merger.stale_docs()
    for entry in stale:
        print(f'STALE overlay entry (reads nothing): {entry}', file=sys.stderr)
    failed |= bool(stale)
    if args.suggest:
        write_json(args.suggest, merger.suggestions)
    print(f'{len(description["services"])} objects, {sum(len(s["methods"]) for s in description["services"])} methods, '
          f'{len(description["types"])} records, {len(description["enums"])} enums')
    return 1 if failed else 0


def read_lock(path: str) -> dict[str, tuple[str, str]]:
    """``tools/zabbix-sources.lock``: per version, the upstream ref and the commit it names."""
    pins: dict[str, tuple[str, str]] = {}
    if os.path.isfile(path):
        with open(path, encoding='utf-8') as handle:
            for line in handle:
                parts = line.split()
                if len(parts) == 3 and not parts[0].startswith('#') and parts[0] != 'repository':
                    pins[parts[0]] = (parts[1], parts[2])
    return pins


def source_of(checkout: str, pin: tuple[str, str] | None, api_version: str | None) -> dict:
    """One version's provenance: the ref and commit the checkout holds (the lock's, when the checkout is the pinned
    commit) and the API version its ``defines.inc.php`` declares."""
    commit = None
    head = os.path.join(checkout, '.git', 'HEAD')
    if os.path.isfile(head):
        with open(head, encoding='utf-8') as handle:
            text = handle.read().strip()
        if len(text) == 40 and all(c in '0123456789abcdef' for c in text):
            commit = text
    out: dict = {}
    if pin is not None and (commit is None or commit == pin[1]):
        out['ref'], commit = pin
    if commit:
        out['commit'] = commit
    out['apiVersion'] = api_version
    return out


def default_sources(repository: str) -> str:
    """$ZABBIX_SOURCES, else the folder tools/fetch-zabbix-sources.sh fills (``.zabbix-sources/`` at the repository root)."""
    return os.environ.get('ZABBIX_SOURCES') or os.path.join(repository, '.zabbix-sources')


def masked_paths(interp) -> list[str]:
    """The request paths the audit log masks (``CAudit::MASKED_PATHS``, CAudit.php 8.0:259): the source's own list of
    what is a secret, some of them only under a condition (a macro's value when its type is secret)."""
    try:
        rules = interp.class_const('CAudit', 'MASKED_PATHS')
    except Exception:   # noqa: BLE001 - a version without the constant masks nothing by this list
        return []
    found: set[str] = set()

    def walk(node) -> None:
        if isinstance(node, dict):
            for key, value in node.items():
                if key == 'paths' and isinstance(value, dict):
                    found.update(str(p) for p in value.values() if isinstance(p, str))
                else:
                    walk(value)
    walk(rules)
    return sorted(found)


def write_json(path: str, value) -> None:
    text = json.dumps(value, indent=1, ensure_ascii=False, sort_keys=False, default=_default) + '\n'
    with open(path, 'w', encoding='utf-8', newline='\n') as handle:
        handle.write(text)


def _default(value):
    if isinstance(value, Sym):
        return int(value)
    if isinstance(value, set):
        return sorted(value)
    # no silent str(): a value the writer does not know is a bug in the extractor, not a string in the model
    raise TypeError(f'{type(value).__name__} is not a JSON value of the description: {value!r}')


if __name__ == '__main__':
    sys.exit(main())
