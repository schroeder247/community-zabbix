# This Source Code Form is subject to the terms of the Mozilla Public
# License, v. 2.0. Copyright (c) 2026 schroeder247. SPDX-License-Identifier: MPL-2.0
"""One Zabbix version's API as its source states it: objects, methods, the rules each method validates its input with,
what a ``.get`` returns, who may call what, and which methods write. Everything here is read from the PHP; nothing is
typed in by hand (the reviewed overrides are applied later, in merge.py)."""
from __future__ import annotations

import json
import os
import re
from typing import Any

from .interp import ApiRef, Interpreter, merge_rule_alt
from .php import Node, PStr, Sym, Unknown, is_unknown, line_of, tokenize

# ----------------------------------------------------------------------------------------------------------------------
# the validator vocabulary


class Vocabulary:
    """The ``API_*`` validator types and flags of one version, read from ``defines.inc.php``."""

    def __init__(self, interp: Interpreter):
        with open(os.path.join(interp.ui, 'defines.inc.php'), encoding='utf-8', errors='replace') as handle:
            text = handle.read()
        start = text.index("define('API_MULTIPLE'")
        flags_at = text.index('// flags', start)
        end = text.index('\n\n', text.index("define('API_", flags_at))
        self.types: dict[int, str] = {}
        for name in re.findall(r"define\('(API_\w+)'", text[start:flags_at]):
            value = interp.define(name)
            if isinstance(value, int):
                self.types[int(value)] = name[4:].lower()
        self.flags: dict[str, int] = {}
        for name in re.findall(r"define\('(API_\w+)'", text[flags_at:end]):
            value = interp.define(name)
            if isinstance(value, int):
                self.flags[name[4:].lower()] = int(value)
        assert self.flags.get('required') == 1, 'API_REQUIRED moved; interp.REQUIRED_FLAG must follow it'

    def type_name(self, value: Any) -> str:
        if isinstance(value, int) and not isinstance(value, bool):
            return self.types.get(int(value), f'unknown:{int(value)}')
        return 'unknown'

    def flag_names(self, value: Any) -> list[str]:
        if not isinstance(value, int) or isinstance(value, bool):
            return []
        unsure = getattr(value, 'unsure', 0)   # a bit only some paths set (interp.MaybeFlags) is not a flag of the rule
        return [name for name, bit in self.flags.items() if value & bit and not unsure & bit]


# ----------------------------------------------------------------------------------------------------------------------
# rules → a plain, reviewable structure


def _plain(value: Any) -> Any:
    """A PHP value as JSON: dicts with 0..n keys become lists, unknowns become their description."""
    if isinstance(value, dict):
        if value and all(isinstance(k, int) for k in value) and sorted(value) == list(range(len(value))):
            return [_plain(v) for v in value.values()]
        return {str(k): _plain(v) for k, v in value.items()}
    if is_unknown(value):
        return None
    if isinstance(value, ApiRef):
        return None
    if isinstance(value, Sym):
        return int(value)
    if isinstance(value, PStr):
        return str(value)
    return value


def in_list(value: Any) -> dict | None:
    """An ``in`` constraint: a set of values (with the constants they came from, when known) or ranges."""
    if value is None or is_unknown(value):
        return None
    if isinstance(value, (int,)) and not isinstance(value, bool):
        return {'values': [int(value)], 'names': {str(int(value)): value.name} if isinstance(value, Sym) else {}}
    if not isinstance(value, str):
        return None
    syms = value.syms if isinstance(value, PStr) else {}
    values: list = []
    ranges: list = []
    strings: list = []
    for part in [p for p in str(value).split(',')]:
        part = part.strip()
        if re.fullmatch(r'-?\d+', part):
            values.append(int(part))
        elif re.fullmatch(r'-?\d*:-?\d*', part):
            ranges.append(part)
        else:
            strings.append(part)
    out: dict = {}
    if values:
        out['values'] = values
        names = {str(v): syms[v] for v in values if v in syms}
        if names:
            out['names'] = names
    if ranges:
        out['ranges'] = ranges
    if strings:
        out['strings'] = strings
    return out or None


class Normalizer:
    def __init__(self, vocab: Vocabulary):
        self.vocab = vocab

    def rule(self, rule: Any) -> dict:
        if not isinstance(rule, dict):
            return {'type': 'unknown'}
        t = self.vocab.type_name(rule.get('type'))
        out: dict = {'type': t}
        flags = self.vocab.flag_names(rule.get('flags'))
        if flags:
            out['flags'] = flags
        constraint = in_list(rule.get('in'))
        if constraint:
            out['in'] = constraint
        length = rule.get('length')
        if isinstance(length, int) and not isinstance(length, bool):
            out['length'] = int(length)
        replacement = rule.get('replacement')
        if isinstance(replacement, str) and replacement:
            out['replacement'] = replacement      # API_DEPRECATED's own pointer (CHostGroup.php 7.0:74)
        if 'default' in rule and not is_unknown(rule['default']):
            out['default'] = _plain(rule['default'])
        fields = rule.get('fields')
        if isinstance(fields, dict) and t in ('object', 'objects'):
            out['fields'] = {str(k): self.rule(v) for k, v in fields.items() if not is_unknown(v)}
        elif isinstance(fields, dict) and t == 'filter':
            out['filterable'] = [str(v) for v in fields.values() if isinstance(v, str)]
        rules = rule.get('rules')
        if isinstance(rules, dict) and t == 'multiple':
            alts = []
            for alt in rules.values():
                if not isinstance(alt, dict):
                    continue
                n = self.rule(alt)
                cond = alt.get('if')
                if isinstance(cond, dict):
                    n['if'] = {'field': cond.get('field') if isinstance(cond.get('field'), str) else None,
                               'in': in_list(cond.get('in'))}
                elif alt.get('else'):
                    n['else'] = True
                alts.append(n)
            out['rules'] = alts
        return out


# ----------------------------------------------------------------------------------------------------------------------
# composing the captures of one method into its input


_SPECIFICITY = {'any': 0, 'unknown': 0, 'unexpected': 0}


def _merge_seq(a: dict, b: dict) -> dict:
    """Two validations that both run on the same input: every field of either; REQUIRED when either requires; the more
    specific type wins over API_ANY."""
    if _SPECIFICITY.get(b.get('type'), 1) > _SPECIFICITY.get(a.get('type'), 1):
        base, other = dict(b), a
    else:
        base, other = dict(a), b
    flags = list(dict.fromkeys((a.get('flags') or []) + (b.get('flags') or [])))
    if 'allow_unexpected' in flags and not ('allow_unexpected' in (a.get('flags') or []) and 'allow_unexpected' in (b.get('flags') or [])):
        # one strict validation of the same input means the input is strict
        flags.remove('allow_unexpected')
    base['flags'] = flags   # (always: an emptied list must not leave the first rule's flags standing)
    for key in ('in', 'length', 'default', 'rules', 'filterable', 'replacement'):
        if key not in base and key in other:
            base[key] = other[key]
    if 'fields' in a or 'fields' in b:
        fields = dict(base.get('fields') or {})
        for name, rule in (other.get('fields') or {}).items():
            fields[name] = _merge_seq(fields[name], rule) if name in fields else rule
        base['fields'] = fields
    return base


def _in_union(a: dict | None, b: dict | None) -> dict | None:
    if a is None or b is None:
        return None   # one path takes any value
    out: dict = {}
    for key in ('values', 'ranges', 'strings'):
        merged = list(dict.fromkeys((a.get(key) or []) + (b.get(key) or [])))
        if merged:
            out[key] = merged
    names = {**(a.get('names') or {}), **(b.get('names') or {})}
    if names:
        out['names'] = names
    return out or None


def _merge_alt(a: dict, b: dict) -> dict:
    """Two validations of the same input on different paths (the arms of a branch the interpreter could not decide, or
    the code past an arm that left early: ``CCepRule::validateWindow``, 8.0:503-549, validates a window of no type and
    every other type apart). What the method accepts is either: the values of either ('in' is the union), a field either
    names; REQUIRED and NOT_EMPTY only when both require it."""
    if _SPECIFICITY.get(a.get('type'), 1) == 0:
        return dict(b)
    if _SPECIFICITY.get(b.get('type'), 1) == 0:
        return dict(a)
    out = dict(a)
    fa, fb = a.get('flags') or [], b.get('flags') or []
    both = {'required', 'not_empty'}
    out['flags'] = [f for f in dict.fromkeys(fa + fb) if f not in both or (f in fa and f in fb)]
    if not out['flags']:
        out.pop('flags')
    if 'in' in a or 'in' in b:
        merged_in = _in_union(a.get('in'), b.get('in'))
        if merged_in:
            out['in'] = merged_in
        else:
            out.pop('in', None)
    if 'length' in a or 'length' in b:
        # unbounded only when a path takes any text: one that takes listed values ('in' => DB::getDefault(…),
        # CCepRule.php 8.0:556) or bounds it in its arms (API_MULTIPLE) does not widen the other's length
        if any('length' not in x and not _fixed(x) and x.get('type') not in ('multiple', 'any', 'unknown') for x in (a, b)):
            out.pop('length', None)
        else:
            out['length'] = max(x['length'] for x in (a, b) if 'length' in x)
    if a.get('type') != b.get('type') and (_fixed(a) or _fixed(b)) and not (_fixed(a) and _fixed(b)):
        # one path takes only its listed values (a window of no type: duration '10m', CCepRule.php 8.0:556), the other
        # a type of its own (API_TIME_UNIT): the other's rule is the field's, those values being among what it takes
        wide = b if _fixed(a) else a
        return {**wide, **{k: out[k] for k in ('flags', 'length') if k in out}} if out.get('flags') or 'length' in out else dict(wide)
    if a.get('type') != b.get('type'):
        # two types on two paths: an API_MULTIPLE of both, which the merge types as it types any other
        arms = _distinct((a.get('rules') if a.get('type') == 'multiple' else [a]) + (b.get('rules') if b.get('type') == 'multiple' else [b]))
        return {'type': 'multiple', 'rules': arms, **({'flags': out['flags']} if out.get('flags') else {})}
    if 'rules' in a or 'rules' in b:
        out['rules'] = _distinct((a.get('rules') or []) + (b.get('rules') or []))
    if 'fields' in a or 'fields' in b:
        fields: dict = {}
        af, bf = a.get('fields') or {}, b.get('fields') or {}
        for name in list(af) + [n for n in bf if n not in af]:
            if name in af and name in bf:
                fields[name] = _merge_alt(af[name], bf[name])
            else:
                # a field only one path names is never required
                rule = dict(af.get(name) or bf.get(name))
                rule['flags'] = [f for f in (rule.get('flags') or []) if f != 'required']
                if not rule['flags']:
                    rule.pop('flags')
                fields[name] = rule
        out['fields'] = fields
    return out


def _distinct(rules: list[dict]) -> list[dict]:
    """The arms, each once (two paths validating with the same API_MULTIPLE)."""
    seen: set[str] = set()
    out = []
    for rule in rules:
        key = json.dumps(rule, sort_keys=True, default=str)
        if key not in seen:
            seen.add(key)
            out.append(rule)
    return out


def _fixed(rule: dict) -> bool:
    """A rule that takes only listed values (no ranges, no fields)."""
    constraint = rule.get('in') or {}
    return bool(constraint.get('strings') or constraint.get('values')) and not constraint.get('ranges') and not rule.get('fields')


def _alternatives(a: frozenset, b: frozenset) -> bool:
    """Two captures ran on different paths: an undecided branch they took different arms of."""
    arm_a = {branch: arm for branch, arm in a}
    return any(branch in arm_a and arm_a[branch] != arm for branch, arm in b)


def _paths(arms: list[frozenset], limit: int = 64) -> list[list[int]] | None:
    """The execution paths the captures ran on: the maximal sets of captures no two of which took different arms of one
    undecided branch (Bron-Kerbosch). None when there are more than ``limit`` (they are then composed as one path)."""
    n = len(arms)
    compatible = [{j for j in range(n) if j != i and not _alternatives(arms[i], arms[j])} for i in range(n)]
    if all(len(c) == n - 1 for c in compatible):
        return [list(range(n))]
    found: list[list[int]] = []

    def expand(r: set, p: set, x: set) -> bool:
        if not p and not x:
            found.append(sorted(r))
            return len(found) <= limit
        for v in list(p):
            if not expand(r | {v}, p & compatible[v], x & compatible[v]):
                return False
            p = p - {v}
            x = x | {v}
        return True

    return found if expand(set(), set(range(n)), set()) else None


def compose(normalized: list[tuple[tuple, dict] | tuple[tuple, dict, frozenset]], defaults: list[tuple[tuple, dict]]) -> dict | None:
    """The method's input as one rule tree. A validation of ``('in', '[]')`` with an object rule is a validation of every
    element of ``('in',)``; a validation of ``('in', '[]', 'x')`` is the rule for field ``x`` of the elements. The
    validations of one execution path compose as all running (``_merge_seq``); the paths compose as alternatives
    (``_merge_alt``: ``CCepRule::validateWindow``, 8.0:503-549, validates a window of no type and every other type apart)."""
    entries = [(e[0], e[1], e[2] if len(e) > 2 else frozenset()) for e in normalized]
    paths = _paths([arms for _, _, arms in entries]) if entries else [[]]
    if paths is None or len(paths) == 1:
        return _compose_path([(o, r) for o, r, _ in entries], defaults)
    root: dict | None = None
    for path in paths:
        tree = _compose_path([(entries[i][0], entries[i][1]) for i in path], defaults)
        if tree is not None:
            root = tree if root is None else _merge_alt(root, tree)
    return root


def _compose_path(normalized: list[tuple[tuple, dict]], defaults: list[tuple[tuple, dict]]) -> dict | None:
    """The validations of one execution path as one rule tree, every one of them running."""
    root: dict | None = None
    by_path: dict[tuple, dict] = {}
    for origin, rule in normalized:
        path = tuple(origin[1:])
        by_path[path] = _merge_seq(by_path[path], rule) if path in by_path else rule

    def attach(tree: dict | None, path: tuple, rule: dict) -> dict:
        if not path:
            return _merge_seq(tree, rule) if tree else rule
        tree = dict(tree) if tree else {'type': 'object', 'fields': {}}
        head, rest = path[0], path[1:]
        if tree.get('type') == 'multiple' and tree.get('rules'):
            # a later validation of an API_MULTIPLE's value (CUserDirectory::validateProvisionMedias, 8.0:837, of the
            # provision_media the first pass only let through): it refines every arm that is an object
            tree['rules'] = [attach(arm, path, rule) if arm.get('type') in ('object', 'objects') and arm.get('fields') is not None
                             else arm for arm in tree['rules']]
            return tree
        if head == '[]':
            if tree.get('type') == 'objects' or (tree.get('type') == 'unknown'):
                element = {'type': 'object', 'fields': tree.get('fields', {}), 'flags': ['allow_unexpected']}
                element = attach(element, rest, rule)
                tree['type'] = 'objects'
                tree['fields'] = element.get('fields', {})
                if not rest and rule.get('type') == 'object' and rule.get('fields') and 'allow_unexpected' not in (rule.get('flags') or []):
                    # a strict validation of every element (CItemGeneral::validateByType, 7.0:243) makes the list's
                    # elements strict, whatever the first, API_ALLOW_UNEXPECTED pass over the list said
                    tree['flags'] = [f for f in (tree.get('flags') or []) if f != 'allow_unexpected']
                return tree
            if tree.get('type') == 'object' and not tree.get('fields'):
                tree['type'] = 'objects'
                element = attach({'type': 'object', 'fields': {}}, rest, rule)
                tree['fields'] = element.get('fields', {})
                return tree
            return tree
        fields = dict(tree.get('fields') or {})
        fields[head] = attach(fields.get(head), rest, rule)
        tree['fields'] = fields
        return tree

    for path in sorted(by_path, key=len):
        root = attach(root, path, by_path[path])
    strict = root is not None and root.get('type') == 'object' and 'allow_unexpected' not in (root.get('flags') or [])
    for origin, dflt in defaults:
        if tuple(origin) != ('in',) or strict:
            # a strict validation already named every accepted option; later defaults are the method's own internals
            continue
        if root is None:
            root = {'type': 'object', 'fields': {}, 'flags': ['allow_unexpected']}
        fields = dict(root.get('fields') or {})
        for name, value in dflt.items():
            if name not in fields:
                fields[str(name)] = {'type': 'convention', 'default': _plain(value)}
        root['fields'] = fields
        root['options'] = [str(n) for n in dflt]
    return root


def _opened(rule: dict) -> dict:
    """A validation of a projection of the request (``array_intersect_key($graph, $rules['fields'])``): whatever its
    flags, it refuses nothing it did not keep."""
    if rule.get('type') in ('object', 'objects'):
        flags = list(rule.get('flags') or [])
        if 'allow_unexpected' not in flags:
            flags.append('allow_unexpected')
        rule = dict(rule, flags=flags)
    return rule


def apply_allowed(params: dict, allowed: list) -> dict:
    """``if (array_diff_key($drule, $allowed_fields)) { self::exception(…) }`` after an ``API_ALLOW_UNEXPECTED``
    validation (``CDRule::validateCreate``, 8.0:194-208): the allow-list is the input's field list, and the input is
    strict after all. Fields only the allow-list names are typed by convention (and by their column, in merge.py)."""
    for origin, names in allowed:
        path = tuple(origin[1:])
        if path not in ((), ('[]',)):
            continue
        if path == ('[]',) and params.get('type') != 'objects':
            continue
        fields = dict(params.get('fields') or {})
        for name in names:
            fields.setdefault(str(name), {'type': 'convention'})
        params = dict(params, fields=fields, flags=[f for f in (params.get('flags') or []) if f != 'allow_unexpected'])
    return params


def apply_mandatory(params: dict, mandatory: list) -> dict:
    """``check_db_fields(['host' => null], $host)``: the keys with a null default are required."""
    for origin, keys in mandatory:
        path = tuple(origin[1:])
        node = params
        if path and path[0] == '[]':
            path = path[1:]
        ok = True
        for part in path:
            node = (node.get('fields') or {}).get(part)
            if node is None:
                ok = False
                break
        if not ok:
            continue
        fields = node.setdefault('fields', {})
        for key in keys:
            rule = dict(fields.get(key) or {'type': 'convention'})
            flags = list(rule.get('flags') or [])
            if 'required' not in flags:
                flags.append('required')
            rule['flags'] = flags
            fields[key] = rule
    return params


def drop_injected(params: dict, injected: set) -> dict:
    """A field the method writes into the request itself and then validates as API_ANY (``host_status``, ``flags``
    before an item's second validation) is the method's bookkeeping, not an input."""
    for origin, key in injected:
        path = [p for p in origin[1:] if p != '[]']
        node = params
        for part in path:
            node = (node.get('fields') or {}).get(part) or {}
        fields = node.get('fields') or {}
        if key in fields and fields[key].get('type') in ('any', 'unexpected'):
            del fields[key]
    return params


def refine(shape: dict, nested: dict) -> dict:
    """``shape`` (a request typed by what the code reads) with the rules of the validations that cover only parts of it
    (``nested``): each validated part takes its rule, and keeps what ``shape`` knew of it beside the rule's own fields.
    A part validated for some fields only lets the rest through."""
    out = dict(shape)
    fields = dict(shape.get('fields') or {})
    for name, rule in (nested.get('fields') or {}).items():
        known = fields.get(name)
        if known is None or known.get('type') == 'convention':
            rule = dict(rule)
            if rule.get('type') in ('object', 'objects'):
                kept = dict((known or {}).get('fields') or {})
                kept.update(rule.get('fields') or {})
                rule['fields'] = kept
                if 'allow_unexpected' not in (rule.get('flags') or []):
                    rule['flags'] = list(rule.get('flags') or []) + ['allow_unexpected']
            rule['flags'] = sorted(set(rule.get('flags') or []) | set((known or {}).get('flags') or []))
            fields[name] = rule
        elif known.get('type') in ('object', 'objects') and rule.get('type') in ('object', 'objects'):
            fields[name] = refine(known, rule)
    out['fields'] = fields
    return out


def legacy_params(trace) -> dict | None:
    """A method that never calls CApiInputValidator (the oldest services): the fields its ``array_diff_key`` allows, or
    failing that the fields its code reads off the request. Typed by convention, never required."""
    for origin, allowed in trace.allowed:
        path = tuple(origin[1:])
        fields = {name: {'type': 'convention'} for name in allowed}
        if path == ('[]',):
            return {'type': 'objects', 'fields': fields, 'typedBy': 'allowed-fields'}
        if path == ():
            return {'type': 'object', 'fields': fields, 'typedBy': 'allowed-fields'}
    root = trace.touched.get(('in',))
    element = trace.touched.get(('in', '[]'))
    if element and not root:
        return {'type': 'objects', 'fields': {k: {'type': 'convention'} for k in sorted(element)}, 'typedBy': 'access'}
    if root:
        return {'type': 'object', 'fields': {k: {'type': 'convention'} for k in sorted(root)}, 'typedBy': 'access'}
    return None


# ----------------------------------------------------------------------------------------------------------------------
# the call graph: does a method write?

_WRITE_CALLS = re.compile(r'^(DB::(insert|insertBatch|update|updateByPk|save|replace|delete|reserveIds|refreshIds)|DBexecute|'
                          r'CAudit::log|\w+::addAuditLog|\w+::addAuditBulk|\w+::addAuditLogByUser)$', re.I)


class CallGraph:
    """Static: which functions a function may call, and which of them write to the database or through another API."""

    def __init__(self, interp: Interpreter, services: dict[str, str]):
        self.interp = interp
        self.services = services
        self._edges: dict[str, tuple[set, set, set]] = {}

    def _key(self, cls: str | None, name: str) -> str:
        return f'{cls}::{name.lower()}'

    def edges(self, cls: str, called: str, fn) -> tuple[set, set, set]:
        key = self._key(fn.cls, fn.name) + '@' + called
        if key in self._edges:
            return self._edges[key]
        calls: set = set()
        writes: set = set()
        api: set = set()
        if fn._span is not None:
            toks = fn._toks[fn._span[0]:fn._span[1]]
            for i, tok in enumerate(toks[:-3]):
                if tok.kind == 'name' and tok.text == 'CZabbixServer' and i > 0 and toks[i - 1].text.lower() == 'new':
                    # the frontend talking to the server (history.push, script.execute, task.create): never a read
                    writes.add('new CZabbixServer')
                    continue
                if tok.kind == 'name' and toks[i + 1].text == '::' and toks[i + 2].kind == 'name' and toks[i + 3].text == '(':
                    owner, member = tok.text, toks[i + 2].text
                    if owner == 'API' and i + 6 < len(toks) and toks[i + 4].text == ')' and toks[i + 5].text == '->' and toks[i + 6].kind == 'name':
                        target = self.api_object(member)
                        target_cls = self.services.get(target)
                        api.add((target, toks[i + 6].text.lower()))
                        if target_cls:
                            calls.add((target_cls, toks[i + 6].text, target_cls))
                        continue
                    label = f'{owner}::{member}'
                    if _WRITE_CALLS.match(label) or (owner.lower() in ('self', 'static', 'parent') and member.lower() in ('addauditlog', 'addauditbulk', 'addauditlogbyuser')):
                        writes.add(label)
                        continue
                    low = owner.lower()
                    target_cls = called if low in ('self', 'static') else (self.interp.cls(fn.cls).parent if low == 'parent' and self.interp.cls(fn.cls) else owner)
                    calls.add((target_cls, member, called if low in ('self', 'static', 'parent') else target_cls))
                elif tok.kind == 'var' and tok.text == '$this' and toks[i + 1].text == '->' and toks[i + 2].kind == 'name' and toks[i + 3].text == '(':
                    member = toks[i + 2].text
                    if member.lower() in ('addauditbulk', 'addauditlog'):
                        writes.add('$this->' + member)
                        continue
                    calls.add((called, member, called))
                elif tok.kind == 'name' and tok.text == 'DBexecute' and toks[i + 1].text == '(':
                    writes.add('DBexecute')
                elif tok.kind == 'name' and toks[i + 1].text == '(' and (i == 0 or toks[i - 1].text not in ('->', '::', 'function')):
                    if tok.text.lower() in self.interp.functions:
                        calls.add((None, tok.text, None))

            # objects the function constructs: `$x = new CConfigurationImport(…); $x->import(…)`. Every method of those
            # classes the function calls by name is an edge (an over-approximation, which errs toward "writes").
            constructed = {toks[i + 1].text for i, tok in enumerate(toks[:-1]) if tok.kind == 'name' and tok.text.lower() == 'new' and toks[i + 1].kind == 'name'}
            invoked = {toks[i + 1].text for i, tok in enumerate(toks[:-2]) if tok.kind == 'op' and tok.text in ('->', '?->') and toks[i + 1].kind == 'name' and toks[i + 2].text == '(' and not (i > 0 and toks[i - 1].text == '$this')}
            for cname in constructed:
                for mname in invoked | {'__construct'}:
                    if self.interp.find_method(cname, mname) is not None:
                        calls.add((cname, mname, cname))
        self._edges[key] = (calls, writes, api)
        return self._edges[key]

    def api_object(self, accessor: str) -> str:
        fn = self.interp.find_method('API', accessor)
        if fn is not None and fn._span is not None:
            toks = fn._toks[fn._span[0]:fn._span[1]]
            for tok in toks:
                if tok.kind == 'str':
                    return tok.text
        return accessor.lower()

    def reach(self, cls: str, method: str) -> tuple[set, set]:
        """The writes and the other API calls reachable from ``cls::method``."""
        writes: set = set()
        api: set = set()
        seen: set = set()
        stack = [(cls, method, cls)]
        while stack:
            owner, name, called = stack.pop()
            fn = self.interp.functions.get(name.lower()) if owner is None else self.interp.find_method(owner, name)
            if fn is None:
                continue
            key = (fn.cls, fn.name.lower(), called)
            if key in seen:
                continue
            seen.add(key)
            calls, w, a = self.edges(owner or '', called or fn.cls or '', fn)
            writes |= w
            api |= a
            stack.extend(calls)
        return writes, api


# ----------------------------------------------------------------------------------------------------------------------
# the extraction


def _returns_ids(fn) -> str | None:
    """``return ['hostids' => …]``: the ids field a write answers with."""
    if fn is None or fn._span is None:
        return None
    toks = fn._toks[fn._span[0]:fn._span[1]]
    for i, tok in enumerate(toks[:-3]):
        if tok.kind == 'name' and tok.text == 'return' and toks[i + 1].text == '[' and toks[i + 2].kind == 'str' and toks[i + 3].text == '=>':
            if toks[i + 2].text.endswith('ids'):
                return toks[i + 2].text
    return None


def _auth_pairs(interp: Interpreter, method: str) -> set[tuple[str, str]]:
    fn = interp.find_method('CLocalApiClient', method)
    if fn is None or fn._span is None:
        return set()
    toks = fn._toks[fn._span[0]:fn._span[1]]
    text = ' '.join(t.text if t.kind != 'str' else repr(t.text) for t in toks)
    return set(re.findall(r"\$api === '(\w+)' && \$method === '(\w+)'", text))


def _string_list(value: Any) -> list[str] | None:
    if isinstance(value, dict):
        items = [v for v in value.values()]
        if all(isinstance(v, str) for v in items):
            return [str(v) for v in items]
    if isinstance(value, str) and not is_unknown(value):
        return [v for v in str(value).split(',') if v]
    return None


class VersionFacts:
    def __init__(self, root: str, version: str):
        self.root = root
        self.version = version
        self.interp = Interpreter(root)
        self.vocab = Vocabulary(self.interp)
        self.norm = Normalizer(self.vocab)
        self.graph = CallGraph(self.interp, self.services())
        self.problems: list[str] = []
        self._src_cache: dict[str, str] = {}
        self.tables_used: set[str] = set()

    def rel(self, path: str) -> str:
        return os.path.relpath(path, self.root)

    def line(self, path: str, pos: int) -> int:
        if path not in self._src_cache:
            with open(path, encoding='utf-8', errors='replace') as handle:
                self._src_cache[path] = handle.read()
        return line_of(self._src_cache[path], pos)

    def services(self) -> dict[str, str]:
        path = os.path.join(self.interp.ui, 'classes', 'api', 'CApiServiceFactory.php')
        with open(path, encoding='utf-8') as handle:
            text = handle.read()
        return dict(re.findall(r"'(\w+)'\s*=>\s*(\w+)::class", text))

    def extract(self) -> dict:
        anonymous = _auth_pairs(self.interp, 'requiresAuthentication')
        optional = _auth_pairs(self.interp, 'supportsAuthentication')
        objects: dict = {}
        for obj, cls in sorted(self.services().items()):
            if obj == 'api':
                continue
            objects[obj] = self.object(obj, cls, anonymous, optional)
        version = self.interp.define('ZABBIX_API_VERSION')
        for entry in objects.values():
            if entry.get('table'):
                self.tables_used.add(entry['table'])
            for sel in (entry.get('output') or {}).get('selects', {}).values():
                if sel.get('table'):
                    self.tables_used.add(sel['table'])
        return {
            'version': self.version,
            'zabbix': str(version) if isinstance(version, str) else None,
            'objects': objects,
            'externalIgnored': self.external_ignored(),
            'userTypes': {name: int(self.interp.define(name)) for name in ('USER_TYPE_ZABBIX_USER', 'USER_TYPE_ZABBIX_ADMIN', 'USER_TYPE_SUPER_ADMIN')},
            'problems': self.problems + [f'unreadable: {u}' for u in self.interp.unreadable],
            'tables': {t: self.table(t) for t in sorted(self.tables_used) if self.table(t)},
        }

    def external_ignored(self) -> list[str]:
        """The parameters the API client takes out of every external call before the service sees it:
        ``unset($params['nopermissions'])`` (``CLocalApiClient.php`` 8.0:94, "must not be available for external API
        calls"). An option the services read but a caller can never set."""
        path = os.path.join(self.interp.ui, 'classes', 'api', 'clients', 'CLocalApiClient.php')
        if not os.path.isfile(path):
            return []
        with open(path, encoding='utf-8') as handle:
            return sorted(set(re.findall(r"unset\(\$params\['(\w+)'\]\)", handle.read())))

    def object(self, obj: str, cls: str, anonymous: set, optional: set) -> dict:
        classdef = self.interp.cls(cls)
        access = self.interp.class_const(cls, 'ACCESS_RULES')
        entry: dict = {'class': cls, 'file': self.rel(classdef.file) if classdef else None, 'methods': {}}
        table = self.interp.class_prop(cls, 'tableName') if classdef else None
        if isinstance(table, str) and table:
            entry['table'] = table
        if not isinstance(access, dict):
            self.problems.append(f'{obj}: ACCESS_RULES not evaluated')
            return entry
        for method, rules in access.items():
            method = str(method)
            fn = self.interp.find_method(cls, method)
            m: dict = {'function': fn.name if fn else None}
            if isinstance(rules, dict) and 'min_user_type' in rules and not is_unknown(rules['min_user_type']):
                m['minUserType'] = int(rules['min_user_type'])
            m['requiresAuthentication'] = (obj, method) not in anonymous
            if (obj, method) in optional:
                m['acceptsAuthentication'] = True
            if fn is None:
                self.problems.append(f'{obj}.{method}: no PHP function')
                entry['methods'][method] = m
                continue
            m['source'] = f'{self.rel(fn.file)}:{fn.line}'
            trace = self.interp.run(cls, fn.name)
            normalized = [(c.origin, _opened(self.norm.rule(c.rules)) if c.partial else self.norm.rule(c.rules), c.arms)
                          for c in trace.captures if isinstance(c.rules, dict)]
            params = compose(normalized, trace.defaults)
            composed = params is not None
            if params is None:
                params = legacy_params(trace)
            elif 'options' not in params and not any(tuple(origin[1:]) in ((), ('[]',)) for origin, _, _ in normalized) \
                    and legacy_params(trace):
                # validations of parts of the request only (an interface's SNMP details, CHostInterface::checkSnmpRetries,
                # 8.0:1090): the request is the shape the older code reads, and they refine the parts they validate
                params = refine(legacy_params(trace), params)
            if composed and trace.allowed:
                params = apply_allowed(params, trace.allowed)
            if params is not None:
                params = apply_mandatory(params, trace.mandatory)
                params = drop_injected(params, trace.injected)
                self.replacements(cls, params)
            stores = sorted({('/'.join(o[1:]), t) for o, t in trace.inserts})
            if stores:
                m['stores'] = [{'path': p, 'table': t} for p, t in stores]
                for _, t in stores:
                    self.tables_used.add(t)
            if params is not None:
                m['params'] = params
            m['validations'] = sorted({f'{self.rel(c.file)}:{self.line(c.file, c.line)}' for c in trace.captures if c.file})
            ids = _returns_ids(fn)
            if ids:
                m['returnsIds'] = ids
            writes, api = self.graph.reach(cls, fn.name)
            m['writes'] = sorted(writes)
            calls = sorted(f'{o}.{mm}' for o, mm in api)
            if calls:
                m['calls'] = calls
            entry['methods'][method] = m
            if self.interp.errors:
                self.problems.extend(f'{obj}.{method}: {e}' for e in self.interp.errors[:3])
                self.interp.errors.clear()
        if 'get' in entry['methods'] and classdef:
            entry['output'] = self.output(obj, cls, entry)
        return entry

    # ------------------------------------------------------------------------------------------------------------------
    # what a .get returns

    def output(self, obj: str, cls: str, entry: dict) -> dict:
        get = entry['methods']['get']
        params = get.get('params') or {}
        fields = params.get('fields') or {}
        out: dict = {}
        declared = None
        output_rule = fields.get('output')
        if output_rule and output_rule.get('in', {}).get('strings'):
            declared = output_rule['in']['strings']
            out['fieldsFrom'] = 'output-rule'
        else:
            for const in ('OUTPUT_FIELDS',):
                v = self.interp.class_const(cls, const)
                lst = _string_list(v)
                if lst:
                    declared = lst
                    out['fieldsFrom'] = 'OUTPUT_FIELDS'
                    break
            if declared is None:
                v = self.interp.class_prop(cls, 'output_fields')
                lst = _string_list(v)
                if lst:
                    declared = lst
                    out['fieldsFrom'] = 'output_fields'
        table = entry.get('table')
        columns = self.table(table) if table else {}
        if declared is None and columns:
            declared = list(columns)
            out['fieldsFrom'] = 'table'
        out['fields'] = declared or []
        computed = sorted(set(self.requested_fields(cls)) - set(out['fields']))
        if computed:
            out['computed'] = computed
        removed = self.unset_fields(cls, table)
        if removed:
            out['unset'] = removed
        selects: dict = {}
        for name, rule in fields.items():
            if name.startswith('select') and len(name) > 6:
                selects[name] = self.select(cls, name, rule, obj)
        out['selects'] = selects
        filt = fields.get('filter') or {}
        if filt.get('filterable'):
            out['filterable'] = filt['filterable']
        search = fields.get('search') or {}
        if search.get('filterable'):
            out['searchable'] = search['filterable']
        # what sortfield accepts: the newer services' own rule ('sortfield' => [… 'in' => …], CMediatype.php 8.0:137),
        # else CApiService's $sortColumns (CHost.php 8.0:22), which CApiService::checkSortField enforces
        sortfield = fields.get('sortfield') or {}
        sortable = (sortfield.get('in') or {}).get('strings') or _string_list(self.interp.class_prop(cls, 'sortColumns'))
        if sortable:
            out['sortable'] = list(sortable)
        return out

    def table(self, table: str) -> dict:
        schema = self.interp.schema
        if not isinstance(schema, dict) or table not in schema:
            return {}
        cols = schema[table].get('fields', {})
        dbtypes = {int(self.interp.class_const('DB', n)): n[11:].lower() for n in ('FIELD_TYPE_INT', 'FIELD_TYPE_CHAR', 'FIELD_TYPE_ID', 'FIELD_TYPE_FLOAT', 'FIELD_TYPE_UINT', 'FIELD_TYPE_BLOB', 'FIELD_TYPE_TEXT', 'FIELD_TYPE_CUID') if isinstance(self.interp.class_const('DB', n), int)}
        out = {}
        for name, col in cols.items():
            if not isinstance(col, dict):
                continue
            t = col.get('type')
            out[name] = {'db': dbtypes.get(int(t), 'unknown') if isinstance(t, int) else 'unknown'}
            if isinstance(col.get('length'), int):
                out[name]['length'] = int(col['length'])
            if col.get('null') is True:
                out[name]['null'] = True
            if 'ref_table' in col and isinstance(col['ref_table'], str):
                out[name]['ref'] = col['ref_table']
            if isinstance(col.get('default'), (str, int)) and not isinstance(col.get('default'), bool):
                out[name]['default'] = str(col['default'])
        return out

    def class_text(self, cls: str) -> list[tuple[str, str]]:
        out = []
        for c in self.interp.chain(cls):
            if c.file not in self._src_cache:
                with open(c.file, encoding='utf-8', errors='replace') as handle:
                    self._src_cache[c.file] = handle.read()
            out.append((c.file, self._src_cache[c.file]))
        return out

    def replacements(self, cls: str, params: dict) -> None:
        """A deprecated field the service converts into another before it validates it further names that field as its
        replacement: ``$maintenance['hosts'] = zbx_toObject($maintenance['hostids'], 'hostid')`` (``CMaintenance.php``
        7.0:329) makes ``hostids`` → ``hosts``. The rule's own ``'replacement'`` key, where there is one, wins."""
        fields = params.get('fields') or {}
        deprecated = {n: r for n, r in fields.items() if 'deprecated' in (r.get('flags') or []) and 'replacement' not in r}
        if not deprecated:
            return
        for _, text in self.class_text(cls)[:1]:
            for m in re.finditer(r"\$(\w+)\['(\w+)'\]\s*=\s*zbx_toObject\(\s*\$\1\['(\w+)'\]", text):
                new, old = m.group(2), m.group(3)
                if old in deprecated and new in fields:
                    deprecated[old]['replacement'] = new

    def unset_fields(self, cls: str, table: str | None) -> list[str]:
        """The columns ``get`` takes out of every object it returns, in its own body or a helper it calls, on every path:
        ``unset($template['hostid'], $template['discover'])`` (``CTemplate.php`` 8.0:252, templates share the hosts
        table), ``unset($item['storage_mode'])`` (``CItem.php`` 8.0:497), ``unsetExtraFields($result, ['name_upper'])``
        with no output to keep them for, and an unset under ``if ($this->outputIsRequested('discover', …))`` (the field is
        gone whenever it is asked for, ``CTrigger.php`` 8.0:540). An unset under any other condition (a secret macro's
        value, ``CUserMacro.php`` 8.0:1394) is not one, and only the object's table's columns count."""
        fn = self.interp.find_method(cls, 'get')
        if fn is None or fn._span is None or not table:
            return []
        columns = self.table(table)
        names: set[str] = set()
        helpers: list[str] = []

        def literal_key(target: Node) -> str | None:
            if target.kind == 'index' and target.b is not None and target.b.kind == 'lit' and isinstance(target.b.a, str) \
                    and target.a is not None and target.a.kind == 'var':
                return target.b.a
            return None

        def requested(cond: Node) -> bool:
            return cond.kind == 'mcall' and cond.b == 'outputIsRequested'

        def neutral(cond: Node) -> bool:
            """A condition on the reply's shape, not on the object (``if ($options['countOutput'])``, ``if ($result)``):
            either arm reaches every object a list reply returns."""
            if cond.kind == 'var':
                return cond.a == 'result'
            if cond.kind == 'unary' and cond.a == '!':
                return neutral(cond.b)
            if cond.kind == 'bin' and cond.a in ('&&', '||', 'and', 'or'):
                return neutral(cond.b) and neutral(cond.c)
            return cond.kind == 'index' and cond.a is not None and cond.a.kind == 'var' and cond.a.a == 'options' \
                and cond.b is not None and cond.b.kind == 'lit' and cond.b.a in ('countOutput', 'groupCount', 'preservekeys')

        def calls(node, conditional: bool) -> None:
            if not isinstance(node, Node):
                return
            if node.kind in ('mcall', 'scall') and isinstance(node.b, str):
                own = node.kind == 'scall' and str(node.a).lower() in ('self', 'static') \
                    or node.kind == 'mcall' and node.a is not None and node.a.kind == 'var' and node.a.a == 'this'
                args = node.c if isinstance(node.c, list) else []
                if node.b == 'unsetExtraFields' and len(args) == 2 and not conditional and args[1].kind == 'array':
                    names.update(v.a for _, v in args[1].a if v.kind == 'lit' and isinstance(v.a, str))
                elif own and not node.b.startswith('addRelated') and node.b != 'unsetExtraFields':
                    # (addRelatedObjects and its kin shape what a select adds, not the object's own row)
                    helpers.append(node.b)
            for child in (node.a, node.b, node.c):
                if isinstance(child, Node):
                    calls(child, conditional)
                elif isinstance(child, list):
                    for c in child:
                        calls(c[1] if isinstance(c, tuple) else c, conditional)

        def walk(stmts, conditional: bool) -> None:
            for s in stmts or []:
                if not isinstance(s, Node):
                    continue
                if s.kind == 'unset':
                    if not conditional:
                        names.update(k for k in (literal_key(t) for t in s.a) if k)
                elif s.kind == 'if':
                    for cond, body in s.a:
                        calls(cond, True)
                        walk(body, conditional or not (requested(cond) or neutral(cond)))
                    walk(s.b, conditional or not all(neutral(cond) for cond, _ in s.a))
                elif s.kind == 'foreach':
                    walk(s.d, conditional)
                elif s.kind in ('block', 'loop'):
                    walk(s.a, conditional)
                elif s.kind == 'while':
                    # (the loop over the fetched rows: while ($row = DBfetch($res)))
                    walk(s.b, conditional)
                elif s.kind == 'try':
                    walk(s.a, conditional)
                elif s.kind == 'switch':
                    for _, body in s.b:
                        walk(body, True)
                else:
                    calls(s, conditional)

        walk(fn.parsed_body(), False)
        for helper in list(dict.fromkeys(helpers)):
            called = self.interp.find_method(cls, helper)
            if called is not None and called is not fn:
                walk(called.parsed_body(), False)
        return sorted(n for n in names if n in columns)

    def requested_fields(self, cls: str) -> list[str]:
        """``outputIsRequested('x', …)``: fields a ``.get`` adds or computes beyond the table."""
        names: list[str] = []
        for _, text in self.class_text(cls)[:2]:
            names.extend(re.findall(r"outputIsRequested\('(\w+)',\s*\$options\['output'\]\)", text))
        return list(dict.fromkeys(names))

    def select(self, cls: str, option: str, rule: dict, obj: str | None = None) -> dict:
        """What ``selectX`` adds to each object: the result key, one or many, and what it is (another API object, a
        table, or the fields the option's own rule lists)."""
        out: dict = {}
        if rule.get('in', {}).get('strings'):
            out['in'] = rule['in']['strings']
        if 'allow_count' in (rule.get('flags') or []):
            out['count'] = True
        if 'deprecated' in (rule.get('flags') or []):
            out['deprecated'] = True
        suffix = option[6:]
        for path, text in self.class_text(cls):
            for m in re.finditer(r"\$options\['" + re.escape(option) + r"'\]", text):
                block = _enclosing_block(text, m.start())
                if block is None:
                    continue
                key = None
                for lit in re.findall(r"'(\w+)'", block):
                    # the key may be snake case (selectRecoveryOperations → recovery_operations)
                    if lit.lower().replace('_', '') == suffix.lower():
                        key = lit
                        break
                apis = re.findall(r"API::(\w+)\(\)->get\(", block)
                tables = re.findall(r"(?:DB::select|\$this->select|DBselect)\(\s*'(\w+)'", block) + re.findall(r"\bFROM\s+(\w+)\b", block)
                mapper = re.search(r"->(mapOne|mapMany)\(", block)
                # one level down: the block hands the work to a helper ($this->addRelatedGroups(…), self::addX(…))
                bodies: list[str] = []
                own: list[str] = []
                # the service reads its own objects for the option (CService::addRelatedChildren, 7.0:705-742: $this->doGet
                # inside the function that reads $options['selectChildren']): children and parents are services
                start = text.rfind('function ', 0, m.start())
                end = text.find('function ', m.start())
                if obj and start >= 0 and '$this->doGet(' in text[start:end if end >= 0 else len(text)]:
                    own.append(obj)
                for helper in re.findall(r"(?:\$this->|self::|static::)(\w+)\(", block):
                    fn = self.interp.find_method(cls, helper)
                    if fn is None or fn._span is None:
                        continue
                    body = ' '.join(t.text if t.kind != 'str' else "'" + t.text + "'" for t in fn._toks[fn._span[0]:fn._span[1]])
                    bodies.append(body)
                    apis += re.findall(r"API :: (\w+) \( \) -> get \(", body)
                    if obj and re.search(r"\$this -> doGet \(", body):
                        own.append(obj)
                    tables += re.findall(r"(?:DB :: select|\$this -> select|DBselect) \( '(\w+)'", body)
                    mapper = mapper or re.search(r"-> (mapOne|mapMany) \(", body)
                    if key is None:
                        for lit in re.findall(r"'(\w+)'", body):
                            if lit.lower().replace('_', '') == suffix.lower():
                                key = lit
                                break
                # A block may read several things (selectSelements resolves host groups for its elements): the one it
                # returns is the one named like the option.
                want = singular(suffix).lower()

                def related(name: str) -> float:
                    n = name.lower().replace('_', '')
                    n = n[:-1] if n.endswith('s') else n
                    return 3 if n == want else 2.5 if n.endswith(want) else 2 if want in n or n in want else 0

                candidates = [(related(self.graph.api_object(a)), 'object', self.graph.api_object(a)) for a in apis]
                # its own objects, when the option is named for none of the others (children, parents are services)
                candidates += [(1, 'object', o) for o in own]
                candidates += [(related(t), 'table', t) for t in tables]
                best = max(candidates, key=lambda c: c[0], default=None)
                api = table = None
                if best and best[0] > 0:
                    if best[1] == 'object':
                        api = best[2]
                    else:
                        table = best[2]
                if key:
                    out['field'] = key
                if api:
                    out['object'] = api
                elif table:
                    out['table'] = table
                if mapper:
                    out['many'] = mapper.group(1) == 'mapMany'
                elif key and re.search(r"\[\s*'" + re.escape(key) + r"'\s*\]\s*=\s*array_values\s*\(", block + ' ' + ' '.join(bodies)):
                    # the key is filled with array_values(…): a list, whatever the option's name says
                    # (selectChildren → children, CService.php 7.0:740)
                    out['many'] = True
                out['source'] = f'{self.rel(path)}:{line_of(text, m.start())}'
                if key and ('object' in out or 'table' in out):
                    return out
        if 'object' not in out and 'table' not in out:
            # the option is named for an API object (selectHostGroups → hostgroup, selectGroups → hostgroup's alias)
            target = singular(suffix).lower()
            if target in self.graph.services:
                out['object'] = target
                out['objectGuessed'] = True
        if 'field' not in out:
            # the newer options return their key in lower case (selectHostGroups → hostgroups, on 7.0 and 8.0)
            out['field'] = suffix.lower()
            out['fieldGuessed'] = True
        return out


def singular(word: str) -> str:
    if word.endswith('ies'):
        return word[:-3] + 'y'
    if word.endswith('s') and not word.endswith('ss'):
        return word[:-1]
    return word


def _enclosing_block(text: str, pos: int) -> str | None:
    """From an ``if (… $options['selectX'] …)`` to the end of its block."""
    brace = text.find('{', pos)
    if brace < 0 or brace - pos > 400:
        return None
    depth = 0
    i = brace
    while i < len(text):
        c = text[i]
        if c == '{':
            depth += 1
        elif c == '}':
            depth -= 1
            if depth == 0:
                return text[brace:i]
        i += 1
    return None


def _frame(cls: str):
    from .interp import Frame
    return Frame({}, cls, cls, None)
