# This Source Code Form is subject to the terms of the Mozilla Public
# License, v. 2.0. Copyright (c) 2026 schroeder247. SPDX-License-Identifier: MPL-2.0
"""python3 -m zabbix_api_extract verify [--sources DIR] [--schema DIR] [--all]

The regression gate for the description: a second, independent reading of each version's PHP compared, mechanically,
with what ``schema/zabbix-api.json`` says. Every discrepancy is printed as one line,

    <version> <CODE> <object.method> <path>: <what differs> [<file>:<line>]

and the run fails unless every line is recorded in ``schema/verify-exceptions.json`` with the source reason it is not a
defect (and every recorded exception still occurs, so the file cannot go stale).

Why a second reading and not the extractor's own facts: the extractor's abstract interpreter is the thing under test.
This reader shares only the tokenizer and parser (``php.py``); it has its own constant evaluator and its own walk.
The walk is deliberately simpler: it follows the service's own methods (``$this->``, ``self::``, ``static::``,
``parent::``) and the non-service helper classes by name, takes every arm of a branch it cannot decide, never runs
another API method, and tracks only *where in the request* a value came from. What it records, per method:

- every ``CApiInputValidator::validate($rules, $data)`` whose ``$data`` came from the request, at that request path;
- every allow-list the request is checked against (``array_diff_key($request, $allowed)``, which makes an
  ``API_ALLOW_UNEXPECTED`` object strict after all: ``CDRule::validateCreate``, 8.0:194-208);
- every hand-written mandatory check (``check_db_fields(['name' => null], $x)``, and
  ``if (!array_key_exists('name', $x)) { self::exception(…) }``);
- the ``$defOptions`` an older ``.get`` merges its options over;
- the tables the request is handed to (``DB::insert('drules', $drules)``).

Per object it reads ``ACCESS_RULES`` (min_user_type), ``OUTPUT_FIELDS``, ``$sortColumns`` and the relation maps
(``mapMany``/``mapOne``); per version ``CApiServiceFactory`` and ``CLocalApiClient::requiresAuthentication``.
"""
from __future__ import annotations

import argparse
import fnmatch
import json
import os
import re
import sys
from dataclasses import dataclass, field
from typing import Any

from .php import NAMESPACE, Function, Node, Parser, line_of, parse_file, tokenize

VERSIONS = ['7.0', '7.4', '8.0']


# ----------------------------------------------------------------------------------------------------------------------
# values


class Opaque:
    """A value this reader does not know (it depends on the database, the caller, or code it does not evaluate)."""

    __slots__ = ('why',)

    def __init__(self, why: str = '?'):
        self.why = why

    def __repr__(self) -> str:
        return f'<{self.why}>'


class Req(Opaque):
    """A value taken from the request: ``path`` is where (``'[]'`` an element of a list). ``partial``: only some of its
    keys (``array_intersect_key($graph, $rules['fields'])``, ``CGraphGeneral.php`` 7.0:800), so a strict validation of
    it says nothing about the keys it left out."""

    __slots__ = ('path', 'partial')

    def __init__(self, path: tuple, partial: bool = False):
        super().__init__('request')
        self.path = path
        self.partial = partial

    def __repr__(self) -> str:
        return 'req:/' + '/'.join(self.path)


class This:
    pass


OPAQUE_KEYS = '\0opaque'        # a map that also holds keys this reader could not evaluate
ALTERNATIVES = '\0alternatives'  # an API_MULTIPLE this reader made of a value's alternatives


def known(v: Any) -> bool:
    return not isinstance(v, (Opaque, This))


def as_list(v: Any) -> list | None:
    if isinstance(v, dict):
        return list(v.values())
    return None


# ----------------------------------------------------------------------------------------------------------------------
# facts


@dataclass
class Rule:
    """One ``validate($rules, $data)``: the evaluated rule, the request path its data came from, and where."""

    rule: Any
    path: tuple
    file: str
    line: int
    partial: bool = False
    conditional: bool = False   # validated in an arm the method may not take: its REQUIRED flags are conditional


@dataclass
class MethodFacts:
    obj: str
    method: str
    source: str
    min_user_type: int | None
    requires_auth: bool
    rules: list[Rule] = field(default_factory=list)
    allowed: list[tuple[tuple, set, str]] = field(default_factory=list)       # path, keys, where
    mandatory: list[tuple[tuple, set, str]] = field(default_factory=list)     # path, keys, where
    def_options: dict = field(default_factory=dict)                           # key -> default
    stores: set = field(default_factory=set)                                  # (path, table)
    injected: set = field(default_factory=set)                                # (path, key) the method writes into the request
    read_off: dict = field(default_factory=dict)                              # path -> keys read off the request


# ----------------------------------------------------------------------------------------------------------------------
# the reader


BUILTIN_SKIP = {'db', 'api', 'capiinputvalidator', 'carrayhelper', 'caudit', 'cwebuser', 'capihelper', 'cmacrohelper',
                'cmacrosresolverhelper', 'capiservice', 'cregistry', 'cconfigfile', 'cvaluemaphelper'}


class Reader:
    """One version's frontend source, parsed; constants evaluated on demand."""

    def __init__(self, root: str, version: str):
        self.root = root
        self.version = version
        self.ui = os.path.join(root, 'ui', 'include')
        self.classes: dict[str, Any] = {}
        self._namespaced: set[str] = set()
        self.functions: dict[str, Function] = {}
        self.define_nodes: dict[str, Node] = {}
        self.define_values: dict[str, Any] = {}
        self.const_values: dict[tuple, Any] = {}
        self.sources: dict[str, str] = {}
        self._schema: dict | None = None
        self._load()
        self.services = self._services()
        self.service_classes = {c.lower() for c in self.services.values()}

    def external_ignored(self) -> set[str]:
        """``unset($params['nopermissions'])`` in the API client (``CLocalApiClient.php`` 8.0:94): a parameter no
        external call reaches a service with."""
        c = self.cls('CLocalApiClient')
        src = self.sources.get(c.file, '') if c is not None else ''
        return set(re.findall(r"unset\(\$params\['(\w+)'\]\)", src))

    # -- loading

    def _load(self) -> None:
        paths = [os.path.join(self.ui, n) for n in sorted(os.listdir(self.ui)) if n.endswith('.php')]
        for directory, dirs, files in os.walk(os.path.join(self.ui, 'classes')):
            dirs.sort()   # one order on every file system: the first class of a name is kept
            paths.extend(os.path.join(directory, n) for n in sorted(files) if n.endswith('.php'))
        for path in paths:
            with open(path, encoding='utf-8', errors='replace') as handle:
                src = handle.read()
            if 'class ' not in src and 'function ' not in src and 'define(' not in src and 'trait ' not in src:
                continue
            try:
                classes, functions = parse_file(path, src)
            except Exception:   # noqa: BLE001 - an unparseable file only narrows what this reader can see
                continue
            self.sources[path] = src
            namespaced = bool(NAMESPACE.search(src))
            for c in classes:
                # a namespaced class (Zabbix\Core\CModule) never hides the global one the API names (CModule)
                key = c.name.lower()
                if key not in self.classes or (key in self._namespaced and not namespaced):
                    self.classes[key] = c
                    if namespaced:
                        self._namespaced.add(key)
                    else:
                        self._namespaced.discard(key)
            for k, fn in functions.items():
                self.functions.setdefault(k, fn)
            if "define('" in src:
                toks = tokenize(src)
                for i, tok in enumerate(toks[:-3]):
                    if tok.kind == 'name' and tok.text == 'define' and toks[i + 1].text == '(' and toks[i + 2].kind == 'str' and toks[i + 3].text == ',':
                        p = Parser(toks)
                        p.i = i + 4
                        try:
                            self.define_nodes.setdefault(toks[i + 2].text, p.expr())
                        except Exception:   # noqa: BLE001
                            pass

    def _services(self) -> dict[str, str]:
        path = os.path.join(self.ui, 'classes', 'api', 'CApiServiceFactory.php')
        with open(path, encoding='utf-8') as handle:
            text = handle.read()
        return {k: v for k, v in re.findall(r"'(\w+)'\s*=>\s*(\w+)::class", text) if k != 'api'}

    def rel(self, path: str) -> str:
        return os.path.relpath(path, self.root)

    def where(self, fn: Function | None, pos: int) -> str:
        if fn is None:
            return '?'
        return f'{self.rel(fn.file)}:{line_of(self.sources.get(fn.file, ""), pos)}'

    # -- classes

    def cls(self, name: str | None):
        return self.classes.get(name.lower()) if name else None

    def chain(self, name: str | None) -> list:
        out, seen = [], set()
        c = self.cls(name)
        while c is not None and c.name.lower() not in seen:
            seen.add(c.name.lower())
            out.append(c)
            for t in c.traits:
                tc = self.cls(t)
                if tc is not None:
                    out.append(tc)
            c = self.cls(c.parent)
        return out

    def method(self, cls: str | None, name: str) -> Function | None:
        for c in self.chain(cls):
            fn = c.methods.get(name.lower())
            if fn is not None and fn._span is not None:
                return fn
        return None

    def is_a(self, cls: str | None, base: str) -> bool:
        return any(c.name.lower() == base.lower() for c in self.chain(cls))

    # -- constants

    def define(self, name: str) -> Any:
        if name in self.define_values:
            return self.define_values[name]
        node = self.define_nodes.get(name)
        if node is None:
            return Opaque('define:' + name)
        self.define_values[name] = Opaque('cycle')
        value = Eval(self, None, None, {}).ev(node)
        self.define_values[name] = value
        return value

    def const(self, cls: str, name: str) -> Any:
        for c in self.chain(cls):
            if name in c.consts:
                key = (c.name, name)
                if key not in self.const_values:
                    self.const_values[key] = Opaque('cycle')
                    self.const_values[key] = Eval(self, c.name, cls, {}).ev(c.consts[name])
                return self.const_values[key]
        return Opaque(f'{cls}::{name}')

    def prop(self, cls: str, name: str) -> Any:
        for c in self.chain(cls):
            if name in c.props:
                node = c.props[name][0]
                return Eval(self, c.name, cls, {}).ev(node) if node is not None else None
        return Opaque('prop:' + name)

    @property
    def schema(self) -> dict:
        if self._schema is None:
            with open(os.path.join(self.ui, 'schema.inc.php'), encoding='utf-8') as handle:
                p = Parser(tokenize(handle.read()))
            p.expect('return')
            value = Eval(self, None, None, {}).ev(p.expr())
            self._schema = value if isinstance(value, dict) else {}
        return self._schema

    def filter_fields(self, table: Any, output: Any, which: str) -> Any:
        schema = self.schema.get(table) if isinstance(table, str) else None
        if not isinstance(schema, dict):
            return Opaque('schema')
        fields = schema.get('fields') or {}
        if output is not None:
            if not isinstance(output, dict):
                return Opaque('fields')
            keep = set(output.values())
            fields = {k: v for k, v in fields.items() if k in keep}
        supported = self.const('DB', 'SUPPORTED_FILTER_TYPES' if which == 'filter' else 'SUPPORTED_SEARCH_TYPES')
        if not isinstance(supported, int):
            return Opaque('supported')
        out = [k for k, v in fields.items() if isinstance(v, dict) and isinstance(v.get('type'), int) and v['type'] & supported]
        return {i: k for i, k in enumerate(out)}


# ----------------------------------------------------------------------------------------------------------------------
# the evaluator (constants, pure helpers, and request paths)


def _list(values: list) -> dict:
    return {i: v for i, v in enumerate(values)}


class Eval:
    def __init__(self, reader: Reader, cls: str | None, called: str | None, env: dict, walker: 'Walker | None' = None, fn: Function | None = None):
        self.r = reader
        self.cls = cls
        self.called = called
        self.env = env
        self.walker = walker
        self.fn = fn

    def resolve(self, name: str) -> str | None:
        low = name.lower()
        if low == 'self':
            return self.cls
        if low == 'static':
            return self.called
        if low == 'parent':
            c = self.r.cls(self.cls)
            return c.parent if c else None
        return name

    def ev(self, n: Node | None) -> Any:   # noqa: C901 - one dispatch over the node kinds
        if n is None:
            return None
        k = n.kind
        if k == 'lit':
            return n.a
        if k == 'var':
            if n.a == 'this':
                return This()
            return self.env.get(n.a, Opaque('var:' + n.a))
        if k == 'const':
            up = n.a.upper()
            if up == 'PHP_INT_MAX':
                return 2 ** 63 - 1
            if up == 'PHP_EOL':
                return '\n'
            if up == '__FUNCTION__' and self.fn is not None:
                return self.fn.name
            if up == '__CLASS__':
                return self.cls
            return self.r.define(n.a)
        if k == 'cconst':
            c = self.resolve(n.a)
            return self.r.const(c, n.b) if c else Opaque('cconst')
        if k == 'array':
            out: dict = {}
            nxt = 0
            for key, value in n.a:
                if value.kind == 'spread':
                    v = self.ev(value.a)
                    for item in (as_list(v) or []):
                        out[nxt] = item
                        nxt += 1
                    continue
                v = self.ev(value)
                if key is None:
                    out[nxt] = v
                    nxt += 1
                    continue
                kv = self.ev(key)
                if isinstance(kv, bool):
                    kv = int(kv)
                if isinstance(kv, str) and re.fullmatch(r'-?\d+', kv):
                    kv = int(kv)
                if not isinstance(kv, (int, str)):
                    continue
                out[kv] = v
                if isinstance(kv, int) and kv >= nxt:
                    nxt = kv + 1
            return out
        if k == 'bin':
            a, b = self.ev(n.b), self.ev(n.c)
            op = n.a
            if op == '??':
                return a if a is not None and known(a) else (b if a is None else a)
            if not (known(a) and known(b)):
                if op == '+' and isinstance(a, Req):
                    return a
                if op == '+' and isinstance(b, Req) and isinstance(a, dict) and self.walker is not None:
                    # ['sysmapid' => $map['sysmapid']] + $selement (CMap.php 8.0:2544): the request element with the
                    # method's own keys in front; those are bookkeeping, not input
                    self.walker.facts.injected.update((_strip(b.path), str(key)) for key in a if isinstance(key, str))
                    return b
                return Opaque('bin')
            try:
                if op == '|':
                    return int(a) | int(b)
                if op == '&':
                    return int(a) & int(b)
                if op == '.':
                    return _str(a) + _str(b)
                if op == '+':
                    if isinstance(a, dict) and isinstance(b, dict):
                        return {**b, **a}
                    return a + b
                if op == '-':
                    return a - b
                if op == '*':
                    return a * b
                if op in ('==', '==='):
                    return a == b
                if op in ('!=', '!==', '<>'):
                    return a != b
            except (TypeError, ValueError):
                return Opaque('bin')
            return Opaque('bin')
        if k == 'instanceof':
            # the class a shared method runs for is known: `$this instanceof CTriggerPrototype` is decided by it
            subject = self.ev(n.a)
            if isinstance(subject, This) and n.b is not None and n.b.kind == 'const' and self.called:
                return self.r.is_a(self.called, n.b.a)
            return Opaque('instanceof')
        if k == 'unary':
            v = self.ev(n.b)
            if n.a == '-' and isinstance(v, (int, float)) and not isinstance(v, bool):
                return -v
            if n.a == '~' and isinstance(v, int) and not isinstance(v, bool):
                return ~v
            if n.a == '!' and isinstance(v, bool):
                return not v
            return Opaque('unary')
        if k == 'ternary':
            c = self.ev(n.a)
            if isinstance(c, bool):
                return self.ev(n.b if c else n.c)
            return alternatives([self.ev(n.b), self.ev(n.c)], self.r)
        if k == 'index':
            base = self.ev(n.a)
            if n.b is None:
                return Opaque('append')
            key = self.ev(n.b)
            if isinstance(base, Req):
                if isinstance(key, str) and self.walker is not None:
                    self.walker.facts.read_off.setdefault(_strip(base.path), set()).add(key)
                return Req(base.path + ((key,) if isinstance(key, str) else ('[]',)))
            if isinstance(base, dict) and base.get(ALTERNATIVES) and isinstance(key, str) and key not in base:
                # a key of rules that are alternatives: that key of each
                return alternatives([arm.get(key) for arm in base['rules'].values() if isinstance(arm, dict)], self.r)
            if isinstance(base, dict) and isinstance(key, (int, str)):
                return base.get(key, None)
            return Opaque('index')
        if k == 'call':
            return self.call(n.a.lower(), [self.ev(a) for a in n.b], n)
        if k == 'scall':
            return self.scall(n)
        if k == 'mcall':
            if self.walker is not None:
                return self.walker.mcall(self, n)
            return Opaque('mcall')
        if k == 'dscall':
            if self.walker is not None:
                return self.walker.dscall(self, n)
            return Opaque('dscall')
        if k == 'assign' and self.walker is not None:
            return self.walker.assign(self, n)
        if k in ('isset', 'empty'):
            for a in n.a:
                self.ev(a)
            return Opaque(k)
        if k == 'cast':
            v = self.ev(n.b)
            if n.a == 'array' and isinstance(v, Req):
                return v
            return Opaque('cast')
        return Opaque(k)

    def call(self, name: str, args: list, n: Node) -> Any:
        if self.walker is not None:
            self.walker.on_call(self, name, args, n)
        if name == 'get_class' and len(args) == 1 and isinstance(args[0], This) and self.called:
            # get_class($this): the service the shared method runs for (CGraphGeneral.php 8.0:829)
            c = self.r.cls(self.called)
            return c.name if c is not None else self.called
        try:
            if name == 'implode' and len(args) == 2:
                sep, parts = (args[0], args[1]) if isinstance(args[0], str) else (args[1], args[0])
                if isinstance(parts, dict) and all(isinstance(v, (int, str)) and not isinstance(v, bool) for v in parts.values()):
                    return str(sep).join(str(v) for v in parts.values())
                return Opaque('implode')
            if name == 'range' and len(args) >= 2 and all(isinstance(a, int) for a in args):
                step = args[2] if len(args) > 2 else 1
                return _list(list(range(args[0], args[1] + (1 if step > 0 else -1), step)))
            if name == 'array_merge':
                if all(isinstance(a, dict) for a in args):
                    out: dict = {}
                    nxt = 0
                    for a in args:
                        for key, v in a.items():
                            if isinstance(key, int):
                                out[nxt] = v
                                nxt += 1
                            else:
                                out[key] = v
                    return out
                if all(isinstance(a, (dict, Req)) for a in args) and any(isinstance(a, Req) for a in args):
                    # $selements = array_merge($selements, $maps[$key]['selements']) (CMap.php 7.0:1760): a local list
                    # that now holds the request's elements
                    out = {}
                    for a in args:
                        for v in (a.values() if isinstance(a, dict) else [Req(a.path + ('[]',), a.partial)]):
                            out[len(out)] = v
                    return out
                return Opaque('array_merge')
            if name == 'array_diff' and args and all(isinstance(a, dict) for a in args):
                drop = {v for a in args[1:] for v in a.values() if isinstance(v, (int, str))}
                return _list([v for v in args[0].values() if v not in drop])
            if name == 'array_keys' and len(args) == 1 and isinstance(args[0], dict):
                out = _list([k2 for k2 in args[0].keys() if k2 not in (OPAQUE_KEYS, ALTERNATIVES)])
                if OPAQUE_KEYS in args[0]:
                    out[OPAQUE_KEYS] = True
                return out
            if name == 'array_values' and len(args) == 1:
                return _list(list(args[0].values())) if isinstance(args[0], dict) else args[0] if isinstance(args[0], Req) else Opaque('av')
            if name == 'array_flip' and len(args) == 1 and isinstance(args[0], dict):
                return {v: i for i, v in args[0].items() if isinstance(v, (int, str))}
            if name == 'array_fill_keys' and len(args) == 2 and isinstance(args[0], dict):
                out = {v: args[1] for k2, v in args[0].items() if k2 != OPAQUE_KEYS and isinstance(v, (int, str))}
                if OPAQUE_KEYS in args[0]:
                    out[OPAQUE_KEYS] = True
                return out
            if name == 'array_intersect_key' and args and isinstance(args[0], Req):
                return Req(args[0].path, True)
            if name in ('zbx_toarray', 'array_diff_key') and args and isinstance(args[0], Req):
                return args[0]
            if name == 'zbx_toarray' and args and isinstance(args[0], dict):
                return args[0]   # already a list (CMapElement::createSelements, 7.0:352)
            if name == 'array_intersect_key' and len(args) == 2 and isinstance(args[0], dict) and isinstance(args[1], dict):
                return {k2: v for k2, v in args[0].items() if k2 in args[1]}
            if name == 'zbx_array_merge' and len(args) == 2 and isinstance(args[1], Req):
                return args[1]
            if name == 'count' and len(args) == 1 and isinstance(args[0], dict):
                return len(args[0])
            if name in ('_', '_s') and args and isinstance(args[0], str):
                return args[0]
            if name == 'get_class' and args and isinstance(args[0], This):
                return self.called
            if name == 'max' and args and all(isinstance(a, int) for a in args):
                return max(args)
        except (TypeError, ValueError, AttributeError):
            return Opaque(name)
        fn = self.r.functions.get(name)
        if fn is not None and self.walker is None:
            return pure(self.r, fn, None, None, args)
        return Opaque(name + '()')

    def scall(self, n: Node) -> Any:
        cname, member, arg_nodes = n.a, n.b, n.c
        low, m = cname.lower(), member.lower()
        if low == 'app' and m == 'getmode':
            # api_jsonrpc.php runs in API mode: what an internal call adds is not the API's (CProxy.php 8.0:86)
            return self.r.const('APP', 'EXEC_MODE_API')
        if low == 'db':
            args = [self.ev(a) for a in arg_nodes]
            if m == 'getfieldlength' and len(args) == 2:
                try:
                    return self.r.schema[args[0]]['fields'][args[1]]['length']
                except (KeyError, TypeError):
                    return Opaque('length')
            if m in ('getfilterfields', 'getsearchfields') and args:
                return self.r.filter_fields(args[0], args[1] if len(args) > 1 else None, 'filter' if m == 'getfilterfields' else 'search')
            if m == 'getschema' and args and isinstance(args[0], str):
                return self.r.schema.get(args[0], Opaque('schema'))
            if m == 'getupdatedvalues' and len(args) >= 2 and isinstance(args[0], str) and isinstance(args[1], Req) and self.walker is not None:
                # DB::getUpdatedValues('sysmaps', $map, $db_map): the request's columns of that table are what is stored
                self.walker.facts.stores.add((_strip(args[1].path), args[0]))
                return Opaque('updated-values')
            if self.walker is not None:
                self.walker.on_db(self, m, args, n)
            return Opaque('db')
        if self.walker is not None:
            return self.walker.scall(self, n)
        c = self.resolve(cname)
        fn = self.r.method(c, member)
        if fn is None:
            return Opaque('scall')
        return pure(self.r, fn, fn.cls, self.called if low in ('self', 'static', 'parent') else c, [self.ev(a) for a in arg_nodes])


def pure(reader: Reader, fn: Function, cls: str | None, called: str | None, args: list, depth: int = 0) -> Any:
    """A helper whose body is straight-line code ending in one ``return`` (``CUserMacro::getOutputFieldsOnHost``)."""
    if depth > 4:
        return Opaque('depth')
    body = fn.parsed_body()
    env = {}
    for i, (name, default, _) in enumerate(fn.params):
        env[name] = args[i] if i < len(args) else (Eval(reader, cls, called, {}).ev(default) if default is not None else Opaque('arg'))
    e = Eval(reader, cls or fn.cls, called or fn.cls, env)
    for stmt in body:
        if stmt.kind == 'return':
            return e.ev(stmt.a)
        if stmt.kind == 'expr' and stmt.a is not None and stmt.a.kind == 'assign' and stmt.a.a == '=' and stmt.a.b.kind == 'var':
            env[stmt.a.b.a] = e.ev(stmt.a.c)
            continue
        return Opaque('impure')
    return Opaque('noreturn')


def _str(v: Any) -> str:
    if isinstance(v, bool):
        return '1' if v else ''
    if isinstance(v, float) and v.is_integer():
        return str(int(v))
    return str(v)


# ----------------------------------------------------------------------------------------------------------------------
# the walk


class Walker:
    """Walks one API method: its body and every helper it calls, collecting ``MethodFacts``."""

    MAX_DEPTH = 10

    def __init__(self, reader: Reader, facts: MethodFacts, service_cls: str, api_methods: set[str]):
        self.r = reader
        self.facts = facts
        self.service_cls = service_cls
        self.api_methods = api_methods
        self.stack: list[str] = []
        self.seen: set = set()
        self.memo: dict = {}
        self.conditions = 0   # > 0 while the condition of an `if (…) { self::exception(…) }` is evaluated: array_diff_key
                              # there is an allow-list check (CDRule.php 8.0:207), elsewhere it only drops keys
        self.branch_depth = 0  # > 0 inside an arm this reader cannot decide: a validation there is conditional

    def run(self, fn: Function) -> None:
        args = [Req(()) if i == 0 else Opaque('arg') for i in range(len(fn.params))]
        self.invoke(fn, self.service_cls, args)

    def invoke(self, fn: Function, called: str | None, args: list) -> Any:
        sig = (fn.cls, fn.name.lower(), called, tuple(_sig(a) for a in args), self.branch_depth > 0)
        if sig in self.memo:
            return self.memo[sig]
        if sig in self.seen or len(self.stack) > self.MAX_DEPTH:
            return Opaque('seen')
        self.seen.add(sig)
        self.stack.append(fn.name)
        env: dict = {}
        e = Eval(self.r, fn.cls, called, env, self, fn)
        for i, (name, default, _) in enumerate(fn.params):
            env[name] = args[i] if i < len(args) else (e.ev(default) if default is not None else Opaque('arg'))
        returns: list = []
        try:
            self.block(fn.parsed_body(), e, returns)
        finally:
            self.stack.pop()
        known_returns = [r for r in returns if known(r)]
        if len(known_returns) == 1 and len(returns) == 1:
            result = known_returns[0]
        elif returns and all(isinstance(r, dict) for r in returns):
            result = alternatives(returns, self.r)
        else:
            result = next((r for r in returns if isinstance(r, Req)), Opaque('return'))
        self.memo[sig] = result
        return result

    # -- statements

    def block(self, stmts: list[Node], e: Eval, returns: list) -> bool:
        """Runs the statements; True when every path through them ended (a return, a throw, ``self::exception``)."""
        for s in stmts:
            if self.stmt(s, e, returns):
                return True
        return False

    def stmt(self, s: Node, e: Eval, returns: list) -> bool:   # noqa: C901
        k = s.kind
        if k == 'expr':
            e.ev(s.a)
            return s.a is not None and s.a.kind == 'scall' and s.a.b.lower() == 'exception'
        if k == 'return':
            returns.append(e.ev(s.a) if s.a is not None else None)
            return True
        if k == 'throw':
            return True
        if k == 'block':
            return self.block(s.a, e, returns)
        if k == 'if':
            arms: list[list[Node]] = []
            decided = False
            for cond, body in s.a:
                self.mandatory_by_hand(cond, body, e)
                throws = bool(body) and (body[0].kind == 'throw' or (body[0].kind == 'expr' and body[0].a is not None
                                                                       and body[0].a.kind == 'scall' and body[0].a.b.lower() == 'exception'))
                self.conditions += 1 if throws else 0
                try:
                    c = e.ev(cond)
                finally:
                    self.conditions -= 1 if throws else 0
                if c is False:
                    continue
                arms.append(body)
                if c is True:
                    decided = True
                    break
            if not decided:
                arms.append(s.b or [])
            return self.branches(arms, e, returns)
        if k == 'foreach':
            subject = e.ev(s.a)
            if s.b is not None and s.b.kind == 'var':
                e.env[s.b.a] = Opaque('key')
            element = Req(subject.path + ('[]',)) if isinstance(subject, Req) else Opaque('element')
            if isinstance(subject, dict) and subject and s.c.kind == 'var':
                for v in list(subject.values())[:8]:
                    e.env[s.c.a] = v
                    self.body(s.d, e, returns)
                return False
            if s.c.kind == 'var':
                e.env[s.c.a] = element
            if isinstance(subject, Req):
                # once per element of the request: what it validates, it validates for every element
                self.block(s.d, e, returns)
            else:
                self.body(s.d, e, returns)
            return False
        if k == 'loop':
            self.body(s.a, e, returns)
            return False
        if k == 'while':
            e.ev(s.a)
            self.body(s.b, e, returns)
            return False
        if k == 'switch':
            subject = e.ev(s.a)
            chosen = None
            if known(subject):
                for i, (value, _) in enumerate(s.b):
                    if value is not None and e.ev(value) == subject:
                        chosen = i
                        break
                if chosen is None:
                    chosen = next((i for i, (value, _) in enumerate(s.b) if value is None), None)
                    if chosen is None:
                        return False
            if chosen is not None:
                # fall through from the chosen case until a break or the end of a path
                for body in [b for _, b in s.b][chosen:]:
                    breaks = any(b.kind == 'break' for b in body)
                    if self.block([b for b in body if b.kind != 'break'], e, returns):
                        return True
                    if breaks:
                        return False
                return False
            arms = []
            for i in range(len(s.b)):
                # each case with its fall-through
                arm: list[Node] = []
                for _, body in s.b[i:]:
                    arm += [b for b in body if b.kind != 'break']
                    if any(b.kind == 'break' for b in body) or _ends(body):
                        break
                arms.append(arm)
            if not any(value is None for value, _ in s.b):
                arms.append([])
            return self.branches(arms, e, returns)
        if k == 'try':
            ended = self.block(s.a, e, returns)
            for h in s.b:
                self.body(h, e, returns)
            self.block(s.c, e, returns)
            return ended and not s.b
        return False

    def body(self, stmts: list[Node], e: Eval, returns: list) -> None:
        """A loop's body: it may run or not, so what it validates is conditional."""
        self.branch_depth += 1
        try:
            self.branches([stmts, []], e, returns)
        finally:
            self.branch_depth -= 1

    def branches(self, arms: list[list[Node]], e: Eval, returns: list) -> bool:
        """Arms of a branch this reader cannot decide: each runs on its own copy of the variables, and a variable the
        arms leave different holds their alternatives afterwards (``$api_input_rules = self::getValidationRules()`` in
        one arm, ``self::getInheritedValidationRules()`` in the other: ``CHostPrototype.php`` 7.0:606-612). What an arm
        validates is conditional, unless every other arm ends (``if ($x) { throw … }``)."""
        if len(arms) == 1:
            return self.block(arms[0], e, returns)
        live = [body for body in arms if not _ends(body)]
        before = e.env
        outs = []
        conditional = len(live) > 1
        for body in arms:
            e.env = dict(before)
            if conditional:
                self.branch_depth += 1
            try:
                ended = self.block(body, e, returns)
            finally:
                if conditional:
                    self.branch_depth -= 1
            if not ended:
                outs.append(e.env)
        if not outs:
            e.env = before
            return True
        merged: dict = {}
        for name in {n for env in outs for n in env}:
            values = [env.get(name, before.get(name, Opaque('unset'))) for env in outs]
            first = values[0]
            merged[name] = first if all(v is first or _same(first, v) for v in values[1:]) else alternatives(values, self.r)
        e.env = merged
        return False

    def mandatory_by_hand(self, cond: Node, body: list[Node], e: Eval) -> None:
        """``if (!array_key_exists('name', $drule) …) { self::exception(…) }`` (``CDRule::validateCreate``, 8.0:210)."""
        if self.branch_depth:
            return   # checked only on some paths (CMap.php 8.0:947: a custom label's string, when the label is custom)
        first = cond
        while first.kind == 'bin' and first.a in ('||', 'or'):
            first = first.b
        if not body or not (body[0].kind == 'throw' or (body[0].kind == 'expr' and body[0].a is not None and body[0].a.kind == 'scall' and body[0].a.b.lower() == 'exception')):
            return
        if first.kind == 'unary' and first.a == '!' and first.b.kind == 'call' and first.b.a.lower() == 'array_key_exists' and len(first.b.b) == 2:
            key = e.ev(first.b.b[0])
            subject = e.ev(first.b.b[1])
        elif first.kind == 'unary' and first.a == '!' and first.b.kind == 'isset' and len(first.b.a) == 1 and first.b.a[0].kind == 'index' and first.b.a[0].b is not None:
            # if (!isset($data['discoveryids']) || …) { self::exception(…) } (CDiscoveryRule::copy, 7.0:3246)
            key = e.ev(first.b.a[0].b)
            subject = e.ev(first.b.a[0].a)
        else:
            return
        if isinstance(key, str) and isinstance(subject, Req):
            self.facts.mandatory.append((_strip(subject.path), {key}, self.r.where(e.fn, cond.pos)))

    # -- expressions with effects

    def assign(self, e: Eval, n: Node) -> Any:
        op, target, value_node = n.a, n.b, n.c
        value = e.ev(value_node)
        if target.kind == 'var':
            if op == '=':
                e.env[target.a] = value
            elif op == '+=':
                cur = e.env.get(target.a)
                if isinstance(cur, dict) and isinstance(value, dict):
                    e.env[target.a] = {**value, **cur}
                elif isinstance(cur, Req) and isinstance(value, dict):
                    if cur.path == () and self.facts.method == 'get':
                        self.facts.def_options.update({k: v for k, v in value.items() if isinstance(k, str)})
                    else:
                        self.facts.injected.update((_strip(cur.path), str(k)) for k in value if isinstance(k, str))
                elif not isinstance(cur, Req):
                    e.env[target.a] = Opaque('+=')
            else:
                e.env[target.a] = Opaque(op)
            return value
        if target.kind == 'index' and target.b is None and target.a.kind == 'var' and op == '=':
            # $ins_graph_items[] = $graph_item: a local list the method later hands to DB::insert
            cur = e.env.get(target.a.a)
            items = dict(cur) if isinstance(cur, dict) else {}
            items[len(items)] = value
            e.env[target.a.a] = items
            return value
        if target.kind == 'index':
            # $rules['fields'] += [...], $rules['fields']['x'] = [...]: build up a rule held in a local variable
            keys: list = []
            base = target
            while base.kind == 'index':
                keys.append(e.ev(base.b) if base.b is not None else None)
                base = base.a
            keys.reverse()
            if base.kind == 'var' and base.a in e.env and isinstance(e.env[base.a], dict) and all(isinstance(x, (int, str)) for x in keys):
                # copy on write along the path: the arms of a branch share the dicts they did not change
                holder = dict(e.env[base.a])
                e.env[base.a] = holder
                for key in keys[:-1]:
                    nxt = holder.get(key)
                    if isinstance(nxt, dict):
                        nxt = dict(nxt)
                    else:
                        # built on a value this reader does not know: whatever is added, more may be there
                        nxt = {OPAQUE_KEYS: True} if isinstance(nxt, Opaque) else {}
                    holder[key] = nxt
                    holder = nxt
                last = keys[-1]
                if op == '=':
                    holder[last] = value
                elif op == '+=' and isinstance(value, dict):
                    cur = holder.get(last)
                    if isinstance(cur, dict):
                        holder[last] = {**value, **cur}
                    elif isinstance(cur, Opaque):
                        holder[last] = {OPAQUE_KEYS: True, **value}
                    else:
                        holder[last] = value
                elif op == '+=' and isinstance(value, Opaque):
                    cur = holder.get(last)
                    if isinstance(cur, dict):
                        holder[last] = {**cur, OPAQUE_KEYS: True}
                elif op == '|=' and isinstance(value, int):
                    # $api_input_rules['fields']['name']['flags'] |= API_REQUIRED (CScript.php 8.0:677)
                    cur = holder.get(last)
                    holder[last] = (int(cur) if isinstance(cur, int) else 0) | int(value)
            elif base.kind == 'var' and isinstance(e.env.get(base.a), Req) and keys and isinstance(keys[-1], str) and op in ('=', '+=', '??='):
                # the method writes into the request ($item['host_status'] = …, $triggers[$i]['host_status'] = …): its
                # bookkeeping, not an input
                inner = tuple(k2 if isinstance(k2, str) else '[]' for k2 in keys[:-1])
                self.facts.injected.add((_strip(e.env[base.a].path + inner), keys[-1]))
        return value

    def on_call(self, e: Eval, name: str, args: list, n: Node) -> None:
        if name == 'array_diff_key' and self.conditions and len(args) >= 2 and isinstance(args[0], Req) and isinstance(args[1], dict):
            self.facts.allowed.append((_strip(args[0].path), {str(k) for k in args[1].keys()}, self.r.where(e.fn, n.pos)))
        elif name == 'check_db_fields' and not self.branch_depth and len(args) == 2 and isinstance(args[0], dict) and isinstance(args[1], Req):
            keys = {str(k) for k, v in args[0].items() if v is None}
            if keys:
                self.facts.mandatory.append((_strip(args[1].path), keys, self.r.where(e.fn, n.pos)))
        elif name == 'zbx_array_merge' and len(args) == 2 and isinstance(args[0], dict) and isinstance(args[1], Req) and args[1].path == ():
            self.facts.def_options.update({k: v for k, v in args[0].items() if isinstance(k, str)})
        elif name == 'array_key_exists' and len(args) == 2 and isinstance(args[0], str) and isinstance(args[1], Req):
            self.facts.read_off.setdefault(_strip(args[1].path), set()).add(args[0])

    def on_db(self, e: Eval, m: str, args: list, n: Node) -> None:
        if m not in ('insert', 'insertbatch', 'save', 'update', 'updatebypk', 'replace') or len(args) < 2 or not isinstance(args[0], str):
            return
        data = args[1]
        if isinstance(data, dict) and isinstance(data.get('values'), Req):
            data = data['values']         # DB::update('graphs', ['values' => $graph, 'where' => …])
        elif isinstance(data, dict):
            # a list of those, or of request elements ($ins_graph_items[] = $graph_item)
            data = next((v['values'] if isinstance(v, dict) else v for v in data.values()
                         if isinstance(v, Req) or (isinstance(v, dict) and isinstance(v.get('values'), Req))), data)
        if isinstance(data, Req):
            self.facts.stores.add((_strip(data.path), args[0]))

    def scall(self, e: Eval, n: Node) -> Any:
        cname, member, arg_nodes = n.a, n.b, n.c
        low, m = cname.lower(), member.lower()
        args = [e.ev(a) for a in arg_nodes]
        if low == 'capiinputvalidator':
            if m == 'validate' and len(args) >= 2 and isinstance(args[1], Req):
                self.facts.rules.append(Rule(args[0], args[1].path, self.r.rel(e.fn.file) if e.fn else '?', line_of(self.r.sources.get(e.fn.file, ''), n.pos) if e.fn else 0, args[1].partial,
                                       self.branch_depth > 0))
            return Opaque('validated')
        if low in ('self', 'static', 'parent'):
            target_cls = e.resolve(cname)
            called = e.called
        else:
            if low in BUILTIN_SKIP or (low in self.r.service_classes and not self.r.is_a(self.service_cls, cname)):
                c = self.r.cls(cname)
                fn = self.r.method(cname, member) if c is not None else None
                return pure(self.r, fn, fn.cls, cname, args) if fn is not None and low not in ('db', 'api') else Opaque('skip')
            target_cls = cname
            called = cname
        if m in self.api_methods and m != self.facts.method and self.r.is_a(self.service_cls, target_cls or ''):
            return Opaque('api-method')
        fn = self.r.method(target_cls, member)
        if fn is None:
            return Opaque('nofn')
        return self.invoke(fn, called, args)

    def dscall(self, e: Eval, n: Node) -> Any:
        """``$item_type::getUpdateValidationRules($db_item)``: the class is a run-time value; every class that defines
        the method is an alternative (the item types of ``CItemTypeFactory``)."""
        subject = e.ev(n.a)
        args = [e.ev(a) for a in n.c]
        if isinstance(subject, str):
            fn = self.r.method(subject, n.b)
            return self.invoke(fn, subject, args) if fn is not None else Opaque('dscall')
        defining = [c for c in self.r.classes.values() if n.b.lower() in c.methods and c.methods[n.b.lower()]._span is not None and not c.abstract and c.kind == 'class']
        if not defining or len(defining) > 40:
            return Opaque('poly')
        return alternatives([self.invoke(c.methods[n.b.lower()], c.name, args) for c in sorted(defining, key=lambda c: c.name)], self.r)

    def mcall(self, e: Eval, n: Node) -> Any:
        subject = e.ev(n.a)
        args = [e.ev(a) for a in n.c]
        if isinstance(subject, This):
            m = n.b.lower()
            if m in self.api_methods and m != self.facts.method:
                return Opaque('api-method')
            fn = self.r.method(e.called or e.cls, n.b)
            if fn is None:
                return Opaque('nofn')
            return self.invoke(fn, e.called or e.cls, args)
        return Opaque('mcall')


def alternatives(values: list, reader: Reader) -> Any:
    """Values one expression may have (both arms of a ternary, every class a polymorphic call may land on). Rules
    become the arms of an ``API_MULTIPLE`` (so a field is required only when every arm requires it); maps of rules are
    united key by key; anything else is unknown."""
    flat: list = []
    for v in values:
        if isinstance(v, Opaque) and not isinstance(v, Req):
            continue
        for x in (list(v['rules'].values()) if isinstance(v, dict) and v.get(ALTERNATIVES) else [v]):
            if not any(x is y for y in flat):
                flat.append(x)
    values = flat
    if not values:
        return Opaque('alternatives')
    if len(values) == 1:
        return values[0]
    if all(isinstance(v, dict) for v in values):
        if all(isinstance(v.get('type'), int) and not isinstance(v.get('type'), bool) for v in values):
            multiple = reader.define('API_MULTIPLE')
            if isinstance(multiple, int):
                return {'type': multiple, 'rules': {i: v for i, v in enumerate(values)}, ALTERNATIVES: True}
        if all(v and all(isinstance(k, int) for k in v) and sorted(v) == list(range(len(v))) for v in values) \
                and all(isinstance(x, (str, int)) for v in values for x in v.values()):
            # lists of names ($output_fields, one per user type: CMediatype.php 8.0:105-112): every name of any
            union: list = []
            for v in values:
                union += [x for x in v.values() if x not in union]
            return _list(union)
        out: dict = {}
        for v in values:
            for k, x in v.items():
                out[k] = alternatives([out[k], x], reader) if k in out else x
        # a rule only some alternatives have: the others refuse the field (it is conditional, never required)
        unexpected = reader.define('API_UNEXPECTED')
        multiple = reader.define('API_MULTIPLE')
        for k, x in list(out.items()):
            if isinstance(x, dict) and isinstance(x.get('type'), int) and not x.get(ALTERNATIVES) and isinstance(unexpected, int) \
                    and isinstance(multiple, int) and not all(k in v for v in values):
                out[k] = {'type': multiple, 'rules': {0: x, 1: {'type': unexpected}}, ALTERNATIVES: True}
        return out
    if all(isinstance(v, Req) for v in values):
        return values[0]
    return Opaque('alternatives')


def _sig(a: Any) -> Any:
    """What a call's argument contributes to its memo key: a request path, a scalar, or nothing."""
    if isinstance(a, Req):
        return ('req', a.path, a.partial)
    if isinstance(a, (int, str, bool)) or a is None:
        return a
    return type(a).__name__


def _ends(body: list[Node]) -> bool:
    """An arm that ends in a throw, an exception or a return leaves no variables behind."""
    if not body:
        return False
    last = body[-1]
    if last.kind in ('throw', 'return'):
        return True
    return last.kind == 'expr' and last.a is not None and last.a.kind == 'scall' and last.a.b.lower() == 'exception'


def _same(a: Any, b: Any) -> bool:
    if isinstance(a, Req) and isinstance(b, Req):
        return a.path == b.path and a.partial == b.partial
    if isinstance(a, Opaque) and isinstance(b, Opaque):
        return True
    if isinstance(a, dict) or isinstance(b, dict):
        return a is b
    try:
        return a == b
    except Exception:   # noqa: BLE001
        return False


def _union(dicts: list[dict]) -> dict:
    out: dict = {}
    for d in dicts:
        for k, v in d.items():
            if k in out and isinstance(out[k], dict) and isinstance(v, dict):
                out[k] = _union([out[k], v])
            elif k not in out:
                out[k] = v
    return out


def _strip(path: tuple) -> tuple:
    return tuple(p for p in path if p != '[]')


# ----------------------------------------------------------------------------------------------------------------------
# rules as a tree of field facts


@dataclass
class FieldFact:
    type: str
    required: bool = False          # unconditionally required (every arm, where there are arms)
    conditional: bool = False       # required in some arm only
    values: set | None = None       # 'in' integer values
    ranges: list | None = None
    strings: list | None = None
    length: int | None = None
    deprecated: bool = False
    replacement: str | None = None
    fields: dict | None = None      # nested object fields (name -> FieldFact)
    open: bool = False              # nested object with API_ALLOW_UNEXPECTED
    filterable: list | None = None
    count: bool = False
    where: str = ''


class Vocab:
    def __init__(self, reader: Reader):
        self.types: dict[int, str] = {}
        self.flags: dict[str, int] = {}
        text = open(os.path.join(reader.ui, 'defines.inc.php'), encoding='utf-8', errors='replace').read()
        start = text.index("define('API_MULTIPLE'")
        flags_at = text.index('// flags', start)
        for name in re.findall(r"define\('(API_\w+)'", text[start:flags_at]):
            v = reader.define(name)
            if isinstance(v, int):
                self.types[v] = name[4:].lower()
        end = text.index('\n\n', text.index("define('API_", flags_at))
        for name in re.findall(r"define\('(API_\w+)'", text[flags_at:end]):
            v = reader.define(name)
            if isinstance(v, int):
                self.flags[name[4:].lower()] = v

    def has(self, rule: dict, flag: str) -> bool:
        f = rule.get('flags')
        return isinstance(f, int) and bool(f & self.flags.get(flag, 0))

    def type_of(self, rule: dict) -> str:
        t = rule.get('type')
        return self.types.get(t, 'unknown') if isinstance(t, int) and not isinstance(t, bool) else 'unknown'


def in_values(v: Any) -> tuple[set | None, list | None, list | None]:
    if isinstance(v, bool) or v is None:
        return None, None, None
    if isinstance(v, int):
        return {v}, None, None
    if not isinstance(v, str):
        return None, None, None
    values, ranges, strings = set(), [], []
    for part in v.split(','):
        part = part.strip()
        if re.fullmatch(r'-?\d+', part):
            values.add(int(part))
        elif re.fullmatch(r'-?\d*:-?\d*', part):
            ranges.append(part)
        elif part:
            strings.append(part)
    return values or None, ranges or None, strings or None


def to_fact(vocab: Vocab, rule: Any, where: str) -> FieldFact | None:
    if not isinstance(rule, dict):
        return None
    t = vocab.type_of(rule)
    f = FieldFact(t, where=where)
    f.required = vocab.has(rule, 'required')
    f.deprecated = vocab.has(rule, 'deprecated')
    f.count = vocab.has(rule, 'allow_count')
    if isinstance(rule.get('replacement'), str):
        f.replacement = rule['replacement']
    f.values, f.ranges, f.strings = in_values(rule.get('in'))
    if isinstance(rule.get('length'), int) and not isinstance(rule.get('length'), bool):
        f.length = rule['length']
    if t in ('object', 'objects') and isinstance(rule.get('fields'), dict):
        f.fields = {str(k): x for k, v in rule['fields'].items() if k != OPAQUE_KEYS and (x := to_fact(vocab, v, where)) is not None}
        f.open = vocab.has(rule, 'allow_unexpected') or OPAQUE_KEYS in rule['fields']
    elif t in ('object', 'objects') and isinstance(rule.get('fields'), Opaque):
        f.fields = {}
        f.open = True
    if t == 'filter' and isinstance(rule.get('fields'), dict):
        f.filterable = [str(v) for v in rule['fields'].values() if isinstance(v, str)]
    if t == 'multiple' and isinstance(rule.get('rules'), dict):
        arms = [to_fact(vocab, a, where) for a in rule['rules'].values()]
        arms = [a for a in arms if a is not None]
        # an arm that takes only an empty list (['type' => API_OBJECTS, 'length' => 0], CUserDirectory.php 8.0) admits no
        # element: what the elements are, and what an element must carry, is the other arms' alone
        live = [a for a in arms if a.type != 'unexpected' and not (a.type == 'objects' and a.length == 0 and not a.fields)] \
            or [a for a in arms if a.type != 'unexpected']
        if live:
            merged = _merge_arms(live)
            # API_REQUIRED on the API_MULTIPLE itself (CCorrelation.php 8.0:543), or on every arm
            merged.required = vocab.has(rule, 'required') or all(a.required for a in arms)
            merged.conditional = any(a.required for a in arms) and not merged.required
            merged.where = where
            return merged
        return FieldFact('unexpected', where=where)
    return f


def _merge_arms(arms: list[FieldFact]) -> FieldFact:
    base = FieldFact(arms[0].type)
    types = {a.type for a in arms}
    base.type = arms[0].type if len(types) == 1 else 'multiple:' + ','.join(sorted(types))
    base.values = set().union(*[a.values for a in arms if a.values]) if any(a.values for a in arms) else None
    base.length = max((a.length for a in arms if a.length), default=None)
    base.deprecated = all(a.deprecated for a in arms)
    if any(a.fields is not None for a in arms):
        names: list[str] = []
        for a in arms:
            names += [n for n in (a.fields or {}) if n not in names]
        base.fields = {}
        for name in names:
            present = [a.fields[name] for a in arms if a.fields and name in a.fields]
            live = [p for p in present if p.type != 'unexpected']
            if not live:
                continue
            m = _merge_arms(live)
            m.required = len(present) == len(arms) and all(p.required for p in present)
            m.conditional = any(p.required for p in present) and not m.required
            m.where = present[0].where
            base.fields[name] = m
        base.open = any(a.open for a in arms)
    return base


def _conditional(f: FieldFact) -> None:
    """A validation the method runs only on some paths: what it requires, it requires only then (its nested objects'
    own requirements stand, relative to the object)."""
    if f.required or f.type == 'unexpected':
        f.required, f.conditional = False, True
    for x in (f.fields or {}).values():
        if x.required or x.type == 'unexpected':
            x.required, x.conditional = False, True


def merge_seq(a: FieldFact, b: FieldFact) -> FieldFact:
    """Two validations of the same request path: every field of either; required when either requires."""
    strict_b = b.fields is not None and not b.open and (a.fields is None or a.open)
    weak = ('any', 'unknown')
    t = b.type if a.type in weak or (a.type == 'unexpected' and a.conditional and b.type not in weak) else a.type
    out = FieldFact(t, where=(b.where if strict_b else a.where) or b.where)
    out.required = a.required or b.required
    out.conditional = (a.conditional or b.conditional) and not out.required
    out.values = a.values or b.values
    out.strings = a.strings or b.strings
    out.ranges = a.ranges or b.ranges
    out.length = a.length or b.length
    out.deprecated = a.deprecated or b.deprecated
    out.replacement = a.replacement or b.replacement
    out.count = a.count or b.count
    out.filterable = a.filterable or b.filterable
    if a.fields is not None or b.fields is not None:
        out.fields = dict(a.fields or {})
        for k, v in (b.fields or {}).items():
            out.fields[k] = merge_seq(out.fields[k], v) if k in out.fields else v
        # one strict validation of the path makes it strict
        out.open = (a.open or a.fields is None) and (b.open or b.fields is None)
    return out


# ----------------------------------------------------------------------------------------------------------------------
# one version, read


def read_version(root: str, version: str) -> tuple[Reader, Vocab, dict]:
    reader = Reader(root, version)
    vocab = Vocab(reader)
    anonymous = _auth_pairs(reader)
    out: dict = {}
    for obj, cls in sorted(reader.services.items()):
        access = reader.const(cls, 'ACCESS_RULES')
        if not isinstance(access, dict):
            continue
        methods: dict[str, MethodFacts] = {}
        api_methods = {str(m).lower() for m in access}
        for m, rules in access.items():
            m = str(m)
            fn = reader.method(cls, m)
            mut = rules.get('min_user_type') if isinstance(rules, dict) else None
            facts = MethodFacts(obj, m, f'{reader.rel(fn.file)}:{fn.line}' if fn else '?', mut if isinstance(mut, int) else None, (obj, m) not in anonymous)
            if fn is not None:
                Walker(reader, facts, cls, api_methods).run(fn)
            methods[m] = facts
        out[obj] = {'class': cls, 'methods': methods}
    return reader, vocab, out


def _auth_pairs(reader: Reader) -> set:
    fn = reader.method('CLocalApiClient', 'requiresAuthentication')
    if fn is None:
        return set()
    toks = fn._toks[fn._span[0]:fn._span[1]]
    text = ' '.join(t.text if t.kind != 'str' else repr(t.text) for t in toks)
    return set(re.findall(r"\$api === '(\w+)' && \$method === '(\w+)'", text))


def request_tree(vocab: Vocab, facts: MethodFacts) -> tuple[FieldFact | None, dict]:
    """Every validation of the method's request merged into one tree, rooted at the request (paths without ``[]``)."""
    by_path: dict[tuple, FieldFact] = {}
    for r in facts.rules:
        f = to_fact(vocab, r.rule, f'{r.file}:{r.line}')
        if f is None:
            continue
        if r.partial and f.fields is not None:
            f.open = True
        if r.conditional:
            _conditional(f)
        if r.path and r.path[-1] == '[]' and f.type in ('object', 'id', 'string_utf8') and len(r.path) > 1:
            # one element validated at a time (CHostBase::validateMacros, per macro): the field is the list
            f.type = {'object': 'objects', 'id': 'ids', 'string_utf8': 'strings_utf8'}[f.type]
        p = _strip(r.path)
        by_path[p] = merge_seq(by_path[p], f) if p in by_path else f
    root = by_path.get(())
    for p in sorted(by_path, key=len):
        if not p:
            continue
        node = root
        if node is None:
            root = node = FieldFact('object', fields={}, open=True, where=by_path[p].where)
        for part in p[:-1]:
            node.fields = node.fields if node.fields is not None else {}
            if part not in node.fields:
                node.fields[part] = FieldFact('object', fields={}, open=True, where=by_path[p].where)
            node = node.fields[part]
        node.fields = node.fields if node.fields is not None else {}
        node.fields[p[-1]] = merge_seq(node.fields[p[-1]], by_path[p]) if p[-1] in node.fields else by_path[p]
    return root, by_path


# ----------------------------------------------------------------------------------------------------------------------
# the description, per version


def vkey(v: str) -> tuple:
    return tuple(int(x) for x in v.split('.'))


def present_in(entry: dict, version: str) -> bool:
    since, removed = entry.get('since'), entry.get('removed')
    if since and vkey(version) < vkey(since):
        return False
    if removed and vkey(version) >= vkey(removed):
        return False
    return True


class Model:
    def __init__(self, description: dict):
        self.d = description
        self.types = {t['name']: t for t in description['types']}
        self.enums = {e['name']: e for e in description['enums']}
        self.services = {s['object']: s for s in description['services']}

    @staticmethod
    def record_of(kind: str) -> str | None:
        m = re.search(r'object:(\w+)$', kind or '')
        return m.group(1) if m else None

    def fields(self, record: str, version: str) -> dict:
        t = self.types.get(record)
        if t is None:
            return {}
        return {f['wire']: f for f in t['fields'] if present_in(f, version)}


# ----------------------------------------------------------------------------------------------------------------------
# the comparison


SOURCE_KINDS = {
    'id': ('id',), 'cuid': ('string',), 'selementid': ('id',), 'ids': ('list:id',), 'cuids': ('list:string',),
    'int32': ('int', 'enum:', 'bool', 'long', 'clock', 'timeofday'), 'ints32': ('list:int', 'list:enum:'), 'uint64': ('ulong',), 'uints64': ('list:ulong',),
    'float': ('double',), 'floats': ('list:double',), 'boolean': ('apibool', 'bool'), 'flag': ('apibool', 'bool'),
    'object': ('object:',), 'objects': ('list:object:',), 'strings_utf8': ('list:string',), 'user_macros': ('list:string',),
    'colors': ('list:string',), 'filter_values': ('list:string',), 'output': ('output',), 'filter': ('filter',),
    'sortorder': ('list:string',), 'timestamp': ('clock', 'timeofday'), 'date': ('date',),
}
STRING_TYPES = {
    'string_utf8', 'escaped_string_utf8', 'hg_name', 'h_name', 'tg_name', 'script_menu_path', 'user_macro', 'time_period', 'regex',
    'variable_name', 'time_unit', 'url', 'color', 'numeric', 'lld_macro', 'psk', 'calc_formula', 'ip', 'dns', 'port',
    'trigger_expression', 'event_name', 'date', 'numeric_ranges', 'uuid', 'vault_secret', 'ip_ranges', 'image', 'exec_params',
    'cond_formula', 'cond_formulaid', 'int32_ranges', 'lat_lng_zoom', 'item_key', 'item_delay', 'json', 'xml', 'preproc_params',
    'prometheus_pattern', 'prometheus_label', 'host_address', 'number', 'frontend_action', 'uuid_v7',
}


class Report:
    def __init__(self):
        self.lines: dict[str, str] = {}

    def add(self, version: str, code: str, api: str, path: str, message: str, where: str = '') -> None:
        key = f'{version} {code} {api} {path}'
        self.lines.setdefault(key, f'{key}: {message}' + (f' [{where}]' if where else ''))


class Comparer:
    def __init__(self, model: Model, report: Report):
        self.m = model
        self.rep = report
        self.required: dict[tuple, dict[str, bool]] = {}   # (api, path) -> version -> the source requires it
        self.present: dict[tuple, set] = {}                 # (api, path) -> the versions the description has it in
        self.method_versions: dict[str, set] = {}

    def finish(self) -> None:
        """The description's ``required`` means required in every version the field is in: a field the source requires
        only from some version on is not a discrepancy (``tags[].tag`` of the 8.0 ``.get`` tag filters)."""
        for (api, path), by_version in self.required.items():
            present = self.present.get((api, path), set(by_version))
            if not all(by_version.get(v, False) for v in present):
                for v in by_version:
                    self.rep.lines.pop(f'{v} REQUIRED-MISSING {api} {path}', None)

    # -- objects and methods

    def version(self, v: str, reader: Reader, vocab: Vocab, objects: dict) -> None:
        self.schema = reader.schema
        for obj in sorted(set(objects) | set(self.m.services)):
            s = self.m.services.get(obj)
            if obj not in objects:
                if s is not None and present_in(s, v):
                    self.rep.add(v, 'OBJECT-EXTRA', obj, '-', 'the description has the object, the version has no such service')
                continue
            src = objects[obj]
            if s is None or not present_in(s, v):
                self.rep.add(v, 'OBJECT-MISSING', obj, '-', f'the version has {src["class"]}, the description lacks the object')
                continue
            methods = {m['api'].split('.', 1)[1]: m for m in s['methods']}
            for name in sorted(set(src['methods']) | set(methods)):
                api = f'{obj}.{name}'
                mm = methods.get(name)
                facts = src['methods'].get(name)
                if facts is None:
                    if mm is not None and present_in(mm, v):
                        self.rep.add(v, 'METHOD-EXTRA', api, '-', 'the description has the method, ACCESS_RULES does not')
                    continue
                if mm is None or not present_in(mm, v):
                    self.rep.add(v, 'METHOD-MISSING', api, '-', 'ACCESS_RULES has the method, the description does not', facts.source)
                    continue
                self.method_versions[api] = {x for x in VERSIONS if present_in(mm, x) and present_in(s, x)}
                self.method(v, reader, vocab, src['class'], facts, mm, s)

    def method(self, v: str, reader: Reader, vocab: Vocab, cls: str, facts: MethodFacts, mm: dict, service: dict) -> None:
        api = mm['api']
        want = mm.get('minUserTypeByVersion', {}).get(v, mm.get('minUserType'))
        if want != facts.min_user_type:
            self.rep.add(v, 'MIN-USER-TYPE', api, '-', f'ACCESS_RULES says {facts.min_user_type}, the description {want}', facts.source)
        if bool(mm.get('requiresAuthentication', True)) != facts.requires_auth:
            self.rep.add(v, 'AUTH', api, '-', f'requiresAuthentication is {facts.requires_auth} in the source, {mm.get("requiresAuthentication")} in the description')
        tree, _ = request_tree(vocab, facts)
        params = mm.get('parameters', 'none')
        record = Model.record_of(params)
        if facts.method == 'get' and record:
            self.get(v, reader, vocab, cls, facts, tree, record, api, service)
            return
        if record is None:
            if tree is not None and tree.fields:
                self.rep.add(v, 'PARAMS-UNTYPED', api, '/', f'the source validates fields {sorted(tree.fields)[:8]} but the description sends {params}', tree.where)
            return
        self.request(v, vocab, facts, tree, record, api, (), set())

    # -- a request, recursively

    def request(self, v: str, vocab: Vocab, facts: MethodFacts, node: FieldFact | None, record: str, api: str, path: tuple, seen: set) -> None:
        if (record, path) in seen or len(path) > 6:
            return
        seen.add((record, path))
        fields = self.m.fields(record, v)
        where_path = ''.join('/' + p for p in path)
        allowed = [keys for p, keys, _ in facts.allowed if p == path]
        allowed_where = [w for p, _, w in facts.allowed if p == path]
        mandatory = set().union(*[keys for p, keys, _ in facts.mandatory if p == path]) if facts.mandatory else set()
        source_fields = (node.fields if node is not None and node.fields is not None else {})
        strict = node is not None and node.fields is not None and not node.open
        # present in the source, absent from the description
        for name, f in sorted(source_fields.items()):
            if f.type == 'unexpected':
                if name in fields and not f.conditional:
                    self.rep.add(v, 'FIELD-REFUSED', api, f'{where_path}/{name}', 'API_UNEXPECTED in the source, sent by the description', f.where)
                continue
            if name not in fields:
                if (path, name) not in facts.injected:
                    self.rep.add(v, 'FIELD-MISSING', api, f'{where_path}/{name}', f'validated as {f.type} in the source, absent from {record}', f.where)
                continue
            self.field(v, vocab, facts, f, fields[name], record, api, path + (name,), seen, mandatory)
        # allowed by a hand-written allow-list but not by a rule
        for keys in allowed:
            for name in sorted(keys - set(source_fields)):
                if name not in fields:
                    self.rep.add(v, 'FIELD-MISSING', api, f'{where_path}/{name}', f'on the allow-list, absent from {record}', allowed_where[0])
        # a nested list the method stores in a table of its own, with no rule naming it (graph.create's gitems →
        # graphs_items, CGraphGeneral::createReal): present, and its columns (below, one level down)
        for child in sorted({p[len(path)] for p, _ in facts.stores if len(p) > len(path) and p[:len(path)] == path}):
            if child in source_fields:
                continue
            tables = sorted({t for p, t in facts.stores if p == path + (child,)})
            if child not in fields:
                if (path, child) not in facts.injected and tables:
                    self.rep.add(v, 'FIELD-MISSING', api, f'{where_path}/{child}', f'stored in {", ".join(tables)} by the source, absent from {record}', facts.source)
                continue
            nested = Model.record_of(fields[child]['kind'])
            if nested:
                self.request(v, vocab, facts, None, nested, api, path + (child,), seen)
        # an open object stored in a table: the table's columns it could carry (graph.create's gitems → graphs_items)
        if path and (node is None or node.open):
            # (nested only: an open request root takes the object's own columns less those the server maintains,
            # which overlay.json's internalColumns names)
            for table in sorted({t for p, t in facts.stores if p == path}):
                columns = (self.schema.get(table) or {}).get('fields') or {}
                pk = next(iter(columns), None)
                for col, info in columns.items():
                    if col == pk or col in fields or col in source_fields or (path, col) in facts.injected:
                        continue
                    if isinstance(info, dict) and info.get('ref_table') and path and col.endswith('id') and col != 'itemid':
                        continue   # the owner's id (graphs_items.graphid): the method links it
                    self.rep.add(v, 'COLUMN-MISSING', api, f'{where_path}/{col}', f'{record} is stored in {table}, whose column it lacks', facts.source)
        # mandatory by hand
        for name in sorted(mandatory):
            if name in fields and name not in source_fields:
                self.required.setdefault((api, f'{where_path}/{name}'), {})[v] = True
            if name in fields and not fields[name].get('required') and name not in source_fields:
                self.rep.add(v, 'REQUIRED-MISSING', api, f'{where_path}/{name}', f'mandatory by hand (check_db_fields / array_key_exists) in the source, optional in {record}',
                             next(w for p, k, w in facts.mandatory if p == path and name in k))
        # present in the description, refused or unfounded in the source
        for name, fd in sorted(fields.items()):
            if name in source_fields:
                continue
            if allowed:
                if not any(name in keys for keys in allowed):
                    self.rep.add(v, 'FIELD-REFUSED', api, f'{where_path}/{name}', f'{record} sends it; the allow-list refuses it', allowed_where[0])
                continue
            if strict:
                self.rep.add(v, 'FIELD-REFUSED', api, f'{where_path}/{name}', f'{record} sends it; the strict validation does not name it', node.where)
                continue
            stored = {t for p, t in facts.stores if p == path}
            read = facts.read_off.get(path, set())
            if name in read or name in mandatory:
                continue
            if any(name in ((self.schema.get(t) or {}).get('fields') or {}) for t in stored):
                continue   # an open object handed to DB::insert/update: its table's columns are what it takes
            self.rep.add(v, 'FIELD-UNFOUNDED', api, f'{where_path}/{name}', f'{record} sends it; no validation, allow-list or table of the source names it',
                         node.where if node is not None else facts.source)

    def field(self, v: str, vocab: Vocab, facts: MethodFacts, f: FieldFact, fd: dict, record: str, api: str, path: tuple, seen: set, mandatory: set) -> None:
        p = '/' + '/'.join(path)
        kind = fd['kind']
        # kind
        expected = SOURCE_KINDS.get(f.type)
        if f.type in STRING_TYPES and f.type not in SOURCE_KINDS:
            expected = ('string',)
        if expected is not None and not any(kind == e or (e.endswith(':') and kind.startswith(e)) for e in expected):
            if kind in ('json', 'list:json'):
                self.rep.add(v, 'KIND-UNTYPED', api, p, f'{f.type} in the source, untyped json in {record}' + (f' ({fd["json"]})' if fd.get('json') else ''), f.where)
            else:
                self.rep.add(v, 'KIND', api, p, f'{f.type} in the source, {kind} in {record}', f.where)
        # required
        self.required.setdefault((api, p), {})[v] = f.required or path[-1] in mandatory
        self.present[(api, p)] = {x for x in VERSIONS if present_in(fd, x)} & self.method_versions.get(api, set(VERSIONS))
        if f.required and not fd.get('required'):
            self.rep.add(v, 'REQUIRED-MISSING', api, p, f'API_REQUIRED in the source, optional in {record}', f.where)
        if fd.get('required') and not f.required and path[-1] not in mandatory:
            self.rep.add(v, 'REQUIRED-EXTRA', api, p, f'required in {record}, ' + ('conditionally required' if f.conditional else 'optional') + ' in the source', f.where)
        # deprecated
        if f.deprecated and not fd.get('deprecated'):
            self.rep.add(v, 'DEPRECATED-MISSING', api, p, f'API_DEPRECATED in the source, not in {record}', f.where)
        if fd.get('deprecated') and vkey(fd['deprecated']) <= vkey(v) and not f.deprecated:
            self.rep.add(v, 'DEPRECATED-EXTRA', api, p, f'deprecated in {record}, not in the source', f.where)
        # length
        if f.length and kind == 'string' and fd.get('maxLength') and fd['maxLength'] != f.length:
            self.rep.add(v, 'LENGTH', api, p, f'length {f.length} in the source, {fd["maxLength"]} in {record}', f.where)
        # values
        self.values(v, f, kind, api, p, record)
        # nested
        nested = Model.record_of(kind)
        if nested and f.fields is not None:
            self.request(v, vocab, facts, f, nested, api, path, seen)

    def values(self, v: str, f: FieldFact, kind: str, api: str, p: str, record: str) -> None:
        if not f.values:
            return
        base = kind[5:] if kind.startswith('list:') else kind
        if base.startswith('enum:'):
            e = self.m.enums.get(base[5:])
            if e is None:
                return
            members = {m['value'] for m in e['members'] if present_in(m, v)}
            if e.get('flags'):
                return
            missing = sorted(f.values - members)
            if missing:
                self.rep.add(v, 'ENUM-VALUE-MISSING', api, p, f'the source accepts {missing}, {e["name"]} has no member for it', f.where)
        elif base == 'bool':
            if not f.values <= {0, 1}:
                self.rep.add(v, 'BOOL-RANGE', api, p, f'bool in {record}, the source accepts {sorted(f.values)}', f.where)
        elif base == 'int' and len(f.values) >= 2 and not f.ranges:
            self.rep.add(v, 'INT-ENUM', api, p, f'int in {record}, the source accepts only {sorted(f.values)}', f.where)

    # -- a .get

    def get(self, v: str, reader: Reader, vocab: Vocab, cls: str, facts: MethodFacts, tree: FieldFact | None, record: str, api: str, service: dict) -> None:
        fields = self.m.fields(record, v)
        accepted: dict[str, FieldFact | None] = {}
        strict = tree is not None and tree.fields is not None and not tree.open
        for name, f in ((tree.fields or {}) if tree else {}).items():
            accepted[name] = f
        if not strict:
            for name in facts.def_options:
                accepted.setdefault(name, None)
        # the API client takes these out of every external request before a service sees them
        for name in reader.external_ignored():
            accepted.pop(name, None)
        if not accepted:
            return
        for name in sorted(accepted):
            f = accepted[name]
            if f is not None and f.type == 'unexpected':
                continue
            if name not in fields:
                self.rep.add(v, 'OPTION-MISSING', api, f'/{name}', f'the source accepts it, {record} lacks it', f.where if f else facts.source)
                continue
            if f is not None:
                self.field(v, vocab, facts, f, fields[name], record, api, (name,), set(), set())
        for name in sorted(set(fields) - set(accepted)):
            self.rep.add(v, 'OPTION-UNFOUNDED', api, f'/{name}', f'{record} has it; the source ' + ('refuses it (strict)' if strict else 'neither validates nor defaults it, so it is ignored'),
                         tree.where if tree else facts.source)
        # the filter and search whitelists
        t = self.m.types.get(record, {})
        for option, key in (('filter', 'filterable'), ('search', 'searchable')):
            f = accepted.get(option)
            if f is not None and f.filterable is not None:
                have = set(t.get(key) or [])
                want = set(f.filterable)
                if want - have:
                    self.rep.add(v, 'FILTER-MISSING', api, f'/{option}', f'the source whitelists {sorted(want - have)}, {record}.{key} lacks them', f.where)
                # a description whitelist wider than one version's is the union across versions: only report the latest
                if have - want and v == VERSIONS[-1]:
                    self.rep.add(v, 'FILTER-EXTRA', api, f'/{option}', f'{record}.{key} has {sorted(have - want)}, the source refuses them', f.where)
        # the sort whitelist ($sortColumns, CApiService::$sortColumns): sortfield outside it is refused
        sort = reader.prop(cls, 'sortColumns')
        sort_rule = accepted.get('sortfield')
        if sort_rule is not None and sort_rule.strings:
            sort = _list(sort_rule.strings)   # the newer services' own whitelist ('sortfield' => [… 'in' => …])
        if isinstance(sort, dict) and 'sortfield' in fields:
            want = {str(x) for x in sort.values() if isinstance(x, str)}
            have = set(t.get('sortable') or [])
            if want - have:
                self.rep.add(v, 'SORT-MISSING', api, '/sortfield', f'the source sorts by {sorted(want - have)}, {record}.sortable lacks them', facts.source)
            if have - want and v == VERSIONS[-1]:
                self.rep.add(v, 'SORT-EXTRA', api, '/sortfield', f'{record}.sortable has {sorted(have - want)}, the source refuses them', facts.source)
        # the output
        self.output(v, reader, cls, api, service, accepted)

    def output(self, v: str, reader: Reader, cls: str, api: str, service: dict, accepted: dict) -> None:
        model = service.get('model')
        if not model:
            return
        fields = self.m.fields(model, v)
        out = accepted.get('output')
        declared = None
        if out is not None and out.strings:
            declared = out.strings
        else:
            const = reader.const(cls, 'OUTPUT_FIELDS')
            if isinstance(const, dict):
                declared = [str(x) for x in const.values() if isinstance(x, str)]
        if declared:
            for name in declared:
                if name not in fields:
                    self.rep.add(v, 'OUTPUT-MISSING', api, f'/{name}', f'the source outputs it (OUTPUT_FIELDS / output rule), {model} lacks it', out.where if out else '')
        # the relation maps: one or many
        for c in reader.chain(cls):
            src = reader.sources.get(c.file, '')
            for m in re.finditer(r"->(mapMany|mapOne)\(\s*\$\w+\s*,\s*\$\w+\s*,\s*'(\w+)'", src):
                key = m.group(2)
                fd = fields.get(key)
                if fd is None:
                    continue
                many = m.group(1) == 'mapMany'
                is_list = fd['kind'].startswith('list:')
                if many != is_list and fd['kind'] != 'json':
                    self.rep.add(v, 'SELECT-SHAPE', api, f'/{key}', f'{m.group(1)} in the source, {fd["kind"]} in {model}', f'{reader.rel(c.file)}:{line_of(src, m.start())}')


# ----------------------------------------------------------------------------------------------------------------------
# entry


def run(argv: list[str]) -> int:
    here = os.path.dirname(os.path.abspath(__file__))
    next_dir = os.path.normpath(os.path.join(here, '..', '..', '..'))
    parser = argparse.ArgumentParser(prog='zabbix_api_extract verify')
    parser.add_argument('--sources', default=os.environ.get('ZABBIX_SOURCES') or os.path.join(next_dir, '.zabbix-sources'),
                        help='default: $ZABBIX_SOURCES, else .zabbix-sources/ (tools/fetch-zabbix-sources.sh)')
    parser.add_argument('--schema', default=os.path.join(next_dir, 'schema'))
    parser.add_argument('--all', action='store_true', help='print every discrepancy, recorded or not')
    parser.add_argument('--codes', help='only these codes (comma separated)')
    args = parser.parse_args(argv)
    if not os.path.isdir(args.sources):
        parser.error(f'no Zabbix sources at {args.sources}: run tools/fetch-zabbix-sources.sh, or give --sources')
    with open(os.path.join(args.schema, 'zabbix-api.json'), encoding='utf-8') as handle:
        model = Model(json.load(handle))
    report = Report()
    comparer = Comparer(model, report)
    for v in VERSIONS:
        reader, vocab, objects = read_version(os.path.join(args.sources, v), v)
        comparer.version(v, reader, vocab, objects)
    comparer.finish()
    exceptions_path = os.path.join(args.schema, 'verify-exceptions.json')
    recorded: dict[str, str] = {}
    if os.path.exists(exceptions_path):
        with open(exceptions_path, encoding='utf-8') as handle:
            data = json.load(handle)
        for entry in data.get('exceptions', []):
            recorded[entry['key']] = entry['reason']
    codes = set(args.codes.split(',')) if args.codes else None
    lines = sorted(report.lines.items())
    new = [(k, line) for k, line in lines if _match(k, recorded) is None and (codes is None or k.split(' ')[1] in codes)]
    used = {_match(k, recorded) for k, _ in lines}
    stale = sorted(k for k in recorded if k not in used)
    shown = lines if args.all else new
    for k, line in shown:
        if codes is None or k.split(' ')[1] in codes:
            print(line)
    for k in stale:
        print(f'STALE exception (no longer occurs): {k}')
    counts: dict[str, int] = {}
    for k, _ in lines:
        code = k.split(' ')[1]
        counts[code] = counts.get(code, 0) + 1
    print(f'verify: {len(lines)} discrepancies ({len(lines) - len(new)} recorded, {len(new)} new), {len(stale)} stale; '
          + ', '.join(f'{c} {n}' for c, n in sorted(counts.items())), file=sys.stderr)
    return 1 if new or stale else 0


def _match(key: str, recorded: dict[str, str]) -> str | None:
    """A recorded key matches exactly, or as a glob (``* CODE api path``, ``* LENGTH role.* /rules/*``)."""
    if key in recorded:
        return key
    star = '* ' + key.split(' ', 1)[1]
    if star in recorded:
        return star
    for pattern in recorded:
        if any(c in pattern for c in '*?[') and fnmatch.fnmatchcase(key, pattern):
            return pattern
    return None
