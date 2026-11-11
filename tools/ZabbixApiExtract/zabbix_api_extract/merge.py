# This Source Code Form is subject to the terms of the Mozilla Public
# License, v. 2.0. Copyright (c) 2026 schroeder247. SPDX-License-Identifier: MPL-2.0
"""Every version's facts, merged into the one description the generator reads (``schema/zabbix-api.json``).

Structure comes from the facts only: which records exist, which fields they carry, their kinds, which are required,
which are deprecated, how long a string may be, which versions have them. The reviewed overlay
(``schema/overlay.json``) supplies what the source cannot: C# names that stay stable, documentation sentences keyed
by wire name, the enum names, and the few facts the PHP does not state in a form an analysis can read (each with the
file and line it was read from)."""
from __future__ import annotations

import re
from typing import Any

VERSIONS = ['7.0', '7.4', '8.0']


def vkey(v: str) -> tuple:
    return tuple(int(x) for x in v.split('.'))


_NOT_ID = {'uuid', 'valid', 'void', 'paid', 'grid', 'id', 'ids'}


def _word(part: str) -> str:
    low = part.lower()
    if part == low and low not in _NOT_ID:
        if low.endswith('ids') and len(low) > 3:
            return low[:-3].capitalize() + 'Ids'
        if low.endswith('id') and len(low) > 2:
            return low[:-2].capitalize() + 'Id'
    return part[0].upper() + part[1:]


# The identifiers CA1720 reads as type names (a generated member named exactly one of them is renamed)
CLR_TYPE_WORDS = frozenset({'Object', 'Obj', 'String', 'Char', 'Int', 'Integer', 'Long', 'Short', 'Float', 'Double',
                            'Decimal', 'Single', 'Signed', 'Unsigned', 'Guid', 'Pointer', 'Ptr'})


def pascal(text: str) -> str:
    """``tls_psk_identity`` → ``TlsPskIdentity``; ``selectDHosts`` → ``SelectDHosts``; ``key_`` → ``Key``;
    ``proxyid`` → ``ProxyId``; ``hostids`` → ``HostIds``."""
    parts = [p for p in re.split(r'[_\W]+', text) if p]
    out = ''.join(_word(p) for p in parts)
    if out and out[0].isdigit():
        out = '_' + out
    return out or '_'


def const_pascal(text: str) -> str:
    """``NOT_MONITORED`` → ``NotMonitored``."""
    parts = [p for p in text.split('_') if p]
    out = ''.join(p[0].upper() + p[1:].lower() for p in parts)
    if out and out[0].isdigit():
        out = '_' + out
    return out or '_'


def singular(word: str) -> str:
    if word.endswith('ies') and len(word) > 4:
        return word[:-3] + 'y'
    for suffix in ('sses', 'xes', 'ches', 'shes'):
        if word.endswith(suffix):
            return word[:-2]
    # status, opstatus: a Latin -us is singular already (singular('opstatus') would give 'opstatu', a record named OpstatuRequest)
    if word.endswith('s') and not word.endswith(('ss', 'us')) and len(word) > 3:
        return word[:-1]
    return word


def plural(stem: str) -> str:
    if stem.endswith('y') and not stem.endswith(('ay', 'ey', 'oy', 'uy')):
        return stem[:-1] + 'ies'
    if stem.endswith(('s', 'x', 'ch', 'sh')):
        return stem + 'es'
    return stem + 's'


def since_removed(present: list[str]) -> tuple[str | None, str | None]:
    """The first version a thing is in, and the first later version it is gone from (None when it is in every one)."""
    present = [v for v in VERSIONS if v in present]
    if not present:
        return None, None
    since = present[0]
    removed = None
    first = VERSIONS.index(since)
    for v in VERSIONS[first:]:
        if v not in present:
            removed = v
            break
    return since, removed


# ----------------------------------------------------------------------------------------------------------------------
# enums


class Enums:
    """Enum resolution: the define family an ``in`` list names, or failing that the family a field's name points at."""

    def __init__(self, defines: dict[str, dict[str, int]], overlay: dict):
        self.defines = defines
        self.overlay = overlay.get('enums', {})
        self.field_enums = overlay.get('fieldEnums', {})
        self.families: dict[str, dict] = {}     # key -> {'key', 'members': {define: {value, versions}}, 'fields': [...]}
        self.tables_of: dict[str, str] = {}     # object -> its table
        self.by_where: dict[str, str] = {}      # field -> the family prefix it first resolved to
        self._by_prefix: dict[str, dict[str, dict[str, int]]] = {}

    def family_members(self, prefix: str, version: str) -> dict[str, int]:
        cache = self._by_prefix.setdefault(version, {})
        if prefix not in cache:
            cache[prefix] = {n: v for n, v in self.defines[version].items() if n.startswith(prefix + '_')}
        return cache[prefix]

    @staticmethod
    def common_prefix(names: list[str]) -> str:
        split = [n.split('_') for n in names]
        out = []
        for parts in zip(*split):
            if all(p == parts[0] for p in parts):
                out.append(parts[0])
            else:
                break
        # never the whole of any name: a family is a prefix *and* a member suffix
        while out and any(len(s) == len(out) for s in split):
            out.pop()
        return '_'.join(out)

    def resolve(self, where: str, values_by_version: dict[str, list[int]], names_by_version: dict[str, dict], by_name: bool = True) -> str | None:
        """The enum key for a field, or None when the values are not an enum (a range, or 0/1 with no family)."""
        forced = self.field_enums.get(where)
        if forced == 'none':
            return None
        values = sorted({v for vs in values_by_version.values() for v in vs})
        names: dict[int, str] = {}
        for nv in names_by_version.values():
            for k, n in nv.items():
                names.setdefault(int(k), n)
        if isinstance(forced, list):
            prefix = self.common_prefix(forced)
            if not prefix:
                return self.composed(forced, where)
            return self.family(prefix, values_by_version, where, exact=False,
                               named={self.value_of(n): n for n in forced})
        if forced:
            prefix, _, suffix = forced.partition('#')
            key = self.family(prefix, values_by_version, where, exact=False)
            if suffix:
                # the same family used as a set of bits (tls_accept = 1|2|4): its own [Flags] enum
                fam = dict(self.families[key])
                fam['key'] = forced
                fam['flags'] = True
                fam['fields'] = [where]
                self.families.setdefault(forced, fam)
                return forced
            return key
        prefixes = {}
        for version, nv in names_by_version.items():
            vnames = [n for n in nv.values() if isinstance(n, str)]
            if len(vnames) >= 2:
                prefixes[version] = self.common_prefix(vnames)
        if len(set(prefixes.values())) > 1 and all(prefixes.values()):
            return self.renamed(prefixes, names_by_version, where)
        # class-constant families (CCepRuleHelper::WINDOW_*, CCepRule.php 8.0:409) count when one class owns every name
        owners = {names[v].partition('::')[0] for v in values if v in names and '::' in names[v]}
        if values and all(v in names for v in values) and (all('::' not in names[v] for v in values)
                                                          or (len(owners) == 1 and all('::' in names[v] for v in values))):
            prefix = self.common_prefix([names[v] for v in values])
            earlier = self.by_where.get(where)
            if earlier and prefix != earlier and all(names[v].startswith(earlier + '_') for v in values):
                # the same field names a narrower subset elsewhere (one event source's condition types): one enum
                prefix = earlier
            self.by_where.setdefault(where, prefix)
            if prefix:
                return self.family(prefix, values_by_version, where, exact=False, named={v: names[v] for v in values})
        if not by_name:
            return None
        # by name: HOST_STATUS for host.status, ITEM_STATE for item.state …
        obj, _, field = where.partition('.')
        field = field.split('.')[-1]
        table = self.tables_of.get(obj)
        for prefix in self.name_candidates(obj, field, table):
            version = max(values_by_version, key=vkey) if values_by_version else VERSIONS[-1]
            members = self.family_members(prefix, version)
            if len(members) >= 2 and set(values) <= set(members.values()) and len(set(members.values())) == len(members):
                return self.family(prefix, values_by_version, where, exact=False)
        return None

    @staticmethod
    def name_candidates(obj: str, field: str, table: str | None = None) -> list[str]:
        o, f = obj.upper(), field.upper()
        out = [f'{o}_{f}', f'ZBX_{o}_{f}']
        if o.endswith('PROTOTYPE'):
            out.append(f'{o[:-9]}_{f}')
        if table:
            t = table.upper()
            ts = t[:-1] if t.endswith('S') else t
            out += [f'{t}_{f}', f'{ts}_{f}', f'ZBX_{ts}_{f}']
        out.append(f)
        return list(dict.fromkeys(out))

    def composed(self, defines: list[str], where: str) -> str:
        """An enum the source spells with defines of more than one family: ``service.status`` is ``ZBX_SEVERITY_OK``
        (-1) or a ``TRIGGER_SEVERITY_*`` (``CService.php`` 7.0:1102-1130, ``defines.inc.php`` 7.0:859-865). Each member
        is named by what follows the last word every define shares (``SEVERITY``: ``OK``, ``NOT_CLASSIFIED``, …), and
        the family's key joins the families it draws on (``ZBX_SEVERITY+TRIGGER_SEVERITY``)."""
        split = [d.split('_') for d in defines]
        shared = [w for w in split[0] if all(w in s for s in split)]
        if not shared:
            raise ValueError(f'{where}: the defines {defines} share no word to name their members by')
        word = shared[-1]
        owners, suffixes = [], {}
        for d, parts in zip(defines, split):
            at = len(parts) - 1 - parts[::-1].index(word)
            owners.append('_'.join(parts[:at + 1]))
            suffixes[d] = '_'.join(parts[at + 1:])
        key = '+'.join(dict.fromkeys(owners))
        fam = self.families.get(key)
        if fam is None:
            members: dict[str, dict] = {}
            for version in VERSIONS:
                for d in defines:
                    if d in self.defines[version]:
                        members.setdefault(d, {'value': self.defines[version][d], 'versions': [], 'suffix': suffixes[d]})['versions'].append(version)
            fam = {'key': key, 'prefix': key, 'members': members, 'fields': [], 'broad': False}
            self.families[key] = fam
        fam['fields'].append(where)
        return key

    def renamed(self, prefixes: dict[str, str], names_by_version: dict[str, dict], where: str) -> str:
        """One field whose values each version names with its own family (``regexp``'s ``expression_type``:
        ``EXPRESSION_TYPE_*`` in 7.x, ``REGEX_TYPE_*`` in 8.0, ``CRegexp.php`` 8.0:135): one enum, keyed by the oldest
        family, its members the defines of each version's family (``emit_enums`` joins those of one value)."""
        ordered = sorted(prefixes, key=vkey)
        key = prefixes[ordered[0]]
        fam = self.families.get(key)
        if fam is None or not fam.get('renamed'):
            members: dict[str, dict] = dict((fam or {}).get('members') or {})
            for version in VERSIONS:
                prefix = prefixes.get(version) or prefixes[max((v for v in prefixes if vkey(v) <= vkey(version)), key=vkey, default=ordered[0])]
                for n, v in self.family_members(prefix, version).items():
                    entry = members.setdefault(n, {'value': v, 'versions': [], 'suffix': n[len(prefix) + 1:]})
                    if version not in entry['versions']:
                        entry['versions'].append(version)
            fam = {'key': key, 'prefix': key, 'members': members, 'fields': list((fam or {}).get('fields') or []), 'broad': False, 'renamed': True}
            self.families[key] = fam
        fam['fields'].append(where)
        return key

    def value_of(self, define: str) -> int:
        for version in reversed(VERSIONS):
            if define in self.defines[version]:
                return self.defines[version][define]
        raise KeyError(define)

    def is_broad(self, prefix: str, in_values: list) -> bool:
        for version in VERSIONS:
            all_members = self.family_members(prefix, version)
            values = list(all_members.values())
            if len(values) != len(set(values)) or (in_values and len(all_members) > max(3 * len(in_values), len(in_values) + 8)):
                return True
        return False

    def family(self, prefix: str, values_by_version: dict, where: str, exact: bool, named: dict | None = None) -> str:
        in_values = sorted({v for vs in values_by_version.values() for v in vs}) or (sorted(named) if named else [])
        broad = self.is_broad(prefix, in_values)
        if broad and named:
            # A prefix that holds several families (ZBX_PREPROC_* beside ZBX_PREPROC_FAIL_*, ZBX_PROTOTYPE_DISCOVER beside
            # ZBX_PROTOTYPE_STATUS_*): the enum is the union of the defines the rules name, across every field that
            # names them, so item and LLD preprocessing share one type. A union whose values would collide splits.
            wanted = set(named.values())
            index = 1
            while True:
                key = prefix if index == 1 else f'{prefix}#{index}'
                fam = self.families.get(key)
                if fam is None:
                    fam = {'key': key, 'prefix': prefix, 'members': {}, 'fields': [], 'broad': True, 'named': set()}
                    self.families[key] = fam
                # an existing member one of these defines has the value of is the same member under another name
                # (TRIGGER_SEVERITY_OFF beside _NOT_CLASSIFIED): the named define takes its place, no split - but only
                # when nothing else of the family stays beside them; ZBX_WIZARD_FIELD_REQUIRED (1) and _TEXT (1) are
                # two families under one prefix (defines.inc.php 7.4:437-443), and _LIST staying proves it
                named_before = fam.setdefault('named', set(fam['members']))
                wanted_values = {self.value_of(n) for n in wanted}
                kept = {n for n in named_before if self.value_of(n) not in wanted_values}
                union = (kept if not (kept - wanted) or kept == named_before else named_before) | wanted
                values = [self.value_of(n) for n in union]
                if len(values) == len(set(values)):
                    fam['named'] = union
                    fam['members'] = {}
                    for version in VERSIONS:
                        for n, v in self.family_members(prefix, version).items():
                            if n in union:
                                fam['members'].setdefault(n, {'value': v, 'versions': []})['versions'].append(version)
                    break
                index += 1
            fam['fields'].append(where)
            return key
        key = prefix
        fam = self.families.get(key)
        if fam is None:
            members: dict[str, dict] = {}
            for version in VERSIONS:
                all_members = self.family_members(prefix, version)
                chosen = {n: v for n, v in all_members.items() if not (n.endswith(('_COUNT', '_DEFAULT')) and v not in in_values)}
                for n, v in chosen.items():
                    entry = members.setdefault(n, {'value': v, 'versions': []})
                    entry['versions'].append(version)
            fam = {'key': key, 'prefix': prefix, 'members': members, 'fields': [], 'broad': broad}
            self.families[key] = fam
        else:
            # a member the first field left out (a _DEFAULT or _COUNT), which this field's rule accepts:
            # MAP_SHOW_LABEL_DEFAULT (-1) of a map element's show_label (CMap.php 7.4:645)
            have = {m['value'] for m in fam['members'].values()}
            for version in VERSIONS:
                for n, v in self.family_members(prefix, version).items():
                    if v in in_values and v not in have or n in fam['members'] and version not in fam['members'][n]['versions'] and v in in_values:
                        entry = fam['members'].setdefault(n, {'value': v, 'versions': []})
                        if version not in entry['versions']:
                            entry['versions'].append(version)
        fam['fields'].append(where)
        return key


# ----------------------------------------------------------------------------------------------------------------------
# rules merged across versions


def _every_arm_requires(node: dict) -> bool:
    """An API_MULTIPLE whose every arm is API_REQUIRED (CItem's value_type: required when dependent, calculated, and
    'else') is required whatever the other field says. One arm that is optional or API_UNEXPECTED makes it
    conditional, which the description does not claim as required."""
    arms = node.get('rules') or []
    return node.get('type') == 'multiple' and bool(arms) and all('required' in (a.get('flags') or []) for a in arms)


def _unrequired(rule: dict) -> dict:
    """A merged rule with no required flag anywhere in it (a shape lent from another method)."""
    out = dict(rule, required=[])
    if isinstance(rule.get('fields'), dict):
        out['fields'] = {k: _unrequired(v) for k, v in rule['fields'].items()}
    return out


def union_alternatives(arms: list[dict]) -> dict:
    """The arms of one API_MULTIPLE as the one record a caller builds: every field of any arm, a field required only
    when every arm requires it (task.create's itemid is required for check-now only, proxyids for a proxy reload only;
    a filter condition's formulaid only when evaltype is a custom expression), and the values an int field accepts
    united across the arms (action.create's conditiontype: each eventsource arm lists its own condition types).
    Nested objects and API_MULTIPLEs are united the same way, recursively."""
    base = dict(arms[0])
    flags = [f for f in (base.get('flags') or []) if f != 'required']
    if all('required' in (a.get('flags') or []) for a in arms):
        flags.append('required')
    base['flags'] = flags
    values: set = set()
    names: dict = {}
    strings: list = []
    for a in arms:
        c = a.get('in') or {}
        values |= set(c.get('values') or [])
        names.update(c.get('names') or {})
        strings += [x for x in (c.get('strings') or []) if x not in strings]
    if values or strings:
        c = dict(base.get('in') or {})
        if values:
            c['values'] = sorted(values)
            c['names'] = names
        if strings:
            c['strings'] = strings
        base['in'] = c
    if base.get('type') == 'multiple':
        base['rules'] = [r for a in arms if a.get('type') == 'multiple' for r in (a.get('rules') or [])]
    if base.get('type') in ('object', 'objects'):
        field_names: list[str] = []
        for a in arms:
            for n in (a.get('fields') or {}):
                if n not in field_names:
                    field_names.append(n)
        fields = {}
        for n in field_names:
            present = [a['fields'][n] for a in arms if n in (a.get('fields') or {})]
            typed = [r for r in present if r.get('type') not in ('unexpected', 'any')] or present
            u = union_alternatives(typed)
            if len(typed) < len(arms):
                u['flags'] = [f for f in (u.get('flags') or []) if f != 'required']
            fields[n] = u
        base['fields'] = fields
    return base


def _at(rule: dict, version: str) -> dict:
    """A rule as one version has it: the fields an earlier merge records for other versions only are left out."""
    out = rule
    if isinstance(rule.get('fields'), dict):
        out = dict(out, fields={k: _at(v, version) for k, v in rule['fields'].items()
                                if not v.get('versions') or version in v['versions']})
    if isinstance(rule.get('rules'), list):
        out = dict(out, rules=[_at(a, version) for a in rule['rules']])
    return out


def vmerge(nodes: dict[str, dict]) -> dict:
    """One rule per version → one rule: the latest version's type and attributes, which versions carry it, which require
    it, which deprecate it, and every field of every version."""
    versions = [v for v in VERSIONS if v in nodes and nodes[v] is not None]
    latest = nodes[versions[-1]]
    out: dict = {k: v for k, v in latest.items() if k not in ('fields', 'flags', 'rules')}
    out['versions'] = versions
    out['required'] = [v for v in versions if 'required' in (nodes[v].get('flags') or []) or _every_arm_requires(nodes[v])]
    out['deprecated'] = [v for v in versions if 'deprecated' in (nodes[v].get('flags') or [])]
    out['flags'] = latest.get('flags') or []
    # the versions whose validator refuses a field it does not list (no API_ALLOW_UNEXPECTED): there, a field that is
    # gone is refused, where elsewhere it may only be read by a later validation the interpreter did not follow
    out['strict'] = [v for v in versions if 'allow_unexpected' not in (nodes[v].get('flags') or [])]
    values: dict[str, list] = {}
    names: dict[str, dict] = {}
    for v in versions:
        c = nodes[v].get('in') or {}
        if c.get('values'):
            values[v] = c['values']
            names[v] = c.get('names') or {}
        # alternatives (API_MULTIPLE): their 'in' lists count too
        for alt in nodes[v].get('rules') or []:
            ca = alt.get('in') or {}
            if ca.get('values') and alt.get('type') in ('int32', 'ints32'):
                values.setdefault(v, [])
                values[v] = sorted(set(values[v]) | set(ca['values']))
                names.setdefault(v, {}).update(ca.get('names') or {})
    out['values'] = values
    out['names'] = names
    kinds = {v: nodes[v].get('type') for v in versions}
    out['typesByVersion'] = kinds
    field_names: list[str] = []
    for v in versions:
        for name in (nodes[v].get('fields') or {}):
            if name not in field_names:
                field_names.append(name)
    if field_names:
        out['fields'] = {name: vmerge({v: (nodes[v].get('fields') or {}).get(name) for v in versions}) for name in field_names}
    alts: list = []
    for v in versions:
        if nodes[v].get('rules'):
            alts = nodes[v]['rules']
    armed = [v for v in versions if nodes[v].get('rules')]
    if alts and all(len(nodes[v]['rules']) == len(alts) and [a.get('type') for a in nodes[v]['rules']] == [a.get('type') for a in alts]
                    for v in armed):
        # the same alternatives in every version that has them (a host prototype interface's details, CHostPrototype.php):
        # merged arm by arm, so a field one version's arm adds (8.0's retries) is that version's only
        alts = [vmerge({v: nodes[v]['rules'][i] for v in armed}) for i in range(len(alts))]
    if alts:
        out['rules'] = alts
    return out


# ----------------------------------------------------------------------------------------------------------------------
# the description


STRING_TYPES = {
    'string_utf8', 'escaped_string_utf8', 'hg_name', 'h_name', 'tg_name', 'script_menu_path', 'user_macro',
    'time_period', 'regex', 'variable_name', 'time_unit', 'url', 'color', 'numeric', 'lld_macro', 'psk',
    'calc_formula', 'ip', 'dns', 'port', 'trigger_expression', 'event_name', 'date', 'numeric_ranges', 'uuid',
    'vault_secret', 'ip_ranges', 'image', 'exec_params', 'cond_formula', 'cond_formulaid', 'int32_ranges',
    'lat_lng_zoom', 'item_key', 'item_delay', 'json', 'xml', 'preproc_params', 'prometheus_pattern',
    'prometheus_label', 'host_address', 'number', 'frontend_action', 'uuid_v7',
}
SCALARS = {'string', 'id', 'int', 'long', 'double', 'ulong'}

COMMON_GET = ['output', 'filter', 'search', 'searchByAny', 'startSearch', 'excludeSearch', 'searchWildcardsEnabled',
              'limit', 'sortfield', 'sortorder', 'countOutput']


class Field:
    def __init__(self, wire: str):
        self.wire = wire
        self.name: str | None = None
        self.kind: str = 'json'
        self.versions: list[str] = []
        self.required = False
        self.deprecated: list[str] = []
        self.length: int | None = None
        self.summary: str | None = None
        self.reason: str | None = None     # why a kind is 'json', when it is
        self.refused = False               # the version that removed it refuses it (its validator is strict)
        self.replacement: str | None = None
        self.secret = False                # a password, key or token (Merger.mark_secrets)
        self.nullable = False              # null is a request of its own (overlay nullMeaningful)

    def to_json(self) -> dict:
        since, removed = since_removed(self.versions)
        out: dict = {'name': self.name, 'wire': self.wire, 'kind': self.kind}
        if self.required:
            out['required'] = True
        if since != VERSIONS[0]:
            out['since'] = since
        if removed:
            out['removed'] = removed
            if self.refused:
                out['refused'] = True
            if self.replacement:
                out['replacement'] = self.replacement
        if self.deprecated:
            out['deprecated'] = self.deprecated[0]
        if self.length:
            out['maxLength'] = self.length
        if self.summary:
            out['summary'] = self.summary
        if self.reason:
            out['json'] = self.reason
        if self.secret:
            out['secret'] = True
        if self.nullable:
            out['nullable'] = True
        return out


class Record:
    def __init__(self, name: str, role: str, summary: str | None, obj: str | None = None, method: str | None = None):
        self.name = name
        self.role = role                # read, get, request, nested-read, nested-request
        self.summary = summary
        self.object = obj
        self.method = method
        self.fields: dict[str, Field] = {}
        self.versions: set[str] = set()
        self.extra: dict = {}
        self.paths: list[str] = []
        self.strict = False   # a request object whose validator refuses a field it does not name

    def signature(self) -> tuple:
        return tuple(sorted((f.wire, f.kind) for f in self.fields.values()))

    def unique_names(self) -> None:
        """Two wire names that would share a C# name (select_alerts beside selectAlerts): the one that is not the
        camel-case form is the alias (SelectAlertsAlias; its wire name stays the attribute's), so both stay reachable and
        neither is renamed silently."""
        # a member named exactly a .NET type (the wire field `object`: an event's, a problem's, an inherited tag's
        # kind of object) reads as a type, not a field (CA1720): it is that object's type, so it is named so; the wire
        # name stays the attribute's
        for f in self.fields.values():
            if f.name in CLR_TYPE_WORDS:
                f.name = f.name + 'Type'
        taken: dict[str, Field] = {}
        for f in sorted(self.fields.values(), key=lambda f: ('_' in f.wire, f.wire)):
            # System.Text.Json binds members ignoring case, so names that differ only in case collide too
            while f.name.lower() in taken:
                f.name = f.name + 'Alias'
            taken[f.name.lower()] = f

    def to_json(self) -> dict:
        self.unique_names()
        out: dict = {'name': self.name, 'role': self.role}
        if self.object:
            out['object'] = self.object
        if self.method:
            out['method'] = self.method
        out['summary'] = self.summary or self.name
        out.update(self.extra)
        out['fields'] = [f.to_json() for f in self.fields.values()]
        return out


def reviewed(entry):
    """A reviewed overlay entry as the description carries it: its notes (a key that starts with _) left out, at any depth."""
    if isinstance(entry, dict):
        return {k: reviewed(v) for k, v in entry.items() if not k.startswith('_')}
    if isinstance(entry, list):
        return [reviewed(v) for v in entry]
    return entry


class Merger:
    def __init__(self, facts: dict[str, dict], defines: dict[str, dict[str, int]], overlay: dict, legacy: dict | None = None,
                 schemas: dict[str, dict] | None = None):
        self.facts = facts
        self.schemas = schemas or {v: f.get('tables', {}) for v, f in facts.items()}
        self.overlay = overlay
        self.enums = Enums(defines, overlay)
        self.records: dict[str, Record] = {}
        self.by_signature: dict[tuple, str] = {}
        self.problems: list[str] = []
        self.legacy = legacy
        self.suggestions: dict = {'objects': {}, 'types': {}, 'enums': {}, 'methods': {}, 'nested': {}}
        self.docs = overlay.get('types', {})
        self.docs_read: set = set()
        self._resolved: dict[str, str] = {}
        self._verbed: set[str] = set()
        self._likes: list[tuple[str, str, str]] = []   # nested records renamed with their verb (HostUpdateMacroRequest)
        self.nested_names = overlay.get('nested', {})
        if legacy is not None:
            self._legacy_types = {t['name']: t for t in legacy['types']}
            self._legacy_enums = {e['name']: e for e in legacy['enums']}

    # ------------------------------------------------------------------------------------------------------------------
    # names and docs

    def object_info(self, obj: str) -> dict:
        info = self.overlay.get('objects', {}).get(obj)
        if info is None:
            stem = pascal(obj)
            info = {'stem': stem, 'model': stem, 'property': plural(stem)}
            self.problems.append(f'object {obj}: no overlay entry, named {stem}')
        return info

    def model_doc(self, obj: str, wire: str) -> dict:
        """The object's read model's sentence for the same wire field: a request's hostid is the host's hostid."""
        model = (self.overlay.get('objects', {}).get(obj) or {}).get('model')
        return self.doc(model, wire) if model else {}

    def doc(self, record: str, wire: str | None = None) -> dict:
        entry = self.docs.get(record) or {}
        if record in self.docs:
            self.docs_read.add(record if wire is None else (record, wire))
        if wire is None:
            return entry
        return (entry.get('fields') or {}).get(wire) or {}

    def stale_docs(self) -> list[str]:
        """The overlay's type entries (and field entries) no record read: a record the description no longer has, or
        a field it no longer takes. Stale, like a recorded verify exception that no longer occurs."""
        out = []
        for record, entry in sorted(self.docs.items()):
            if record.startswith('_'):
                continue
            used_fields = {w for k in self.docs_read if isinstance(k, tuple) and k[0] == record for w in k[1:]}
            if record not in self.docs_read and not used_fields:
                out.append(f'types.{record}')
                continue
            out.extend(f'types.{record}.fields.{w}' for w in sorted((entry.get('fields') or {})) if w not in used_fields)
        return out

    # ------------------------------------------------------------------------------------------------------------------
    # kinds

    def kind(self, rule: dict, where: str, record: str, wire: str, role: str, obj: str, tables: dict) -> tuple[str, str | None]:
        """A merged rule as a generator kind (and the reason, when the kind is 'json')."""
        t = rule.get('type')
        if t == 'multiple':
            # an arm that forbids the field (API_UNEXPECTED) or accepts anything says nothing about its type
            # and so does one that only takes an empty list (['type' => API_OBJECTS, 'length' => 0], CCepRule.php 8.0:599):
            # an empty list is a list of the other arm's elements
            alts = [a for a in (rule.get('rules') or []) if a.get('type') not in ('unexpected', 'any')
                    and not (a.get('type') == 'objects' and a.get('length') == 0 and not a.get('fields'))] or (rule.get('rules') or [])
            object_alts = [a for a in alts if a.get('type') in ('object', 'objects')]
            kinds = []
            for alt in alts:
                if alt.get('type') in ('object', 'objects'):
                    # never typed arm by arm: that would name a record from an unmerged arm (see the merged path below)
                    kinds.append('list:object:*' if alt.get('type') == 'objects' else 'object:*')
                    continue
                k, _ = self.kind(alt | {'versions': rule.get('versions'), 'values': {}, 'names': {}}, where, record, wire, role, obj, tables)
                kinds.append(k)
            if object_alts and len(object_alts) == len(alts):
                merged = union_alternatives(object_alts)
                versions = rule.get('versions') or VERSIONS
                # a version the alternatives were merged across sees only its own fields (vmerge, arm by arm)
                armed = {v for a in object_alts for v in (a.get('versions') or [])}
                return self.kind(vmerge({v: _at(merged, v) if v in armed else merged for v in versions}), where, record, wire, role, obj, tables)
            distinct = sorted(set(kinds) - {'json'})
            if len(distinct) > 1 and set(distinct) <= SCALARS and 'string' in distinct:
                # a scalar the API reads as text or a number depending on another field: text carries every one
                return ('string', None)
            if len(distinct) == 1:
                k = distinct[0]
                if k == 'int' or k.startswith('enum:') or k == 'bool':
                    k2 = self.int_kind(rule, where, role in ('read', 'get', 'request'))
                    return (k2, None)
                return (k, None)
            if not distinct:
                return ('json', 'every alternative is untyped')
            return ('json', 'one of ' + ', '.join(distinct) + ' depending on another field')
        if t in ('object', 'objects'):
            fields = rule.get('fields')
            if not fields:
                return ('json' if t == 'object' else 'list:json', 'an object the API validates without naming its fields')
            name = self.nested(record, wire, rule, role, obj, tables)
            return (('object:' if t == 'object' else 'list:object:') + name, None)
        if t in ('id', 'selementid'):
            return ('id', None)
        if t == 'ids':
            return ('list:id', None)
        # A CUID (API_CUID, ZBX_TYPE_CUID: auditlog's auditid and recordsetid, ha_node's ha_nodeid) is a 25-character
        # string, never a number: it cannot be a ZabbixId.
        if t == 'cuid':
            return ('string', None)
        if t == 'cuids':
            return ('list:string', None)
        if t == 'int32':
            return (self.int_kind(rule, where, role in ('read', 'get', 'request')), None)
        if t == 'ints32':
            k = self.int_kind(rule, where, role in ('read', 'get', 'request'))
            return ('list:' + ('int' if k == 'bool' else k), None)
        if t == 'uint64':
            return ('ulong', None)
        if t == 'uints64':
            return ('list:ulong', None)
        if t == 'float':
            return ('double', None)
        if t == 'floats':
            return ('list:double', None)
        if t in ('boolean', 'flag'):
            return ('apibool', None)
        if t == 'timestamp':
            # an instant, unless the validator bounds it within one day ('format' => 'H:i', 'in' => '0:86340'): a time
            # of day in seconds after midnight (a maintenance period's start_time, CMaintenance.php 8.0:336)
            ranges = ((rule.get('in') or {}).get('ranges') or [])
            if ranges and all(r.partition(':')[2].isdigit() and int(r.partition(':')[2]) < 86400 for r in ranges):
                return ('timeofday', None)
            return ('clock', None)
        if t == 'date':
            return ('date', None)
        if t in ('strings_utf8', 'user_macros', 'colors', 'filter_values'):
            return ('list:string', None)
        if t == 'output':
            return ('output', None)
        if t == 'sortorder':
            return ('list:string', None)
        if t == 'filter':
            return ('filter', None)
        if t in STRING_TYPES:
            return ('string', None)
        if t == 'http_post':
            return ('json', 'raw post data as a string, or form fields as a list of {name, value} (API_HTTP_POST)')
        if t == 'jsonrpc_params':
            return ('json', 'JSON-RPC params, any JSON (API_JSONRPC_PARAMS)')
        if t == 'column':
            return (self.column_kind(rule.get('db'), rule, where, role in ('read', 'get', 'request')), None)
        if t == 'convention':
            return self.convention(wire, rule, where, role, obj, tables)
        if t == 'kind':
            # a reviewed generator kind (overlay fields: lists)
            return (rule['kind'], None)
        if t in ('any', 'value', 'unexpected', 'unknown'):
            return ('json', f'the API validates it as {t.upper()}')
        return ('json', f'validator type {t} has no mapping yet')

    BOOLEAN_PAIRS = {('OFF', 'ON'), ('FALSE', 'TRUE'), ('DISABLED', 'ENABLED'), ('NO', 'YES')}

    def boolean_family(self, key: str) -> bool:
        """A family of exactly 0 and 1 named off/on, false/true, disabled/enabled or no/yes is a flag, not an enum:
        ``ZBX_HTTP_VERIFY_HOST_OFF``/``_ON`` is ``bool``. ``ITEM_STATUS_ACTIVE`` = 0 is not (0 means yes)."""
        if key in self.overlay.get('keepEnums', []):
            return False
        fam = self.enums.families[key]
        if fam['prefix'].endswith('_STATUS'):
            # a status is its object's own enum even when it is two-valued (ZBX_CONNECTOR_STATUS_DISABLED = 0)
            return False
        members = {info['value']: define[len(fam['prefix']) + 1:] for define, info in fam['members'].items()}
        return sorted(members) == [0, 1] and (members[0], members[1]) in self.BOOLEAN_PAIRS

    def int_kind(self, rule: dict, where: str, by_name: bool = True) -> str:
        values = rule.get('values') or {}
        names = rule.get('names') or {}
        key = self.enums.resolve(where, values, names, by_name)
        if key:
            return 'bool' if self.boolean_family(key) else 'enum:' + key
        flat = sorted({v for vs in values.values() for v in vs})
        if flat == [0, 1] and self.overlay.get('fieldEnums', {}).get(where) != 'int':
            return 'bool'
        return 'int'

    def column_kind(self, db: str | None, rule: dict, where: str, by_name: bool = True) -> str:
        if db is None:
            return 'json'
        if db == 'id':
            return 'id'
        if db == 'cuid':
            return 'string'
        if db == 'int':
            key = self.enums.resolve(where, {}, {}, by_name) if where else None
            if key:
                return 'bool' if self.boolean_family(key) else 'enum:' + key
            return 'int'
        if db == 'uint':
            return 'ulong'
        if db == 'float':
            return 'double'
        return 'string'

    def convention(self, wire: str, rule: dict, where: str, role: str, obj: str, tables: dict) -> tuple[str, str | None]:
        """An option the older services accept through ``$defOptions`` with no validator: typed by its name and its
        default, as ``CApiService`` reads it."""
        default = rule.get('default')
        low = wire.lower()
        if wire == 'output' or wire.startswith('select'):
            return ('output', None)
        if wire in ('filter', 'search', 'searchInventory'):
            return ('filter', None)
        if wire in ('sortfield', 'sortorder'):
            return ('list:string', None)
        if wire in ('limit', 'limitSelects'):
            return ('int', None)
        if low.endswith('ids') and wire not in ('preservekeys',):
            return ('list:id', None)
        if low.endswith('id'):
            return ('id', None)
        if wire == 'tags':
            name = self.nested_filter_record('TagFilter', {'tag': 'string', 'value': 'string', 'operator': 'int'})
            return ('list:object:' + name, None)
        if wire == 'severities':
            return ('list:int', None)
        if wire in ('evaltype', 'status', 'mfa_status', 'type', 'min_severity', 'value', 'source', 'object'):
            return ('int', None)
        if wire in ('time_from', 'time_till', 'lastChangeSince', 'lastChangeTill'):
            return ('clock', None)
        if wire in ('host', 'group', 'proxy'):
            return ('string', None)
        if wire in ('functions',):
            return ('list:string', None)
        if wire in ('webitems', 'dependent', 'skipDependent', 'globalmacro', 'maintenance', 'withLastEventUnacknowledged'):
            return ('apibool', None)
        if isinstance(default, bool) or wire in ('countOutput', 'groupCount', 'preservekeys', 'editable', 'nopermissions',
                                                 'searchByAny', 'startSearch', 'excludeSearch', 'searchWildcardsEnabled') \
                or low.startswith(('with', 'monitored', 'templated', 'only', 'inherited', 'expand', 'active', 'maintenance')):
            return ('apibool', None)
        if isinstance(default, int):
            return ('int', None)
        if isinstance(default, str):
            return ('string', None)
        if isinstance(default, list):
            return ('list:string', None)
        return ('json', 'an option the service reads without a validator or a telling default')

    def nested_filter_record(self, name: str, fields: dict[str, str]) -> str:
        if name not in self.records:
            r = Record(name, 'nested-request', (self.doc(name).get('summary')) or 'A tag filter: the tag, a value and how to compare them.')
            for wire, kind in fields.items():
                f = Field(wire)
                f.name = self.doc(name, wire).get('name') or pascal(wire)
                f.kind = kind
                f.versions = list(VERSIONS)
                f.summary = self.doc(name, wire).get('summary')
                r.fields[wire] = f
            r.versions = set(VERSIONS)
            self.records[name] = r
        return name

    # ------------------------------------------------------------------------------------------------------------------
    # records

    def build(self, name: str, role: str, rule: dict, obj: str, method: str | None, tables: dict, summary: str | None,
              register: bool = True) -> Record:
        record = self.records.get(name) if register else None
        if record is None:
            record = Record(name, role, summary, obj, method)
            if register:
                self.records[name] = record
        record.versions |= set(rule.get('versions') or [])
        record.strict = role in ('request', 'nested-request') and rule.get('type') in ('object', 'objects') \
            and bool(rule.get('fields')) and 'allow_unexpected' not in (rule.get('flags') or [])
        for wire, frule in (rule.get('fields') or {}).items():
            if wire.startswith('_'):
                continue
            f = record.fields.get(wire) or Field(wire)
            # overlay fieldEnums keys by record path first (HostInterfaceRequest.type), then by object.field
            where = f'{name}.{wire}' if f'{name}.{wire}' in self.overlay.get('fieldEnums', {}) else f'{obj}.{wire}'
            f.kind, f.reason = self.kind(frule, where, name, wire, role, obj, tables)
            f.versions = frule.get('versions') or []
            present = f.versions
            method_versions = rule.get('versions') or []
            # a .get's own API_REQUIRED (hostdashboard.get's hostids, CHostDashboard.php 8.0:33) is required as a request's is
            f.required = role in ('request', 'nested-request', 'get') and bool(present) and set(frule.get('required') or []) >= set(present) and set(present) >= set(method_versions)
            f.deprecated = frule.get('deprecated') or []
            f.length = frule.get('length') if isinstance(frule.get('length'), int) else None
            _, removed = since_removed(present)
            f.refused = bool(removed) and removed in (rule.get('strict') or [])
            f.replacement = frule.get('replacement') if isinstance(frule.get('replacement'), str) else None
            # The record's own entry wins member by member; the object's read model fills what it leaves out (a request's
            # hostid is named and described like the host's hostid), so a sentence written for the request never costs it
            # the reviewed name.
            own, model = self.doc(name, wire), self.model_doc(obj, wire)
            f.name = own.get('name') or model.get('name') or f.name or pascal(wire)
            f.summary = own.get('summary') or model.get('summary') or f.summary
            record.fields[wire] = f
            self.legacy_enum(name, wire, f.kind)
        return record

    def nested(self, parent: str, wire: str, rule: dict, role: str, obj: str, tables: dict) -> str:
        nested_role = 'nested-read' if role in ('read', 'nested-read') else 'nested-request'
        path = f'{parent}.{wire}'
        if path in self._resolved:
            return self._resolved[path]
        name = self.nested_names.get(path)
        # an overlay-named record is one concept by review: every path the overlay gives that name shares the record
        # whose shapes agree (a host's tags and an item's tags are one ZabbixTag), what one of them requires unioned away
        shared = name is not None
        if name is None and self.legacy is not None:
            legacy = self.legacy_nested(parent, wire)
            if legacy:
                self.suggestions['nested'][path] = legacy
                name = legacy
        suffix = 'Filter' if role == 'get' or parent.endswith(('GetParameters', 'Filter')) else 'Request'
        if name is None:
            base = re.sub(r'(CreateRequest|UpdateRequest|GetParameters|Request|Parameters|Filter)$', '', parent)
            name = base + pascal(singular(wire))
            if nested_role == 'nested-request' and not name.endswith(suffix):
                name += suffix
        if parent in self._verbed:
            self._verbed.add(name)
        # build unregistered (its own nested records are named from the candidate name) to compare shapes
        scratch = self.build(name, nested_role, rule, obj, None, tables, None, register=False)
        existing = self.records.get(name)
        if existing is None and nested_role == 'nested-request' and parent in self._verbed:
            # a verb's own nested record (HttpTestUpdateStepHeaderRequest) is the shared one when their shapes agree
            plain = re.sub(r'(?<=[a-z])(Update|Create)(?=[A-Z])', '', name, count=1)
            if plain != name and plain in self.records and self.records[plain].role == nested_role and self.compatible(self.records[plain], scratch):
                self.union(self.records[plain], scratch)
                self.records[plain].paths.append(path)
                self._resolved[path] = plain
                return plain
        if existing is not None and existing.role != nested_role:
            name = name + (suffix if nested_role == 'nested-request' else 'Record')
            existing = self.records.get(name)
        if existing is not None:
            if self.compatible(existing, scratch, required=not shared):
                self.union(existing, scratch)
                existing.paths.append(path)
                self._resolved[path] = name
                return name
            # the parent's verb stays in the name (HostUpdateMacroRequest beside host.create's HostMacroRequest)
            alt = re.sub(r'(GetParameters|Request|Parameters|Filter)$', '', parent) + pascal(singular(wire))
            if nested_role == 'nested-request' and not alt.endswith(suffix):
                alt += suffix
            if alt == name or (alt in self.records and not self.compatible(self.records[alt], scratch)):
                alt = f'{alt}{len(self.records)}'
                self.problems.append(f'{path}: shape conflicts with {name}; named {alt}')
            if alt in self.records:
                self.union(self.records[alt], scratch)
                self.records[alt].paths.append(path)
                self._resolved[path] = alt
                return alt
            name = alt
            self._verbed.add(alt)
            # rebuilt under its own name: its nested records were resolved from the candidate's (DashboardPageRequest.widgets
            # is dashboard.create's widget, which requires type; dashboard.update's pages' widgets do not, CDashboard.php 8.0:317)
            scratch = self.build(name, nested_role, rule, obj, None, tables, None, register=False)
        scratch.name = name
        scratch.summary = self.doc(name).get('summary') or f'<c>{wire}</c> of a <see cref="Zabbix{parent}"/>.'
        for f in scratch.fields.values():
            d = self.doc(name, f.wire)
            f.name = d.get('name') or f.name
            f.summary = d.get('summary') or f.summary
        scratch.paths.append(path)
        scratch.object = None
        if parent in self._verbed:
            self._verbed.add(name)
        self.records[name] = scratch
        self._resolved[path] = name
        return name

    @staticmethod
    def compatible(a: Record, b: Record, required: bool = True) -> bool:
        def shape(kind: str) -> str:
            # records unify by shape, and an int and an enum of it are one wire type (the enum is the better name)
            return re.sub(r'enum:.+$', 'int', re.sub(r'object:\w+$', 'object:*', kind))
        for wire, f in b.fields.items():
            g = a.fields.get(wire)
            if g is not None and shape(g.kind) != shape(f.kind) and 'json' not in (g.kind, f.kind):
                return False
        # a strict request object never takes a field its validator refuses: host.create's macros refuse hostmacroid,
        # which host.update's macros need (CHost.php 8.0:1631 vs 2248), so the two are two records, not one union
        # (a field only versions the other record does not span has is no difference: 8.0's interface retries)
        def beyond(x: Record, y: Record) -> set:
            return {w for w in set(x.fields) - set(y.fields)
                    if not y.versions or not x.fields[w].versions or set(x.fields[w].versions) & y.versions}
        if a.strict and beyond(b, a) or b.strict and beyond(a, b):
            return False
        # nor loses what one of them requires (host.create's macros require macro; host.update's do not), unless the
        # overlay names them one record
        if not required:
            return True
        for wire, f in b.fields.items():
            g = a.fields.get(wire)
            if g is not None and g.required != f.required:
                return False
        return True

    @staticmethod
    def union(a: Record, b: Record) -> None:
        for wire, f in b.fields.items():
            g = a.fields.get(wire)
            if g is None:
                f.required = False
                a.fields[wire] = f
            else:
                g.required = g.required and f.required
                g.versions = sorted(set(g.versions) | set(f.versions), key=vkey)
                if g.kind == 'json' and f.kind != 'json' or g.kind == 'int' and f.kind.startswith('enum:'):
                    g.kind, g.reason = f.kind, f.reason
        for wire, g in a.fields.items():
            if wire not in b.fields:
                g.required = False
        a.versions |= b.versions

    def legacy_enum(self, record: str, wire: str, kind: str) -> None:
        """A naming suggestion: the enum name the description given with --legacy has at the same record and wire."""
        if self.legacy is None:
            return
        mm = re.search(r'enum:(.+)$', kind)
        t = self._legacy_types.get(record)
        if mm is None or t is None:
            return
        for f in t['fields']:
            if f['wire'] == wire:
                old = re.search(r'enum:(\w+)$', f['kind'])
                if old:
                    self.suggestions['enums'].setdefault(mm.group(1), {}).setdefault(old.group(1), []).append(f'{record}.{wire}')

    def legacy_nested(self, parent: str, wire: str) -> str | None:
        t = self._legacy_types.get(parent)
        if t is None:
            return None
        for f in t['fields']:
            if f['wire'] == wire:
                m = re.search(r'object:(\w+)$', f['kind'])
                if m:
                    return m.group(1)
        return None

    # ------------------------------------------------------------------------------------------------------------------
    # the whole description

    def run(self) -> dict:
        objects = sorted({o for f in self.facts.values() for o in f['objects']})
        for v in VERSIONS:
            for o, entry in self.facts[v]['objects'].items():
                if entry.get('table'):
                    self.enums.tables_of[o] = entry['table']
        services = []
        self.result_records()
        for obj in objects:
            services.append(self.service(obj))
        self.resolve_likes()
        self.field_kinds()
        self.mark_secrets()
        self.mark_nullable()
        self.note_defaults()
        # records no method signature reaches are not emitted (dead vocabulary)
        reachable = self.reachable(services)
        enums = self.emit_enums(reachable)
        types = [self.records[n].to_json() for n in sorted(self.records) if n in reachable]
        self.check_object_facts(services, reachable)
        references = reviewed(self.overlay.get('references') or {})
        self.check_references(references, objects)
        cmdlets = reviewed(self.overlay.get('cmdlets') or {})
        self.check_cmdlets(cmdlets, services, reachable)
        return {
            '$comment': 'The Zabbix API as the Zabbix source states it, merged across versions by tools/ZabbixApiExtract. '
                        'Do not edit: re-run the tool; names and sentences come from schema/overlay.json.',
            'versions': VERSIONS,
            'sources': {v: self.facts[v].get('zabbix') for v in VERSIONS},
            'enums': enums,
            'types': types,
            'services': services,
            'references': references,
            'cmdlets': cmdlets,
        }

    # ------------------------------------------------------------------------------------------------------------------
    # reviewed facts and the cmdlets' shape, carried from the overlay and checked against the model

    def _request(self, services: list[dict], obj: str, verb: str) -> 'Record | None':
        for s in services:
            if s['object'] == obj:
                for m in s['methods']:
                    if m['api'] == f'{obj}.{verb}':
                        mm = re.search(r'object:(\w+)$', m.get('parameters') or '')
                        return self.records.get(mm.group(1)) if mm else None
        return None

    def _read(self, services: list[dict], obj: str) -> 'Record | None':
        model = next((s.get('model') for s in services if s['object'] == obj), None)
        return self.records.get(model) if model else None

    def check_object_facts(self, services: list[dict], reachable: set[str]) -> None:
        """An overlay object's idField names a field of its read model; its requiredWhen, fields of its create request."""
        for s in services:
            obj = s['object']
            if 'idField' in s:
                read = self._read(services, obj)
                if read is None or s['idField'] not in read.fields:
                    self.problems.append(f'objects.{obj}.idField: {s["idField"]} is not a field of {obj}\'s read model')
            for wire in s.get('requiredWhen', {}):
                create = self._request(services, obj, 'create')
                if create is None or wire not in create.fields:
                    self.problems.append(f'objects.{obj}.requiredWhen: {wire} is not a field of {obj}.create')

    def check_references(self, references: dict, objects: list[str]) -> None:
        """Every object an id refers to is an API object, and so is every owner."""
        known = set(objects)
        for wire, obj in (references.get('ids') or {}).items():
            if obj not in known:
                self.problems.append(f'references.ids.{wire}: {obj} is not an API object')
        for owner, ids in (references.get('owners') or {}).items():
            if owner not in known:
                self.problems.append(f'references.owners.{owner}: not an API object')
            for wire, obj in ids.items():
                if obj not in known:
                    self.problems.append(f'references.owners.{owner}.{wire}: {obj} is not an API object')

    def check_cmdlets(self, cmdlets: dict, services: list[dict], reachable: set[str]) -> None:
        """The cmdlets section names API objects, the fields of their requests and read models, and described records."""
        objects = {s['object'] for s in services}
        for obj, entry in (cmdlets.get('objects') or {}).items():
            if obj not in objects:
                self.problems.append(f'cmdlets.objects.{obj}: not an API object')
                continue
            requests = [r for r in (self._request(services, obj, 'create'), self._request(services, obj, 'update')) if r is not None]
            for key in ('switches', 'valueForms', 'unhide'):
                for wire in entry.get(key, {}):
                    if not any(wire in r.fields for r in requests):
                        self.problems.append(f'cmdlets.objects.{obj}.{key}: {wire} is not a field of {obj}.create or {obj}.update')
            if 'names' in entry and set(entry['names']) != {'display', 'technical'}:
                self.problems.append(f'cmdlets.objects.{obj}.names: must name the display and the technical field and nothing else')
            for role, wire in (entry.get('names') or {}).items():
                if role in ('display', 'technical') and not any(wire in r.fields for r in requests):
                    self.problems.append(f'cmdlets.objects.{obj}.names.{role}: {wire} is not a field of {obj}.create or {obj}.update')
            if 'interfaceShorthand' in entry and not any(entry['interfaceShorthand'] in r.fields for r in requests[:1]):
                self.problems.append(f'cmdlets.objects.{obj}.interfaceShorthand: {entry["interfaceShorthand"]} is not a field of {obj}.create')
            if 'view' in entry:
                read = self._read(services, obj)
                for wire in entry['view']:
                    if read is None or wire not in read.fields:
                        self.problems.append(f'cmdlets.objects.{obj}.view: {wire} is not a field of {obj}\'s read model')
            if 'manyPerId' in entry:
                read = self._read(services, obj)
                if read is None or entry['manyPerId'] not in read.fields:
                    self.problems.append(f'cmdlets.objects.{obj}.manyPerId: {entry["manyPerId"]} is not a field of {obj}\'s read model')
        concepts: set[str] = set()
        for builder in cmdlets.get('builders') or []:
            concept, record = builder.get('concept'), builder.get('record')
            if concept in concepts:
                self.problems.append(f'cmdlets.builders: {concept} is declared twice')
            concepts.add(concept)
            if record not in reachable:
                self.problems.append(f'cmdlets.builders.{concept}: {record} is not a record of the description')
                continue
            fields = self.records[record].fields
            for key in ('positional', 'valueForms', 'ownerSupplied'):
                for wire in builder.get(key) or []:
                    if wire not in fields:
                        self.problems.append(f'cmdlets.builders.{concept}.{key}: {wire} is not a field of {record}')
        gets = [r for r in self.records.values() if r.name in reachable and r.role == 'get']
        for wire in cmdlets.get('getSwitches') or {}:
            if not any(wire in r.fields for r in gets):
                self.problems.append(f'cmdlets.getSwitches.{wire}: no .get takes it')
        for wire in cmdlets.get('getLists') or {}:
            if not any(wire in r.fields and r.fields[wire].kind.startswith('list:enum:') for r in gets):
                self.problems.append(f'cmdlets.getLists.{wire}: no .get takes it as a list of enum values')

    # a password, passphrase, pre-shared key (and its identity), client secret, private key or token: the names the audit
    # log masks (CAudit::MASKED_PATHS) and the secrets it does not audit (a host's ipmi_password and tls_psk, an
    # interface's and a discovery check's SNMPv3 passphrases, a web scenario's http_password, a login's password)
    SECRET_NAME = re.compile(r'(passwd|password|passphrase|psk|psk_identity|secret|private_key)$|(^|_)token$')

    def mark_secrets(self) -> None:
        """Marks every text field that holds a secret: by name, or because the audit log masks it for its object
        (``userdirectory.idp_certificate``). A macro's value is masked only when the macro's type is secret, so it is
        not marked: its text form would hide every plain macro value too."""
        # (usermacro.value is masked only for a secret macro, ZBX_MACRO_TYPE_SECRET: CAudit.php 8.0:376)
        masked = {tuple(p.split('.', 1)) for v in VERSIONS for p in self.facts[v].get('maskedPaths') or [] if p.count('.') == 1} \
            - {('usermacro', 'value')}
        for record in self.records.values():
            for wire, field in record.fields.items():
                if field.kind not in ('string', 'list:string'):
                    continue
                if self.SECRET_NAME.search(wire) or (record.object, wire) in masked:
                    field.secret = True

    def note_defaults(self) -> None:
        """A create's status left out is the column's default, which is not always enabled (a media type is
        created disabled: media_type.status defaults to 1, schema.inc.php). The field's sentence says which it is."""
        enums = {e['key']: e for e in self.enums.families.values()} if isinstance(self.enums.families, dict) else {}
        for record in self.records.values():
            if record.role != 'request' or not (record.method or '').endswith('.create') or 'status' not in record.fields:
                continue
            field = record.fields['status']
            obj = record.method.rpartition('.')[0]
            latest = max((v for v in VERSIONS if obj in self.facts[v]['objects']), key=vkey, default=None)
            table = self.facts[latest]['objects'][obj].get('table') if latest else None
            default = ((self.schemas.get(latest, {}).get(table) or {}).get('status') or {}).get('default') if table else None
            if default is None or not default.lstrip('-').isdigit():
                continue
            value = int(default)
            if field.kind == 'bool':
                said = 'on' if value else 'off'
            elif field.kind.startswith('enum:') and field.kind[5:] in self.enums.families:
                fam = self.enums.families[field.kind[5:]]
                member = next((d for d, info in fam['members'].items() if info['value'] == value), None)
                said = f'<c>{member}</c>' if member else str(value)
            else:
                said = str(value)
            note = f'Left out, the server stores {said} (the <c>{table}.status</c> default).'
            field.summary = ((field.summary or f'Wire field <c>status</c>.').rstrip() + ' ' + note)

    def mark_nullable(self) -> None:
        """The reviewed options whose null is a request of its own (``overlay.json`` ``nullMeaningful``, ``obj.method.wire``)."""
        for key in self.overlay.get('nullMeaningful', {}):
            if key.startswith('_'):
                continue
            api, _, wire = key.rpartition('.')
            records = [r for r in self.records.values() if api in (r.method, f'{r.object}.{r.method}') and wire in r.fields]
            if not records:
                self.problems.append(f'nullMeaningful {key}: no such request field')
            for record in records:
                record.fields[wire].nullable = True

    def resolve_likes(self) -> None:
        for name, wire, like in self._likes:
            source, _, source_wire = like.partition('.')
            field = (self.records.get(source) or Record('', '', None)).fields.get(source_wire)
            if field is None:
                self.problems.append(f'resultRecords {name}.{wire}: like {like} names nothing')
                continue
            self.records[name].fields[wire].kind = re.sub(r'Request$', '', field.kind) if field.kind.startswith(('object:', 'list:object:')) else field.kind

    def result_records(self) -> None:
        """The reviewed records of a method result the source builds by hand (``overlay.json`` ``resultRecords``:
        history.push answers ``{response, data: [{itemid?, error?}]}``, CHistory.php 8.0:459 and the server's
        trapper_history_push.c 8.0:465-545); a method names one through its overlay ``result``."""
        for name, spec in self.overlay.get('resultRecords', {}).items():
            if name.startswith('_'):
                continue
            record = Record(name, spec.get('role', 'read'), spec.get('summary'))
            record.versions = set(spec.get('versions') or VERSIONS)
            for wire, fspec in spec['fields'].items():
                f = Field(wire)
                # 'like': another record field's kind, resolved once the methods are built (resolve_likes)
                f.kind = fspec.get('kind') or 'json'
                if fspec.get('like'):
                    self._likes.append((name, wire, fspec['like']))
                f.reason = fspec.get('json')
                f.name = fspec.get('name') or pascal(wire)
                f.summary = fspec.get('summary')
                f.versions = sorted(record.versions, key=vkey)
                record.fields[wire] = f
            record.extra['extensionData'] = record.role == 'read'
            self.records[name] = record

    def field_kinds(self) -> None:
        """The reviewed kinds of ``overlay.json`` ``fieldKinds`` (``Record.wire``: a generator kind, or ``enum:<family>``)."""
        for key, spec in self.overlay.get('fieldKinds', {}).items():
            if key.startswith('_'):
                continue
            name, _, wire = key.partition('.')
            record = self.records.get(name)
            field = record.fields.get(wire) if record else None
            if field is None and record is not None and record.role in ('read', 'nested-read'):
                # a field the reply carries that the table behind the record does not (a widget's fields)
                field = Field(wire)
                field.versions = sorted(record.versions, key=vkey) or list(VERSIONS)
                d = self.doc(name, wire)
                field.name = d.get('name') or pascal(wire)
                field.summary = d.get('summary')
                record.fields[wire] = field
            if field is None:
                self.problems.append(f'fieldKinds {key}: no such record field')
                continue
            if spec.startswith('enum:') and spec[5:] not in self.enums.families:
                # a family no rule names (a nested read record's code the service only ever writes): the defines the
                # overlay lists (enum:A,B, named so a _DEFAULT member stays), or every define of the prefix (enum:PREFIX)
                defines = spec[5:].split(',')
                prefix = self.enums.common_prefix(defines) if len(defines) > 1 else defines[0]
                version = VERSIONS[-1]
                named = {self.enums.value_of(d): d for d in defines} if len(defines) > 1 else None
                if not prefix or (named is None and len(self.enums.family_members(prefix, version)) < 2):
                    self.problems.append(f'fieldKinds {key}: {spec[5:]} is not a family the description has')
                    continue
                spec = 'enum:' + self.enums.family(prefix, {}, key, exact=False, named=named)
            if spec.startswith(('table:', 'list:table:', 'fields:', 'list:fields:')):
                # a read record of a table's columns, or of named fields, nested under this one
                present = sorted(record.versions, key=vkey) or list(VERSIONS)
                rule = self.extra_rule(spec, present)
                if spec.startswith(('table:', 'list:table:')):
                    table = spec.rpartition(':')[2]
                    rule['fields'] = {c: dict(i, required=[], deprecated=[]) for c, i in self.table_columns(table, present).items()}
                rule['type'] = 'object'
                role = 'read' if record.role in ('read', 'nested-read') else 'request'
                nested = self.nested(name, wire, rule, role, record.object or '', self.tables(present))
                field.kind, field.reason = ('list:object:' if spec.startswith('list:') else 'object:') + nested, None
                continue
            field.kind, field.reason = spec, None

    def per_version(self, obj: str) -> dict[str, dict]:
        return {v: self.facts[v]['objects'][obj] for v in VERSIONS if obj in self.facts[v]['objects']}

    def tables(self, versions: list[str]) -> dict:
        return self.facts[versions[-1]].get('tables', {})

    def service(self, obj: str) -> dict:
        info = self.object_info(obj)
        pv = self.per_version(obj)
        versions = list(pv)
        since, removed = since_removed(versions)
        stem = info['stem']
        model = info.get('model') or stem
        tables = self.tables(versions)
        methods_all: list[str] = []
        for v in versions:
            for m in pv[v]['methods']:
                if m not in methods_all:
                    methods_all.append(m)
        out: dict = {'object': obj, 'client': f'Zabbix{stem}Client', 'property': info.get('property') or plural(stem),
                     'summary': info.get('summary')}
        # The PowerShell help's words for the object (noun, sample name, example arguments), carried as written.
        if info.get('help'):
            out['help'] = info['help']
        # The read model's own id where the delete names none (a host dashboard is the dashboard its dashboardid names),
        # and the fields a run-time rule class requires only under a condition: reviewed facts, checked in check_object_facts.
        if info.get('idField'):
            out['idField'] = info['idField']
        if info.get('requiredWhen'):
            out['requiredWhen'] = reviewed(info['requiredWhen'])
        if since != VERSIONS[0]:
            out['since'] = since
        if removed:
            out['removed'] = removed
        has_read_model = any('output' in pv[v] for v in versions)
        if has_read_model:
            self.read_model(obj, model, pv, tables)
            out['model'] = model
        methods = []
        for m in methods_all:
            methods.append(self.method(obj, m, pv, info, model if has_read_model else None, tables))
        out['methods'] = methods
        return out

    # ------------------------------------------------------------------------------------------------------------------
    # read models

    def read_model(self, obj: str, model: str, pv: dict, tables: dict) -> None:
        versions = [v for v in VERSIONS if v in pv and 'output' in pv[v]]
        record = self.records.get(model) or Record(model, 'read', self.doc(model).get('summary'), obj, obj + '.get')
        record.role = 'read'
        record.object = obj
        self.records[model] = record
        record.versions |= set(versions)
        # the kinds a request gives the same wire field (so a read and a write agree on an enum)
        request_rules: dict[str, dict] = {}
        for method in ('create', 'update'):
            per = {v: (pv[v]['methods'].get(method) or {}).get('params') for v in versions}
            per = {v: p for v, p in per.items() if p}
            if per:
                merged = vmerge(per)
                for wire, rule in (merged.get('fields') or {}).items():
                    request_rules.setdefault(wire, rule)
        field_versions: dict[str, list[str]] = {}
        internal = set(self.overlay.get('internalColumns', {}).get(obj, [])) & {'name_upper'}
        # what the object's own output names in any version, and what its own writes name: a column of its table
        # outside both is another object's or the server's (a template's proxyid is a host's, CHost.php)
        declared = {w for v in versions if pv[v]['output'].get('fieldsFrom') != 'table' for w in pv[v]['output'].get('fields') or []}
        written = self.written(obj)
        not_returned = self.not_fields(obj)
        for v in versions:
            out = pv[v]['output']
            table = pv[v].get('table')
            # a column another object sharing the table writes, and this one does not (CTemplate.php 8.0:252:
            # "Templates share table with hosts and host prototypes"), and one the get takes out of every object
            foreign = self.sibling_columns(obj, v) if out.get('fieldsFrom') == 'table' else set()
            dropped = set(out.get('unset') or []) | not_returned | foreign
            extra: list[str] = []
            if out.get('fieldsFrom') in ('OUTPUT_FIELDS', 'output_fields'):
                # output=extend answers every column of the table, but a column neither the output rule names in any
                # version nor the object's own writes take is another object's (a host's vendor_name is a
                # template's) or a secret the get never returns (a host's tls_psk, CHost.php 8.0:468)
                extra = [c for c in (self.schemas.get(v, {}).get(table) or {}) if not c.endswith('_upper') and c not in internal
                         and (c in declared or c in written) and not (self.SECRET_NAME.search(c) and c not in declared)
                         and c not in self.sibling_columns(obj, v)]
            for wire in list(out.get('fields') or []) + list(out.get('computed') or []) + extra:
                if wire in dropped or wire.endswith('_upper'):
                    continue
                if v not in field_versions.setdefault(wire, []):
                    field_versions[wire].append(v)
        overrides = self.overlay.get('readFields', {}).get(obj, {})
        for wire in overrides:
            # a reviewed field the extraction does not find at all (computed at run time)
            for v in versions:
                if v not in field_versions.setdefault(wire, []):
                    field_versions[wire].append(v)
        side_tables = self.overlay.get('objectTables', {}).get(obj)
        if isinstance(side_tables, str):
            side_tables = [side_tables]
        for v in versions:
            if not pv[v]['output'].get('fields') and side_tables:
                # the service picks its table at run time (history.get by value type, trend.get by trends/trends_uint):
                # the reviewed tables stand in for the one it would name
                for table in side_tables:
                    for wire in self.schemas.get(v, {}).get(table, {}):
                        if v not in field_versions.setdefault(wire, []):
                            field_versions[wire].append(v)
        for wire, present in field_versions.items():
            f = record.fields.get(wire) or Field(wire)
            f.versions = present
            where = f'{obj}.{wire}'
            table = pv[present[-1]].get('table')
            column = self.column(present[-1], table, wire)
            for side in side_tables or []:
                column = column or (self.schemas.get(present[-1], {}).get(side) or {}).get(wire)
            if wire in overrides:
                f.kind, f.reason = self.override_kind(overrides[wire], model, wire, obj, tables, present)
            elif wire in request_rules and request_rules[wire].get('type') in ('object', 'objects') and request_rules[wire].get('fields') \
                    and (column is None or column.get('db') in ('text', 'char')):
                # a column that stores JSON text (query_fields, headers) is the structure the request validates
                f.kind, f.reason = self.kind(request_rules[wire], where, model, wire, 'read', obj, tables)
            elif wire in request_rules and request_rules[wire].get('type') not in ('any', 'convention', 'unknown', 'unexpected', 'object', 'objects', 'multiple'):
                f.kind, f.reason = self.kind(request_rules[wire], where, model, wire, 'read', obj, tables)
            elif wire in request_rules and request_rules[wire].get('type') == 'multiple':
                f.kind, f.reason = self.kind(request_rules[wire], where, model, wire, 'read', obj, tables)
            elif column is not None:
                f.kind = self.column_kind(column.get('db'), {}, where)
                f.reason = None
            elif wire in request_rules and request_rules[wire].get('type') in ('object', 'objects'):
                f.kind, f.reason = self.kind(request_rules[wire], where, model, wire, 'read', obj, tables)
            else:
                f.kind, f.reason = 'json', 'a computed field the source gives no type for'
            if f.kind == 'bool':
                pass
            d = self.doc(model, wire)
            f.name = d.get('name') or pascal(wire)
            f.summary = d.get('summary')
            record.fields[wire] = f
            self.legacy_enum(model, wire, f.kind)
        # the selects: what each adds to the object
        select_versions: dict[str, dict[str, dict]] = {}
        for v in versions:
            for option, sel in (pv[v]['output'].get('selects') or {}).items():
                select_versions.setdefault(option, {})[v] = sel
        for option, per in select_versions.items():
            if option.startswith('select_'):
                # the snake-case aliases (select_acknowledges) fill the same key their camel-case option does
                continue
            sel = per[max(per, key=vkey)]
            wire = (self.overlay.get('selects', {}).get(f'{obj}.{option}') or {}).get('field') or sel.get('field') or option[6:].lower()
            if wire in record.fields and record.fields[wire].kind != 'json' and f'{obj}.{option}' not in self.overlay.get('selects', {}):
                record.fields[wire].versions = sorted(set(record.fields[wire].versions) | set(per), key=vkey)
                continue
            f = record.fields.get(wire) or Field(wire)
            f.versions = sorted(set(f.versions) | set(per), key=vkey)
            if wire in overrides:
                f.kind, f.reason = self.override_kind(overrides[wire], model, wire, obj, tables, sorted(per, key=vkey))
            else:
                f.kind, f.reason = self.select_kind(obj, model, option, wire, sel, pv, tables)
            d = self.doc(model, wire)
            f.name = d.get('name') or pascal(wire)
            f.summary = d.get('summary') or f'What <c>{option}</c> adds.'
            record.fields[wire] = f
        record.extra['extensionData'] = True
        for path in sorted(p for p in not_returned if '.' in p):
            # a nested field the get takes out (settings.get's apm_global_db.password, CSettings.php 8.0:132)
            parent, _, leaf = path.partition('.')
            m = re.search(r'object:(\w+)$', (record.fields.get(parent) or Field(parent)).kind or '')
            nested = self.records.get(m.group(1)) if m else None
            if nested is None or nested.fields.pop(leaf, None) is None:
                self.problems.append(f'notFields {obj}: {path} names nothing')

    def written(self, obj: str, versions: list[str] | None = None) -> set[str]:
        """The top-level fields the object's own create and update name (their rules, allow-lists and the fields their
        code reads), in any of the versions."""
        out: set[str] = set()
        for v in versions or VERSIONS:
            entry = self.facts[v]['objects'].get(obj) or {}
            for m in ('create', 'update'):
                params = (entry.get('methods', {}).get(m) or {}).get('params') or {}
                out |= set(params.get('fields') or {}) - self.not_fields(f'{obj}.{m}')
        return out

    def sibling_columns(self, obj: str, version: str) -> set[str]:
        """The columns of the object's table that another API object stored in the same table writes and this object
        does not: a host's proxyid and ipmi_* in the hosts table a template shares, a trigger prototype's discover in the
        triggers table. Neither a read of this object nor its open request carries them."""
        table = (self.facts[version]['objects'].get(obj) or {}).get('table')
        if not table:
            return set()
        siblings = [o for o, e in self.facts[version]['objects'].items() if o != obj and e.get('table') == table]
        theirs: set[str] = set()
        for o in siblings:
            theirs |= self.written(o)
        return theirs - self.written(obj)

    def external_ignored(self) -> set[str]:
        return {p for v in VERSIONS for p in self.facts[v].get('externalIgnored') or []}

    def not_fields(self, key: str) -> set[str]:
        """The reviewed fields an object's read (``obj``) or a method's request (``obj.method``) does not have
        although a table or a shared validation names them (``overlay.json`` ``notFields``, each with its source)."""
        entry = self.overlay.get('notFields', {}).get(key) or {}
        return set(entry.get('fields') or [])

    def override_kind(self, spec: str, model: str, wire: str, obj: str, tables: dict, present: list[str]) -> tuple[str, str | None]:
        """A reviewed read-field kind: a generator kind, or ``table:<name>`` / ``list:table:<name>`` (a record of that
        table's columns, ids of the owner left out)."""
        many = spec.startswith('list:table:')
        if spec.startswith(('table:', 'list:table:')):
            table = spec.split(':')[-1]
            rule = {'type': 'object', 'versions': present, 'fields': self.table_columns(table, present)}
            name = self.nested(model, wire, rule, 'read', obj, tables)
            return (('list:object:' if many else 'object:') + name, None)
        if spec.startswith(('fields:', 'list:fields:')):
            # a record of named fields (an APM span's events: timestamp, name, attributes, CApmSpan.php 8.0:41)
            rule = dict(self.extra_rule(spec, present), type='object')
            name = self.nested(model, wire, rule, 'read', obj, tables)
            return (('list:object:' if spec.startswith('list:') else 'object:') + name, None)
        if spec.startswith('enum:'):
            key = self.enums.resolve(f'{obj}.{wire}', {}, {}) if spec == 'enum:' else spec[5:]
            return ('enum:' + key, None)
        return (spec, None)

    def table_columns(self, table: str, present: list[str]) -> dict[str, dict]:
        """A table's columns as column rules, each present in the versions whose schema has it (``interface_snmp.retries``
        is 8.0 only, ``schema.inc.php`` 8.0), in the latest version's order, then the older versions' own."""
        out: dict[str, dict] = {}
        for v in sorted(present, key=vkey, reverse=True):
            cols = self.schemas.get(v, {}).get(table) or self.facts[v].get('tables', {}).get(table, {})
            for c, info in cols.items():
                rule = out.setdefault(c, {'type': 'column', 'db': info.get('db'), 'versions': []})
                rule['versions'].insert(0, v)
        # typed by the newest schema that has the column, so the rows of one table are one record whichever versions
        # lend it (interface_snmp.max_repetitions is text from 8.0, where it takes a user macro)
        for v in sorted(VERSIONS, key=vkey, reverse=True):
            for c, info in (self.schemas.get(v, {}).get(table) or {}).items():
                if c in out and not out[c].get('typedBy'):
                    out[c]['db'], out[c]['typedBy'] = info.get('db'), v
        for rule in out.values():
            rule.pop('typedBy', None)
        return out

    def any_column(self, version: str, name: str, hints: list[str]) -> dict | None:
        schema = self.schemas.get(version, {})
        owners = [t for t, cols in schema.items() if name in cols]
        if not owners:
            return None
        owners.sort(key=lambda t: (-sum(1 for h in hints if h and h in t), t))
        return schema[owners[0]][name]

    def column(self, version: str, table: str | None, wire: str) -> dict | None:
        """A read field's column: in the object's table, or in a table the object keeps beside it (``item_rtdata``,
        ``host_rtdata``, ``host_inventory``, ``proxy_rtdata``: the singular table name and a suffix)."""
        schema = self.schemas.get(version, {})
        if not table:
            return None
        if wire in (schema.get(table) or {}):
            return schema[table][wire]
        stem = table[:-1] if table.endswith('s') else table
        for name in sorted(schema):
            if (name.startswith(stem + '_') or name.startswith(table + '_')) and wire in schema[name]:
                return schema[name][wire]
        return None

    def find_table(self, version: str, fields: list[str], hints: list[str]) -> str | None:
        """The table with the most of these columns (at least half), preferring one whose name contains a hint."""
        schema = self.schemas.get(version, {})
        def score(t: str) -> tuple:
            covered = sum(1 for f in fields if f in schema[t])
            return (-covered, -sum(1 for h in hints if h and h in t), len(schema[t]), t)
        ranked = sorted(schema, key=score)
        if not ranked:
            return None
        best = ranked[0]
        return best if sum(1 for f in fields if f in schema[best]) * 2 >= len(fields) else None

    def select_kind(self, obj: str, model: str, option: str, wire: str, sel: dict, pv: dict, tables: dict) -> tuple[str, str | None]:
        many = sel.get('many')
        if many is None:
            many = option.endswith('s') or option.endswith('S')
        target = sel.get('object')
        forced = self.overlay.get('selects', {}).get(f'{obj}.{option}')
        if forced:
            target = forced.get('object', target)
            many = forced.get('many', many)
        if target:
            info = self.overlay.get('objects', {}).get(target)
            target_model = (info or {}).get('model') or pascal(target)
            if any(target in self.facts[v]['objects'] and 'output' in self.facts[v]['objects'][target] for v in VERSIONS):
                return (('list:object:' if many else 'object:') + target_model, None)
        fields = sel.get('in')
        table = (forced or {}).get('table') or sel.get('table')
        latest = max(pv, key=vkey)
        if not table and fields:
            own = pv[latest].get('table') or obj
            table = self.find_table(latest, fields, [singular(option[6:].lower()), own[:-1] if own.endswith('s') else own])
        columns = self.schemas.get(latest, {}).get(table, {}) if table else {}
        if forced and forced.get('fields'):
            # a reviewed field list is the shape (map lines: sysmap_shape rows under their own names, CMap.php 8.0:3778)
            fields = list(forced['fields'])
        elif forced and forced.get('table') and not fields:
            # a reviewed table is the shape: every column of it, when the option's own rule lists no fields; a list
            # (selectInheritedTags 'in' tag, value, object, objectid, CHost.php 8.0:548) is the shape, typed by the table
            fields = list(columns)
        if not fields and columns:
            fields = list(columns)
        if fields:
            rule_fields = {}
            words = [w.lower() for w in re.findall(r'[A-Z][a-z]*', option[6:])]
            hints = [singular(option[6:].lower()), obj] + [singular(w) for w in words]
            for name in fields:
                col = columns.get(name) or self.any_column(latest, name, hints)
                rule_fields[name] = {'type': 'column', 'db': col.get('db') if col else None, 'versions': list(VERSIONS)}
            rule = {'type': 'object', 'fields': rule_fields, 'versions': list(VERSIONS)}
            if forced:
                self._resolved.pop(f'{model}.{wire}', None)   # the reviewed shape wins over an earlier guess
            name = self.nested(model, wire, rule, 'read', obj, tables)
            return (('list:object:' if many else 'object:') + name, None)
        return ('json', f'{option} names no object, table or field list the extractor can read')

    # ------------------------------------------------------------------------------------------------------------------
    # methods

    def method(self, obj: str, m: str, pv: dict, info: dict, model: str | None, tables: dict) -> dict:
        present = [v for v in VERSIONS if v in pv and m in pv[v]['methods']]
        per = {v: pv[v]['methods'][m] for v in present}
        latest = per[present[-1]]
        since, removed = since_removed(present)
        api = f'{obj}.{m}'
        ov = self.overlay.get('methods', {}).get(api, {})
        fn = latest.get('function') or m
        out: dict = {'api': api, 'name': ov.get('name') or (fn[0].upper() + fn[1:] + 'Async')}
        if since != VERSIONS[0]:
            out['since'] = since
        if removed:
            out['removed'] = removed
            if ov.get('replacement'):
                out['replacement'] = ov['replacement']
        min_types = {v: per[v].get('minUserType') for v in present if per[v].get('minUserType') is not None}
        if min_types:
            out['minUserType'] = min_types[present[-1]] if present[-1] in min_types else list(min_types.values())[-1]
            if len(set(min_types.values())) > 1:
                out['minUserTypeByVersion'] = min_types
        out['read'] = all(not per[v].get('writes') for v in present)
        out['idempotent'] = out['read'] or bool(ov.get('idempotent'))
        out['requiresAuthentication'] = all(per[v].get('requiresAuthentication', True) for v in present)
        accepts = [v for v in present if per[v].get('acceptsAuthentication')]
        if accepts:
            out['acceptsAuthenticationSince'] = accepts[0]
        if ov.get('emulation'):
            out['emulation'] = ov['emulation']
        out['source'] = latest.get('source')
        params_per = {v: per[v].get('params') for v in present if per[v].get('params')}
        stem = info['stem']
        # the shape
        if m == 'get' and model is not None:
            params = vmerge(params_per) if params_per else {'type': 'object', 'fields': {}, 'versions': present}
            fields = params.get('fields') or {}
            # an option the API client strips from every external call (nopermissions, CLocalApiClient.php 8.0:94), or a
            # reviewed one the get accepts and never applies
            for wire in self.external_ignored() | self.not_fields(api):
                if fields.pop(wire, None) is None and wire in self.not_fields(api):
                    self.problems.append(f'notFields {api}: {wire} names nothing')
            # one object, not a list: a .get that takes nothing but output (settings.get), or whose every other option is
            # one required scalar naming the object (serverinfo.get's serverid, CServerInfo.php 8.0:27-46 answers the
            # object itself); a required id list (hostdashboard.get's hostids) still selects a list
            singleton = set(fields) <= {'output'} or all(
                set(fields[w].get('required') or []) >= set(present) and fields[w].get('type') not in ('ids', 'objects', 'strings_utf8')
                and not w.endswith('ids') for w in set(fields) - {'output'})
            pname = info.get('parameters') or f'{stem}GetParameters'
            record = self.build(pname, 'get', params, obj, api, tables, self.doc(pname).get('summary') or f'What <c>{api}</c> takes.')
            record.extra['getParameters'] = all(w in record.fields for w in COMMON_GET)
            filterable = sorted({x for v in present for x in ((per[v].get('params') or {}).get('fields', {}).get('filter', {}) or {}).get('filterable', [])})
            searchable = sorted({x for v in present for x in ((per[v].get('params') or {}).get('fields', {}).get('search', {}) or {}).get('filterable', [])})
            sortable = sorted({x for v in present for x in (pv[v].get('output') or {}).get('sortable', [])})
            if filterable:
                record.extra['filterable'] = filterable
            if searchable:
                record.extra['searchable'] = searchable
            if sortable:
                record.extra['sortable'] = sortable
            out['shape'] = 'get-object' if singleton else 'get-list'
            out['parameters'] = 'object:' + pname
            out['result'] = ('object:' if singleton else 'list:object:') + model
            out['count'] = 'countOutput' in record.fields
            return out
        if not params_per:
            if m == 'delete' or m.endswith('delete'):
                out['shape'] = 'ids'
                out['parameters'] = 'list:id'
            else:
                out['shape'] = 'call'
                out['parameters'] = 'none'
        else:
            params = vmerge(params_per)
            self.open_fields(obj, m, params, pv, present)
            t = params.get('type')
            if t in ('ids', 'cuids', 'strings_utf8') or (t == 'multiple' and not params.get('fields')):
                out['shape'] = 'ids' if t not in ('cuids', 'strings_utf8') else 'call'
                out['parameters'] = 'list:id' if t not in ('cuids', 'strings_utf8') else 'list:string'
            elif t == 'object' and not (params.get('fields') or {}):
                # validated as an object with no fields (apiinfo.version, user.logout): nothing to send but {}
                out['shape'] = 'call'
                out['parameters'] = 'none'
            elif t in ('object', 'objects') and (params.get('fields') or {}):
                verb = pascal(fn)
                default = f'{stem}{verb}Request'
                rname = ov.get('parameters') or self.doc_request_name(info, m) or default
                self.build(rname, 'request', params, obj, api, tables, self.doc(rname).get('summary') or f'What <c>{api}</c> takes.')
                out['shape'] = 'call'
                out['parameters'] = ('list:object:' if t == 'objects' else 'object:') + rname
                out['batch'] = t == 'objects'
            else:
                out['shape'] = 'call'
                out['parameters'] = 'json'
        ids = latest.get('returnsIds')
        if ov.get('result'):
            out['result'] = ov['result']
        elif ids:
            out['result'] = 'ids:' + ids
        else:
            out['result'] = 'json'
        return out

    def doc_request_name(self, info: dict, m: str) -> str | None:
        return (info.get('requests') or {}).get(m)

    def open_fields(self, obj: str, m: str, params: dict, pv: dict, present: list[str]) -> None:
        """A request whose validation lets unknown keys through (API_ALLOW_UNEXPECTED) accepts the object's table columns
        beyond the rules: they reach DB::insert/update as they are. Plus the reviewed extra fields."""
        flags = params.get('flags') or []
        extra = self.overlay.get('extraFields', {}).get(f'{obj}.{m}', {})
        fields = params.setdefault('fields', {})
        if m in ('create', 'update', 'massupdate'):
            latest = present[-1]
            internal = set(self.overlay.get('internalColumns', {}).get(obj, []))
            table = pv[latest].get('table')
            columns = self.facts[latest].get('tables', {}).get(table, {}) if table else {}
            for v in present:
                for s in pv[v]['methods'][m].get('stores') or []:
                    if s['path'] == '[]' or s['path'] == '':
                        table = s['table']
                        columns = self.facts[v].get('tables', {}).get(table, columns)
            if 'allow_unexpected' in flags or params.get('typedBy') == 'access':
                # (an array_diff_key allow-list, typedBy 'allowed-fields', is the whole input: CHostInterface.php 7.0:290)
                # the table's own key is never an input of a create (media_type.mediatypeid, CMediatype.php 7.0:315)
                pk = next(iter(columns), None) if m == 'create' else None
                # a column the object's get takes out of every object it returns is not the object's (a graph's discover
                # is a graph prototype's, CGraph.php 8.0:260)
                foreign = {c for v in present for c in ((pv[v].get('output') or {}).get('unset') or [])}
                for col, info in columns.items():
                    if col in fields or col in internal or col.endswith('_upper') or col == pk or col in foreign:
                        continue
                    fields[col] = {'type': 'column', 'db': info.get('db'), 'versions': present, 'required': [], 'deprecated': [], 'length': info.get('length') if info.get('db') in ('char',) else None}
            for name, rule in list(fields.items()):
                # a field an allow-list or a hand-written check names (8.0 CDRule, CDRule.php:205) is typed by its column
                if rule.get('type') == 'convention' and name in columns:
                    fields[name] = dict(rule, type='column', db=columns[name].get('db'))
        for name, spec in extra.items():
            if '.' in name:
                continue
            # the reviewed shape replaces the extracted one; what the source requires of the field itself stays
            kept = (fields.get(name) or {}).get('required') or []
            fields[name] = dict(self.extra_rule(spec, present), required=kept)
        # an open nested object the service stores whole in a table of its own (map selements → sysmaps_elements,
        # CMapElement::createSelements): that table's columns beside the ones its validation names
        for path, spec in (self.overlay.get('nestedTables', {}).get(f'{obj}.{m}') or {}).items():
            node = self._node(params, path)
            if node is None:
                self.problems.append(f'{obj}.{m}: nestedTables path {path} names nothing')
                continue
            if 'allow_unexpected' not in (node.get('flags') or []):
                self.problems.append(f'{obj}.{m}: nestedTables path {path} is strict; its validation already names its fields')
                continue
            columns = self.schemas.get(present[-1], {}).get(spec['table'], {})
            pk = next(iter(columns), None)
            nf = node.setdefault('fields', {})
            for col, info in columns.items():
                if col == pk or col in nf or col in (spec.get('exclude') or []):
                    continue
                nf[col] = {'type': 'column', 'db': info.get('db'), 'versions': present, 'required': [], 'deprecated': [],
                           'length': info.get('length') if info.get('db') == 'char' else None}
        for name, spec in extra.items():
            if '.' in name:
                parent, _, leaf = name.rpartition('.')
                node = self._node(params, parent)
                if node is None:
                    self.problems.append(f'{obj}.{m}: extraFields path {name} names nothing')
                    continue
                node.setdefault('fields', {})[leaf] = self.extra_rule(spec, present)
        for path in sorted(self.not_fields(f'{obj}.{m}')):
            # a reviewed field the request does not take although a table or a shared validation names it
            parent, _, leaf = path.rpartition('.')
            node = self._node(params, parent) if parent else params
            if node is None or leaf not in (node.get('fields') or {}):
                self.problems.append(f'notFields {obj}.{m}: {path} names nothing')
                continue
            del node['fields'][leaf]
        if m == 'create':
            # a create never requires the object's own id, whatever a shared check_db_fields said in an update branch
            table = pv[present[-1]].get('table')
            pk = ((self.schemas.get(present[-1], {}).get(table) or {}) and next(iter(self.schemas[present[-1]][table]), None)) if table else None
            if pk and pk in fields:
                fields[pk] = dict(fields[pk], required=[])

    @staticmethod
    def _node(params: dict, path: str) -> dict | None:
        node = params
        for part in path.split('.'):
            node = (node.get('fields') or {}).get(part)
            if node is None:
                return None
        return node

    def extra_rule(self, spec: str, present: list[str]) -> dict:
        """A reviewed extra field: ``list:<object>.<method>`` (the element of another method's request),
        ``table:<name>`` (an object of a table's columns) or ``list:table:<name>`` (a list of them)."""
        if spec.startswith('list:') and '.' in spec:
            obj, _, method = spec[5:].partition('.')
            per = {v: (self.facts[v]['objects'].get(obj, {}).get('methods', {}).get(method) or {}).get('params') for v in present}
            per = {v: p for v, p in per.items() if p}
            if per:
                merged = vmerge(per)
                self.open_fields(obj, method, merged, self.per_version(obj), [v for v in present if v in per])
                merged = dict(merged)
                merged['type'] = 'objects'
                # a reviewed extra field lends the other method's shape, never its required flags
                merged['fields'] = {k: dict(v, required=[]) for k, v in (merged.get('fields') or {}).items()}
                return merged
        if spec.startswith('field:'):
            # a field of another method's request, as that method validates it (map.update's shapes are map.create's,
            # checked on the zbx_array_diff of the old and the new, CMap.php 8.0:3097-3110), its required flags dropped
            source, _, wire = spec[6:].partition('/')
            obj, _, method = source.partition('.')
            per = {v: ((self.facts[v]['objects'].get(obj, {}).get('methods', {}).get(method) or {}).get('params') or {}).get('fields', {}).get(wire)
                   for v in present}
            per = {v: p for v, p in per.items() if p}
            if per:
                merged = vmerge(per)
                return _unrequired(merged)
            self.problems.append(f'extraFields {spec}: names nothing')
        if spec in ('id', 'string', 'int'):
            return {'type': {'id': 'id', 'string': 'string_utf8', 'int': 'int32'}[spec], 'versions': present, 'required': [], 'deprecated': []}
        if spec.startswith(('table:', 'list:table:')):
            table = spec.rpartition(':')[2]
            cols = self.table_columns(table, present)
            return {'type': 'objects' if spec.startswith('list:') else 'object', 'versions': present, 'required': [], 'deprecated': [],
                    'fields': {c: dict(i, required=[], deprecated=[]) for c, i in cols.items() if not c.endswith('id')}}
        if spec.startswith(('fields:', 'list:fields:')):
            # named fields of named kinds (a map element's elements: one of hostid, groupid, sysmapid, triggerid by
            # the element's type, CMapElement.php 8.0:110-170)
            # a kind ending in ! is required (graph.create's gitems: itemid and color, CGraphGeneral.php 8.0:603,855)
            # any other kind is a generator kind as it is (an APM span's events: attributes=map:string, CApmSpan.php 8.0:41;
            # an enum by its family, enum:<family>)
            kinds = dict(part.split('=', 1) for part in spec.partition('fields:')[2].split(','))
            types = {'id': 'id', 'string': 'string_utf8', 'int': 'int32'}

            def typed(k: str) -> dict:
                base = k.rstrip('!')
                return {'type': types[base]} if base in types else {'type': 'kind', 'kind': base}
            return {'type': 'objects' if spec.startswith('list:') else 'object', 'versions': present, 'required': [], 'deprecated': [],
                    'fields': {n: dict(typed(k), versions=present, required=present if k.endswith('!') else [],
                                       deprecated=[]) for n, k in kinds.items()}}
        return {'type': 'any', 'versions': present, 'required': [], 'deprecated': []}

    # ------------------------------------------------------------------------------------------------------------------
    # reachability and enums

    def reachable(self, services: list[dict]) -> set[str]:
        roots: set[str] = set()
        for s in services:
            if s.get('model'):
                roots.add(s['model'])
            for m in s['methods']:
                for k in (m.get('parameters'), m.get('result')):
                    if isinstance(k, str):
                        mm = re.search(r'object:(\w+)$', k)
                        if mm:
                            roots.add(mm.group(1))
                        elif k in self.records:
                            roots.add(k)
        seen: set[str] = set()
        stack = list(roots)
        while stack:
            name = stack.pop()
            if name in seen or name not in self.records:
                continue
            seen.add(name)
            for f in self.records[name].fields.values():
                mm = re.search(r'object:(\w+)$', f.kind)
                if mm:
                    stack.append(mm.group(1))
        return seen

    def emit_enums(self, reachable: set[str]) -> list[dict]:
        used: set[str] = set()
        for name in reachable:
            for f in self.records[name].fields.values():
                mm = re.search(r'enum:(.+)$', f.kind)
                if mm:
                    used.add(mm.group(1))
        out = []
        names_taken: dict[str, str] = {}
        rename: dict[str, str] = {}
        for key in sorted(used):
            fam = self.enums.families[key]
            ov = self.overlay.get('enums', {}).get(key, {})
            name = ov.get('name')
            if name is None:
                if fam['broad']:
                    where = fam['fields'][0]
                    obj, _, field = where.partition('.')
                    name = pascal(self.object_info(obj)['stem']) + pascal(field)
                elif '::' in fam['prefix']:
                    # CCepRuleHelper::WINDOW → CepRuleWindow: the class (less its C and a Helper suffix) and the family
                    owner, _, family = fam['prefix'].partition('::')
                    name = re.sub(r'Helper$', '', re.sub(r'^C(?=[A-Z])', '', owner)) + const_pascal(family)
                else:
                    name = const_pascal(re.sub(r'^ZBX_', '', fam['prefix']))
                self.suggestions.setdefault('enumDefaults', {})[key] = name
            if name in names_taken:
                self.problems.append(f'enum name {name} taken by {names_taken[name]} and {key}')
                name = name + const_pascal(re.sub(r'\W', '_', key).split('_')[-1] or 'Other')
            names_taken[name] = key
            rename[key] = name
            # two defines with one value are one member: an alias, or a define renamed between versions
            # (SMTP_AUTHENTICATION_NORMAL → _PASSWORD in 7.4, EXPRESSION_TYPE_* → REGEX_TYPE_* in 8.0). The member is
            # named by the newest define and present in every version any of them is.
            by_value: dict[int, list[tuple[str, dict]]] = {}
            for define, info in fam['members'].items():
                by_value.setdefault(info['value'], []).append((define, info))
            unique = []
            for value in sorted(by_value):
                group = sorted(by_value[value], key=lambda di: (tuple(-x for x in max(vkey(v) for v in di[1]['versions'])), di[0]))
                define, info = group[0]
                versions = sorted({v for _, i in group for v in i['versions']}, key=vkey)
                suffix = info.get('suffix') or (define[len(fam['prefix']) + 1:] if define.startswith(fam['prefix'] + '_') else define)
                mname = (ov.get('members') or {}).get(suffix) or const_pascal(suffix)
                since, removed = since_removed(versions)
                entry = {'name': mname, 'value': value, 'define': define}
                # overlay 'memberSummaries': a member's own sentence, keyed like 'members' by what follows the prefix
                if (ov.get('memberSummaries') or {}).get(suffix):
                    entry['summary'] = ov['memberSummaries'][suffix]
                if since != VERSIONS[0]:
                    entry['since'] = since
                if removed:
                    entry['removed'] = removed
                if len(group) > 1:
                    entry['aliases'] = sorted(d for d, _ in group[1:])
                unique.append(entry)
            seen_names: set[str] = set()
            for entry in unique:
                if entry['name'] in seen_names:
                    entry['name'] = entry['name'] + str(entry['value'])
                seen_names.add(entry['name'])
            flags = bool(fam.get('flags')) or bool(ov.get('flags'))
            # overlay 'none': a bit set whose empty set the API accepts but no define names (passwd_check_rules 0): the
            # zero member a [Flags] enum has (CA1008), with the overlay's sentence
            if flags and ov.get('none') and all(entry['value'] != 0 for entry in unique):
                unique.insert(0, {'name': 'None', 'value': 0, 'summary': ov['none']})
            # overlay 'flags': a family the source combines as bits (ZBX_FLAG_DISCOVERY_RULE | _PROTOTYPE | _CREATED)
            out.append({'name': name, 'key': key, 'family': fam['prefix'], 'flags': flags, 'summary': ov.get('summary') or f'<c>{fam["prefix"]}_*</c> (<c>defines.inc.php</c>).',
                        'fields': sorted(set(fam['fields'])), 'members': unique})
        # rewrite the kinds from keys to names
        for record in self.records.values():
            for f in record.fields.values():
                mm = re.search(r'enum:(.+)$', f.kind)
                if mm and mm.group(1) in rename:
                    f.kind = f.kind[:mm.start(1)] + rename[mm.group(1)]
        for t in out:
            t.pop('key', None)
        # generated file names compare ignoring case: an enum and a record may not differ only in case
        records = {n.lower(): n for n in self.records}
        for t in out:
            if t['name'].lower() in records:
                self.problems.append(f"enum {t['name']} and record {records[t['name'].lower()]} differ only in case: name the enum in overlay.json")
        return out
