# This Source Code Form is subject to the terms of the Mozilla Public
# License, v. 2.0. Copyright (c) 2026 schroeder247. SPDX-License-Identifier: MPL-2.0
"""The abstract interpreter over the parsed PHP (see php.py for what it does and does not do)."""
from __future__ import annotations

import os
from dataclasses import dataclass, field, replace
from typing import Any

from .php import (NAMESPACE, ClassDef, Function, Node, PStr, Parser, Sym, Unknown, is_unknown, parse_file, syms_of, tokenize)

REQUIRED_FLAG = 0x00001   # API_REQUIRED, the same in 7.0, 7.4 and 8.0 (checked by the extractor against the defines)


class ApiRef:
    """``API::Host()``: another service, called through the API. Calls on it are recorded, never followed."""

    def __init__(self, obj: str):
        self.obj = obj

    def __repr__(self) -> str:
        return f'API::{self.obj}'


@dataclass
class Capture:
    """One ``CApiInputValidator::validate($rules, $data, …)`` whose data came from the request."""

    origin: tuple
    rules: Any
    file: str
    line: int
    partial: bool = False   # the data was array_intersect_key($x, $rules['fields']): a strict rule says nothing of the rest
    arms: frozenset = frozenset()   # (if, arm) of every undecided branch it ran in; 'after' = past an arm that left early


@dataclass
class Trace:
    captures: list[Capture] = field(default_factory=list)
    defaults: list[tuple[tuple, dict]] = field(default_factory=list)   # zbx_array_merge($defOptions, $options)
    api_calls: set = field(default_factory=set)                        # ('host', 'get') …
    returns: list = field(default_factory=list)
    allowed: list[tuple[tuple, list]] = field(default_factory=list)    # array_diff_key($input, $allowed_fields)
    touched: dict = field(default_factory=dict)                        # origin -> the keys the code read off it
    inserts: list[tuple[tuple, str]] = field(default_factory=list)     # DB::insert('table', $input)
    mandatory: list[tuple[tuple, list]] = field(default_factory=list)  # check_db_fields(['host' => null], $input)
    injected: set = field(default_factory=set)                         # (origin, key) the method sets on the request


TOUCHED: list = [None]   # the running trace's `touched`, for _index (a module function)
WEAK_TYPES: set = set()  # API_ANY and API_UNEXPECTED, read from the defines when an interpreter starts


class Budget(Exception):
    pass


@dataclass
class Frame:
    vars: dict
    cls: str | None          # the class the running function is declared in (self::)
    called: str | None       # the class the call was made on (static::, $this->)
    fn: Function | None
    returns: list = field(default_factory=list)


_TERMINATED = object()


class Interpreter:
    def __init__(self, root: str):
        self.root = root
        self.ui = os.path.join(root, 'ui', 'include')
        self.classes: dict[str, ClassDef] = {}
        self._namespaced: set[str] = set()
        self.functions: dict[str, Function] = {}
        self.defines: dict[str, Node] = {}
        self._define_values: dict[str, Any] = {}
        self._class_const_values: dict[tuple[str, str], Any] = {}
        self._schema: dict | None = None
        self._memo: dict = {}
        self.trace = Trace()
        self.this_state: dict[str, Any] = {}
        self.steps = 0
        self.max_steps = 400_000
        self.depth = 0
        self.stack: list[str] = []
        self.unreadable: list[str] = []
        self.errors: list[str] = []
        self.refusing = 0    # > 0 while the condition of an `if (…) { self::exception(…) }` is evaluated
        self.branching = 0   # > 0 inside an arm of a branch the interpreter cannot decide
        self.arms: list[tuple[int, Any]] = []   # the (if, arm) of every undecided branch being run
        self.inherited: list[frozenset] = []    # per call: the early-exit arms the caller was in when it called
        self.frame_now: Frame | None = None     # the frame whose block is being run
        self._load()
        WEAK_TYPES.clear()
        WEAK_TYPES.update(int(v) for v in (self.define('API_ANY'), self.define('API_UNEXPECTED')) if isinstance(v, int))

    # ------------------------------------------------------------------------------------------------------------------
    # loading

    def _load(self) -> None:
        paths = [os.path.join(self.ui, name) for name in sorted(os.listdir(self.ui)) if name.endswith('.php')]
        for directory, dirs, files in os.walk(os.path.join(self.ui, 'classes')):
            dirs.sort()   # one order on every file system: the first class of a name is kept
            paths.extend(os.path.join(directory, name) for name in sorted(files) if name.endswith('.php'))
        for path in paths:
            if True:
                with open(path, encoding='utf-8', errors='replace') as handle:
                    src = handle.read()
                if 'class ' not in src and 'function ' not in src and 'define(' not in src and 'trait ' not in src:
                    continue
                try:
                    classes, functions = parse_file(path, src)
                except Exception as error:   # noqa: BLE001 - reported, never fatal: one unreadable file is one gap
                    self.unreadable.append(f'{os.path.relpath(path, self.root)}: {error}')
                    continue
                namespaced = bool(NAMESPACE.search(src))
                for cls in classes:
                    # a namespaced class (Zabbix\Core\CModule) never hides the global one the API names (CModule)
                    key = cls.name.lower()
                    if key not in self.classes or (key in self._namespaced and not namespaced):
                        self.classes[key] = cls
                        if namespaced:
                            self._namespaced.add(key)
                        else:
                            self._namespaced.discard(key)
                for key, fn in functions.items():
                    self.functions.setdefault(key, fn)
                if 'define(' in src:
                    self._load_defines(src)

    def _load_defines(self, src: str) -> None:
        toks = tokenize(src)
        i = 0
        while i < len(toks) - 3:
            tok = toks[i]
            if tok.kind == 'name' and tok.text == 'define' and toks[i + 1].text == '(' and toks[i + 2].kind == 'str':
                p = Parser(toks)
                p.i = i + 3
                if p.accept(','):
                    try:
                        node = p.expr()
                        self.defines.setdefault(toks[i + 2].text, node)
                    except Exception:   # noqa: BLE001 - a define we cannot parse stays unknown
                        pass
                i = p.i
                continue
            i += 1

    # ------------------------------------------------------------------------------------------------------------------
    # constants

    def define(self, name: str) -> Any:
        if name in self._define_values:
            return self._define_values[name]
        node = self.defines.get(name)
        if node is None:
            return Unknown(name)
        self._define_values[name] = Unknown('cycle:' + name)
        value = self.eval(node, Frame({}, None, None, None))
        if isinstance(value, int) and not isinstance(value, bool):
            value = Sym(value, name)
        self._define_values[name] = value
        return value

    def cls(self, name: str | None) -> ClassDef | None:
        return self.classes.get(name.lower()) if name else None

    def chain(self, name: str | None) -> list[ClassDef]:
        """The class, its traits, its parent, the parent's traits, … (method resolution order)."""
        out: list[ClassDef] = []
        seen = set()
        c = self.cls(name)
        while c is not None and c.name.lower() not in seen:
            seen.add(c.name.lower())
            out.append(c)
            for trait in c.traits:
                t = self.cls(trait)
                if t is not None:
                    out.append(t)
            c = self.cls(c.parent)
        return out

    def is_subclass(self, name: str, base: str) -> bool:
        return any(c.name.lower() == base.lower() for c in self.chain(name))

    def class_const(self, cls: str, name: str) -> Any:
        for c in self.chain(cls):
            if name in c.consts:
                key = (c.name, name)
                if key not in self._class_const_values:
                    self._class_const_values[key] = Unknown('cycle')
                    value = self.eval(c.consts[name], Frame({}, c.name, cls, None))
                    if isinstance(value, int) and not isinstance(value, bool):
                        value = Sym(value, f'{c.name}::{name}')
                    self._class_const_values[key] = value
                return self._class_const_values[key]
        return Unknown(f'{cls}::{name}')

    def find_method(self, cls: str | None, name: str) -> Function | None:
        for c in self.chain(cls):
            fn = c.methods.get(name.lower())
            if fn is not None and (fn._span is not None or fn.body is not None):
                return fn
        return None

    def classes_defining(self, method: str) -> list[ClassDef]:
        m = method.lower()
        return [c for c in self.classes.values() if m in c.methods and c.methods[m]._span is not None]

    # ------------------------------------------------------------------------------------------------------------------
    # the database schema (schema.inc.php)

    @property
    def schema(self) -> dict:
        if self._schema is None:
            path = os.path.join(self.ui, 'schema.inc.php')
            with open(path, encoding='utf-8') as handle:
                toks = tokenize(handle.read())
            p = Parser(toks)
            p.expect('return')
            self._schema = self.eval(p.expr(), Frame({}, None, None, None))
        return self._schema

    def field_length(self, table: Any, column: Any) -> Any:
        try:
            return self.schema[table]['fields'][column]['length']
        except (KeyError, TypeError):
            return Unknown(f'length:{table}.{column}')

    def filter_fields(self, table: str, output: Any, filter_: bool) -> Any:
        fields = (self.schema.get(table) or {}).get('fields') if isinstance(self.schema.get(table), dict) else None
        supported = self.class_const('DB', 'SUPPORTED_FILTER_TYPES' if filter_ else 'SUPPORTED_SEARCH_TYPES')
        if not isinstance(fields, dict) or not isinstance(supported, int):
            return Unknown('filter-fields')
        if output is not None:
            if not isinstance(output, dict):
                return Unknown('filter-fields')
            keep = {v for v in output.values() if isinstance(v, str)}
            fields = {k: v for k, v in fields.items() if k in keep}
        names = [k for k, v in fields.items() if isinstance(v, dict) and isinstance(v.get('type'), int) and v['type'] & supported]
        return {i: n for i, n in enumerate(names)}

    def field_default(self, table: Any, column: Any) -> Any:
        try:
            return self.schema[table]['fields'][column].get('default', '')
        except (KeyError, TypeError, AttributeError):
            return Unknown(f'default:{table}.{column}')

    # ------------------------------------------------------------------------------------------------------------------
    # running an API method

    def run(self, service_class: str, method: str) -> Trace:
        """Follow ``service_class::method`` from its entry with the request as its first parameter."""
        self.trace = Trace()
        TOUCHED[0] = self.trace.touched
        self.this_state = {}
        self.steps = 0
        self.depth = 0
        self.stack = []
        self.arms = []
        self.inherited = []
        self.frame_now = None
        self._memo = {}
        fn = self.find_method(service_class, method)
        if fn is None:
            return self.trace
        ctor = self.find_method(service_class, '__construct')
        try:
            if ctor is not None:
                self.invoke(ctor, [], service_class)
        except Budget:
            pass
        self.steps = 0
        args = []
        for index, _ in enumerate(fn.params):
            args.append(Unknown('request', ('in',)) if index == 0 else Unknown('arg'))
        try:
            result = self.invoke(fn, args, service_class)
            self.trace.returns.append(result)
        except Budget:
            pass
        return self.trace

    def current_arms(self, frame: 'Frame | None' = None) -> frozenset:
        """The arms the code being run is in: the undecided branches' and the early exits' of every frame."""
        arms = set(self.arms)
        for inherited in self.inherited:
            arms |= inherited
        f = frame if frame is not None else self.frame_now
        if f is not None:
            arms |= f.vars.get(GUARD_ARMS) or frozenset()
        return frozenset(arms)

    def invoke(self, fn: Function, args: list, called: str | None) -> Any:
        key = f'{fn.cls}::{fn.name}'
        if self.stack.count(key) >= 2 or self.depth > 14:
            return Unknown('depth:' + key)
        memo_key = None
        if all(_memoizable(a) for a in args):
            memo_key = (key, called, repr(args))
            if memo_key in self._memo:
                result, captures = self._memo[memo_key]
                here = self.current_arms()
                self.trace.captures.extend(replace(c, arms=c.arms | here) for c in captures)
                return result
        before = len(self.trace.captures)
        entry_arms = self.current_arms()
        frame = Frame({}, fn.cls, called or fn.cls, fn)
        for index, (name, default, _) in enumerate(fn.params):
            if index < len(args):
                frame.vars[name] = args[index]
            elif default is not None:
                frame.vars[name] = self.eval(default, frame)
            else:
                frame.vars[name] = Unknown('missing-arg')
        self.depth += 1
        self.stack.append(key)
        self.inherited.append(frozenset(self.frame_now.vars.get(GUARD_ARMS) or ()) if self.frame_now is not None else frozenset())
        try:
            self.exec_block(fn.parsed_body(), frame)
        finally:
            self.depth -= 1
            self.stack.pop()
            self.inherited.pop()
        result = merge_alternatives(frame.returns) if frame.returns else None
        if memo_key is not None:
            # kept relative to this call's context: a replay adds the context it is replayed in
            self._memo[memo_key] = (result, [replace(c, arms=c.arms - entry_arms) for c in self.trace.captures[before:]])
        return result

    # ------------------------------------------------------------------------------------------------------------------
    # statements

    def exec_block(self, stmts: list[Node], frame: Frame) -> bool:
        """Runs the statements; False when every path returned (or threw)."""
        outer, self.frame_now = self.frame_now, frame
        try:
            for stmt in stmts:
                if not self.exec(stmt, frame):
                    return False
            return True
        finally:
            self.frame_now = outer

    def exec(self, s: Node, frame: Frame) -> bool:
        self.steps += 1
        if self.steps > self.max_steps:
            raise Budget()
        k = s.kind
        if k == 'expr':
            try:
                self.eval(s.a, frame)
            except (TypeError, ValueError, KeyError, AttributeError, IndexError, OverflowError) as error:
                self.errors.append(f'{frame.fn.file if frame.fn else "?"}@{s.pos}: {type(error).__name__}: {error}')
            return True
        if k == 'return':
            frame.returns.append(self.eval(s.a, frame) if s.a is not None else None)
            return False
        if k == 'throw':
            return False
        if k == 'block':
            return self.exec_block(s.a, frame)
        if k == 'if':
            return self.exec_if(s, frame)
        if k == 'foreach':
            subject = self.eval(s.a, frame)
            inner = dict(frame.vars)
            value_origin = subject.origin + ('[]',) if is_unknown(subject) and subject.origin else None
            elements = _elements(subject)
            if elements:
                # run the body once per distinct element shape (bounded), merging the variables afterwards
                outs = []
                for key_value, element in elements[:24]:
                    vars_ = dict(frame.vars)
                    f2 = Frame(vars_, frame.cls, frame.called, frame.fn, frame.returns)
                    if s.b is not None:
                        self.assign(s.b, key_value, f2)
                    self.assign(s.c, element, f2)
                    self.exec_block(s.d, f2)
                    outs.append(f2.vars)
                frame.vars = merge_envs([frame.vars] + outs)
                return True
            f2 = Frame(inner, frame.cls, frame.called, frame.fn, frame.returns)
            if s.b is not None:
                self.assign(s.b, Unknown('key'), f2)
            self.assign(s.c, Unknown('element', value_origin), f2)
            self.exec_block(s.d, f2)
            frame.vars = merge_envs([frame.vars, f2.vars])
            return True
        if k in ('loop', 'while'):
            body = s.a if k == 'loop' else s.b
            f2 = Frame(dict(frame.vars), frame.cls, frame.called, frame.fn, frame.returns)
            self.exec_block(body, f2)
            frame.vars = merge_envs([frame.vars, f2.vars])
            return True
        if k == 'switch':
            subject = self.eval(s.a, frame)
            outs = []
            chosen = None
            if not is_unknown(subject):
                for index, (value, _) in enumerate(s.b):
                    if value is not None and not is_unknown(v := self.eval(value, frame)) and _loose_eq(v, subject):
                        chosen = index
                        break
            cases = s.b[chosen:] if chosen is not None else s.b
            any_open = False
            for value, body in cases:
                f2 = Frame(dict(frame.vars), frame.cls, frame.called, frame.fn, frame.returns)
                open_ = self.exec_block([b for b in body if b.kind != 'break'], f2)
                if open_:
                    any_open = True
                    outs.append(f2.vars)
                if chosen is not None and any(b.kind == 'break' for b in body):
                    break
            if chosen is None and not any(value is None for value, _ in s.b):
                outs.append(frame.vars)
                any_open = True
            if outs:
                frame.vars = merge_envs(outs)
            return any_open or not outs
        if k == 'try':
            f2 = Frame(dict(frame.vars), frame.cls, frame.called, frame.fn, frame.returns)
            open_ = self.exec_block(s.a, f2)
            frame.vars = f2.vars
            for handler in s.b:
                f3 = Frame(dict(frame.vars), frame.cls, frame.called, frame.fn, frame.returns)
                self.exec_block(handler, f3)
            self.exec_block(s.c, frame)
            return open_
        if k == 'unset':
            for target in s.a:
                if target.kind == 'var':
                    frame.vars.pop(target.a, None)
                elif target.kind == 'index':
                    self.unset_index(target, frame)
            return True
        return True

    def exec_if(self, s: Node, frame: Frame) -> bool:
        arms = list(s.a)
        other = s.b
        taken: list[tuple[list[Node], bool]] = []
        for cond, body in arms:
            refuses = _refuses(body)
            if refuses and not self.branching and not frame.vars.get(GUARDED):
                self.mandatory_by_hand(cond, frame)
            self.refusing += 1 if refuses else 0
            try:
                c = truth(self.eval(cond, frame))
            finally:
                self.refusing -= 1 if refuses else 0
            if c is True:
                taken.append((body, True))
                break
            if c is None:
                taken.append((body, False))
        else:
            taken.append((other or [], True))
        outs = []
        open_any = False
        undecided = len([b for b, _ in taken if not _refuses(b)]) > 1
        for index, (body, _) in enumerate(taken):
            f2 = Frame(dict(frame.vars), frame.cls, frame.called, frame.fn, frame.returns)
            self.branching += 1 if undecided else 0
            if undecided:
                self.arms.append((id(s), index))
            try:
                ended_open = self.exec_block(body, f2)
            finally:
                self.branching -= 1 if undecided else 0
                if undecided:
                    self.arms.pop()

            if ended_open:
                open_any = True
                outs.append(f2.vars)
        if outs:
            frame.vars = merge_envs(outs)
        if len(taken) > 1 and any(_leaves(body) for body, _ in taken):
            # `if (…) { continue; }` (CReport.php 8.0:277-280): what follows runs on some paths only - the paths the arm
            # that left did not take, an arm of its own beside it
            frame.vars[GUARDED] = True
            frame.vars[GUARD_ARMS] = frozenset(frame.vars.get(GUARD_ARMS) or ()) | {(id(s), 'after')}
        return open_any

    def mandatory_by_hand(self, cond: Node, frame: Frame) -> None:
        """``if (!array_key_exists('name', $drule) …) { self::exception(…) }`` (``CDRule::validateCreate``, 8.0:210) and
        ``if (!isset($data['hostids']) …)``: the key is mandatory, the same fact as ``check_db_fields``."""
        first = cond
        while first.kind == 'bin' and first.a in ('||', 'or'):
            first = first.b
        if not (first.kind == 'unary' and first.a == '!'):
            return
        inner = first.b
        if inner.kind == 'call' and inner.a.lower() == 'array_key_exists' and len(inner.b) == 2:
            key, subject = self.eval(inner.b[0], frame), self.eval(inner.b[1], frame)
        elif inner.kind == 'isset' and len(inner.a) == 1 and inner.a[0].kind == 'index' and inner.a[0].b is not None:
            key, subject = self.eval(inner.a[0].b, frame), self.eval(inner.a[0].a, frame)
        else:
            return
        if isinstance(key, str) and is_unknown(subject) and subject.origin:
            self.trace.mandatory.append((subject.origin, [key]))

    # ------------------------------------------------------------------------------------------------------------------
    # assignment

    def assign(self, target: Node, value: Any, frame: Frame) -> None:
        if target.kind == 'var':
            if target.a == 'this':
                return
            frame.vars[target.a] = value
            return
        if target.kind == 'array':   # [$a, $b] = … / list($a, $b) = …
            for index, (key, sub) in enumerate(target.a):
                if sub is None or (sub.kind == 'lit' and sub.a is None):
                    continue
                k = self.eval(key, frame) if key is not None else index
                self.assign(sub, _index(value, k), frame)
            return
        if target.kind == 'index':
            path = []
            node = target
            while node.kind == 'index':
                path.append(node.b)
                node = node.a
            path.reverse()
            if node.kind == 'var' and node.a != 'this':
                keys = [self.eval(p, frame) if p is not None else None for p in path]
                base = frame.vars.get(node.a)
                if is_unknown(base) and base.origin and keys and isinstance(keys[-1], str):
                    # the method writing a key into the request itself ($item['flags'] = …): not a caller's field
                    origin = base.origin + tuple('[]' if not isinstance(k, str) else k for k in keys[:-1])
                    self.trace.injected.add((origin, keys[-1]))
                frame.vars[node.a] = _set_path(base, keys, value)
            elif node.kind == 'prop' and node.a.kind == 'var' and node.a.a == 'this':
                keys = [self.eval(p, frame) if p is not None else None for p in path]
                self.this_state[node.b] = _set_path(self.this_prop(node.b, frame), keys, value)
            return
        if target.kind == 'prop' and target.a.kind == 'var' and target.a.a == 'this':
            self.this_state[target.b] = value
            return
        if target.kind == 'sprop':
            self.this_state['::' + target.b] = value

    def unset_index(self, target: Node, frame: Frame) -> None:
        path = []
        node = target
        while node.kind == 'index':
            path.append(node.b)
            node = node.a
        path.reverse()
        if node.kind != 'var':
            return
        keys = [self.eval(p, frame) if p is not None else None for p in path]
        base = frame.vars.get(node.a)
        frame.vars[node.a] = _unset_path(base, keys)

    def class_prop(self, cls: str, name: str) -> Any:
        """A property's declared default, walking the parents; never the running object's state."""
        for c in self.chain(cls):
            if name in c.props:
                default = c.props[name][0]
                return self.eval(default, Frame({}, c.name, cls, None)) if default is not None else None
        return Unknown('prop:' + name)

    def this_prop(self, name: str, frame: Frame) -> Any:
        if name in self.this_state:
            return self.this_state[name]
        for c in self.chain(frame.called or frame.cls):
            if name in c.props:
                default = c.props[name][0]
                value = self.eval(default, Frame({}, c.name, frame.called, None)) if default is not None else None
                self.this_state[name] = value
                return value
        return Unknown('prop:' + name)

    # ------------------------------------------------------------------------------------------------------------------
    # expressions

    def eval(self, n: Node | None, frame: Frame) -> Any:
        if n is None:
            return None
        k = n.kind
        if k == 'lit':
            return n.a
        if k == 'var':
            if n.a == 'this':
                return _This()
            if n.a in frame.vars:
                return frame.vars[n.a]
            return Unknown('var:' + n.a)
        if k == 'array':
            out: dict = {}
            next_index = 0
            for key, value in n.a:
                if value.kind == 'spread':
                    spread = self.eval(value.a, frame)
                    if isinstance(spread, dict):
                        for item in spread.values():
                            out[next_index] = item
                            next_index += 1
                    continue
                v = self.eval(value, frame)
                if key is None:
                    out[next_index] = v
                    next_index += 1
                else:
                    kv = self.eval(key, frame)
                    if is_unknown(kv) or isinstance(kv, (dict, _This, ApiRef)) or kv is None:
                        continue
                    kv = _key(kv)
                    out[kv] = v
                    if isinstance(kv, int) and kv >= next_index:
                        next_index = kv + 1
            return out
        if k == 'const':
            name = n.a
            if name.upper() in ('PHP_INT_MAX',):
                return 2**63 - 1
            if name.upper() == 'PHP_EOL':
                return '\n'
            # magic constants: CHostInterface::update() calls $this->checkInput($interfaces, __FUNCTION__), and
            # checkInput's own `$update = ($method == 'update')` decides which fields are mandatory
            if name.upper() == '__FUNCTION__' and frame.fn is not None:
                return frame.fn.name
            if name.upper() == '__CLASS__' and frame.cls is not None:
                return frame.cls
            if name.upper() == '__METHOD__' and frame.fn is not None and frame.cls is not None:
                return f'{frame.cls}::{frame.fn.name}'
            return self.define(name)
        if k == 'cconst':
            cls = self.resolve_class(n.a, frame)
            if cls is None:
                return Unknown(f'{n.a}::{n.b}')
            return self.class_const(cls, n.b)
        if k == 'dcconst':
            subject = self.eval(n.a, frame)
            if isinstance(subject, str):
                return self.class_const(subject, n.b)
            return Unknown('dcconst')
        if k == 'sprop':
            # static properties are run-time state (CApiService::$userData is the caller): never their declared default
            if '::' + n.b in self.this_state:
                return self.this_state['::' + n.b]
            return Unknown('sprop:' + n.b)
        if k == 'prop':
            subject = n.a
            if subject.kind == 'var' and subject.a == 'this':
                return self.this_prop(n.b, frame)
            return Unknown('prop')
        if k == 'index':
            base = self.eval(n.a, frame)
            if n.b is None:
                return Unknown('append')
            key = self.eval(n.b, frame)
            return _index(base, key)
        if k == 'assign':
            op, target, value_node = n.a, n.b, n.c
            value = self.eval(value_node, frame)
            if op != '=':
                current = self.eval(target, frame)
                if op == '??=':
                    value = current if current is not None and not is_unknown(current) else value
                else:
                    value = self.plus_or_binary(op[:-1], current, value)
            self.assign(target, value, frame)
            return value
        if k == 'bin':
            op = n.a
            # Both operands are always evaluated: this is an analysis, and the right operand's calls (a validation, an
            # array_diff_key) are facts whether or not PHP would short-circuit them for some input.
            if op in ('&&', 'and'):
                left = truth(self.eval(n.b, frame))
                right = truth(self.eval(n.c, frame))
                if left is False:
                    return False
                if left is True:
                    return right if right is not None else Unknown('&&')
                return False if right is False else Unknown('&&')
            if op in ('||', 'or'):
                left = truth(self.eval(n.b, frame))
                right = truth(self.eval(n.c, frame))
                if left is True:
                    return True
                if left is False:
                    return right if right is not None else Unknown('||')
                return True if right is True else Unknown('||')
            if op == '??':
                left = self.eval(n.b, frame)
                if left is None:
                    return self.eval(n.c, frame)
                if is_unknown(left):
                    right = self.eval(n.c, frame)
                    return left if not isinstance(right, dict) else merge_alternatives([right, left])
                return left
            return self.plus_or_binary(op, self.eval(n.b, frame), self.eval(n.c, frame))
        if k == 'unary':
            v = self.eval(n.b, frame)
            if n.a == '!':
                t = truth(v)
                return Unknown('!') if t is None else not t
            if n.a == '-':
                return -v if isinstance(v, (int, float)) and not isinstance(v, bool) else Unknown('-')
            if n.a == '+':
                return v
            if n.a == '~' and isinstance(v, int) and not isinstance(v, bool):
                # $rules['fields']['expression']['flags'] &= ~API_ALLOW_LLD_MACRO (CTriggerGeneral.php 8.0:1139)
                return ~int(v)
            return Unknown('~')
        if k == 'ternary':
            c = truth(self.eval(n.a, frame))
            if c is True:
                return self.eval(n.b, frame)
            if c is False:
                return self.eval(n.c, frame)
            return merge_alternatives([self.eval(n.b, frame), self.eval(n.c, frame)])
        if k == 'elvis':
            left = self.eval(n.a, frame)
            t = truth(left)
            if t is True:
                return left
            if t is False:
                return self.eval(n.b, frame)
            return merge_alternatives([left, self.eval(n.b, frame)])
        if k == 'cast':
            v = self.eval(n.b, frame)
            if n.a in ('int', 'integer') and isinstance(v, (int, float, str)) and not is_unknown(v):
                try:
                    return int(v)
                except ValueError:
                    return 0
            if n.a == 'string' and isinstance(v, (int, str)):
                return str(v)
            if n.a == 'array':
                return v if isinstance(v, dict) else (Unknown('cast', v.origin) if is_unknown(v) else {0: v})
            return Unknown('cast')
        if k == 'isset':
            vals = [self.eval(a, frame) for a in n.a]
            if any(is_unknown(v) for v in vals):
                return Unknown('isset')
            return all(v is not None for v in vals)
        if k == 'empty':
            v = self.eval(n.a[0], frame)
            t = truth(v)
            return Unknown('empty') if t is None else not t
        if k == 'match':
            subject = self.eval(n.a, frame)
            options = []
            for conds, value in n.b:
                if conds is None:
                    options.append(value)
                    continue
                vals = [self.eval(c, frame) for c in conds]
                if not is_unknown(subject) and all(not is_unknown(v) for v in vals):
                    if any(_loose_eq(v, subject) for v in vals):
                        return self.eval(value, frame)
                    continue
                options.append(value)
            return merge_alternatives([self.eval(o, frame) for o in options]) if options else Unknown('match')
        if k == 'call':
            return self.call_function(n, frame)
        if k == 'scall':
            return self.call_static(n, frame)
        if k == 'mcall':
            return self.call_method(n, frame)
        if k == 'dscall':
            return self.call_dynamic_static(n, frame)
        if k == 'incdec':
            return Unknown('incdec')
        if k == 'new':
            args = [self.eval(a, frame) for a in n.a]
            if n.b == 'CLimitedSetValidator' and args and isinstance(args[0], dict) and isinstance(args[0].get('values'), dict):
                return _SetValidator(args[0]['values'])
            return Unknown('new')
        if k == 'instanceof':
            # a service's own class is known: `$this instanceof CTriggerPrototype` is decided for the class the method
            # runs for, so a shared validation takes the branch of the service it validates (CTriggerGeneral.php 8.0:1134)
            subject = self.eval(n.a, frame)
            if isinstance(subject, _This) and n.b is not None and n.b.kind == 'const' and frame.called:
                return self.is_subclass(frame.called, n.b.a)
            return Unknown('instanceof')
        if k in ('closure', 'unknown', 'spread'):
            return Unknown(k)
        return Unknown(k)

    def plus_or_binary(self, op: str, a: Any, b: Any) -> Any:
        # `$options += $defaults` / `$options + $defaults`: the request keeps its origin; the defaults are the options
        # the method accepts beyond its validated rules (the older services' $defOptions).
        if op == '+' and is_unknown(a) and a.origin:
            if isinstance(b, dict):
                self.trace.defaults.append((a.origin, b))
                return Unknown('options', a.origin, {**b, **(a.keys or {})})
            return a
        if op == '+' and is_unknown(b) and b.origin and isinstance(a, dict):
            self.trace.defaults.append((b.origin, a))
            return Unknown('options', b.origin, {**a, **(b.keys or {})})
        return binary(op, a, b)

    def resolve_class(self, name: str, frame: Frame) -> str | None:
        low = name.lower()
        if low == 'self':
            return frame.cls
        if low == 'static':
            return frame.called
        if low == 'parent':
            c = self.cls(frame.cls)
            return c.parent if c else None
        return name

    # ------------------------------------------------------------------------------------------------------------------
    # calls

    def args(self, nodes: list[Node], frame: Frame) -> list:
        out = []
        for a in nodes:
            if a.kind == 'spread':
                v = self.eval(a.a, frame)
                if isinstance(v, dict):
                    out.extend(v.values())
                else:
                    out.append(Unknown('spread'))
            else:
                out.append(self.eval(a, frame))
        return out

    def call_function(self, n: Node, frame: Frame) -> Any:
        name = n.a.lower()
        args = self.args(n.b, frame)
        if name == 'get_class' and len(args) == 1 and isinstance(args[0], _This) and frame.called:
            # get_class($this): the service the shared method runs for (CGraphGeneral.php 8.0:829)
            cls = self.cls(frame.called)
            return cls.name if cls else frame.called
        builtin = BUILTINS.get(name)
        if builtin is not None:
            try:
                return builtin(self, args)
            except (TypeError, ValueError, KeyError, AttributeError, IndexError):
                return Unknown(name)
        fn = self.functions.get(name)
        if fn is not None:
            return self.invoke(fn, args, None)
        return Unknown(name + '()')

    def call_static(self, n: Node, frame: Frame) -> Any:
        cls_name, method, arg_nodes = n.a, n.b, n.c
        low = cls_name.lower()
        m = method.lower()
        if low == 'capiinputvalidator':
            args = self.args(arg_nodes, frame)
            if m == 'validate' and len(args) >= 2:
                data = args[1]
                origin = data.origin if is_unknown(data) else None
                if origin:
                    self.trace.captures.append(Capture(origin, args[0], frame.fn.file if frame.fn else '', n.pos, data.why == 'partial', self.current_arms(frame)))
            return Unknown('validated')
        if low == 'db':
            args = self.args(arg_nodes, frame)
            if m in ('insert', 'insertbatch', 'save', 'update') and len(args) >= 2 and isinstance(args[0], str):
                data = args[1]
                if is_unknown(data) and data.origin:
                    self.trace.inserts.append((data.origin, args[0]))
            if m == 'getfieldlength' and len(args) == 2:
                return self.field_length(args[0], args[1])
            if m == 'getdefault' and len(args) == 2:
                return self.field_default(args[0], args[1])
            if m == 'getschema' and len(args) >= 1 and isinstance(args[0], str):
                return self.schema.get(args[0], Unknown('schema'))
            if m in ('getfilterfields', 'getsearchfields') and args and isinstance(args[0], str):
                # DB::getFilterFields('hosts', self::OUTPUT_FIELDS) (CHost.php 8.0:543): the columns of those whose
                # type is one of DB::SUPPORTED_FILTER_TYPES / SUPPORTED_SEARCH_TYPES (DB.php 8.0:1201-1241)
                return self.filter_fields(args[0], args[1] if len(args) > 1 else None, m == 'getfilterfields')
            return Unknown('db')
        if low == 'api':
            if m in ('getapi', 'getapiservice') and arg_nodes:
                v = self.eval(arg_nodes[0], frame)
                return ApiRef(v) if isinstance(v, str) else Unknown('api')
            fn = self.find_method('API', method)
            if fn is not None:
                body = fn.parsed_body()
                for stmt in body:
                    if stmt.kind == 'return' and stmt.a is not None and stmt.a.kind == 'scall' and stmt.a.c:
                        target = stmt.a.c[0]
                        if target.kind == 'lit' and isinstance(target.a, str):
                            return ApiRef(target.a)
            return Unknown('api')
        if low == 'app' and m == 'getmode':
            # a request to api_jsonrpc.php runs in API mode (ZBase::EXEC_MODE_API): what an internal (frontend) call adds
            # is not the API's (CProxy::get's write-only tls_psk fields, CProxy.php 8.0:86)
            mode = self.class_const('APP', 'EXEC_MODE_API')
            return mode if not is_unknown(mode) else Unknown('APP::getMode()')
        if low == 'csettingshelper' and m != 'getvalidationrules':
            # the server's run-time settings (CSettingsHelper::isMobileDevicesEnabled, 8.0:242, gates role's devices.*
            # rules, CRole.php 8.0:228): a fact of the installation, not of the API, so both answers stand
            self.args(arg_nodes, frame)
            return Unknown('settings:' + method)
        cls = self.resolve_class(cls_name, frame)
        args = self.args(arg_nodes, frame)
        fn = self.find_method(cls, method)
        if fn is None:
            return Unknown(f'{cls_name}::{method}()')
        # parent::/self:: keep the late-static-binding class; a named class call binds to that class
        called = frame.called if low in ('self', 'parent', 'static') else cls
        if low == 'static':
            called = frame.called
        return self.invoke(fn, args, called)

    def call_method(self, n: Node, frame: Frame) -> Any:
        subject_node, method, arg_nodes = n.a, n.b, n.c
        subject = self.eval(subject_node, frame)
        args = self.args(arg_nodes, frame)
        if isinstance(subject, _This):
            fn = self.find_method(frame.called or frame.cls, method)
            if fn is None:
                return Unknown('$this->' + method)
            return self.invoke(fn, args, frame.called or frame.cls)
        if isinstance(subject, _SetValidator) and method.lower() == 'validate' and args and is_unknown(args[0]) and args[0].origin:
            # an older service's CLimitedSetValidator: the same fact as an API_INT32 rule with an 'in' list
            rule = {'type': self.define('API_INT32'), 'in': _implode(self, [',', subject.values])}
            self.trace.captures.append(Capture(args[0].origin, rule, frame.fn.file if frame.fn else '', n.pos, False, self.current_arms(frame)))
            return Unknown('validated')
        if isinstance(subject, ApiRef):
            self.trace.api_calls.add((subject.obj, method.lower()))
            return Unknown(f'API::{subject.obj}->{method}', None)
        return Unknown('->' + method)

    def call_dynamic_static(self, n: Node, frame: Frame) -> Any:
        subject = self.eval(n.a, frame)
        args = self.args(n.c, frame)
        if isinstance(subject, str):
            fn = self.find_method(subject, n.b)
            return self.invoke(fn, args, subject) if fn is not None else Unknown('dyn')
        # a polymorphic static call on a class we cannot know: every class that defines the method, as alternatives
        defining = self.classes_defining(n.b)
        if not defining or len(defining) > 40:
            return Unknown('poly:' + n.b)
        results = []
        for c in sorted(defining, key=lambda c: c.name):
            if c.abstract:
                continue
            results.append(self.invoke(c.methods[n.b.lower()], args, c.name))
        return merge_alternatives(results) if results else Unknown('poly')


class _SetValidator:
    def __init__(self, values: dict):
        self.values = values


class _This:
    def __repr__(self) -> str:
        return '$this'


# ----------------------------------------------------------------------------------------------------------------------
# value helpers


GUARD_ARMS = '\0guard-arms'   # a frame variable: the (if, 'after') arms its early exits opened
GUARDED = '\0guarded'   # a frame variable: an earlier undecided branch left the block early on some paths


def _leaves(body: list[Node]) -> bool:
    return any(s.kind in ('continue', 'break', 'return') for s in body)


def _refuses(body: list[Node]) -> bool:
    """An arm whose first statement throws: its condition is a check the input must pass."""
    if not body:
        return False
    first = body[0]
    return first.kind == 'throw' or (first.kind == 'expr' and first.a is not None and first.a.kind == 'scall'
                                     and first.a.b.lower() == 'exception')


def _memoizable(v: Any) -> bool:
    if is_unknown(v):
        return v.origin is None
    if isinstance(v, dict):
        return all(_memoizable(x) for x in v.values())
    return not isinstance(v, (_This, ApiRef))


def _key(k: Any) -> Any:
    if isinstance(k, bool):
        return int(k)
    if isinstance(k, Sym):
        return int(k)
    if isinstance(k, float):
        return int(k)
    if isinstance(k, str) and k.isdigit() and (k == '0' or not k.startswith('0')):
        return int(k)
    return str(k) if isinstance(k, str) else k


def _index(base: Any, key: Any) -> Any:
    if isinstance(base, dict):
        if is_unknown(key) or isinstance(key, (dict, _This, ApiRef)):
            return Unknown('index')
        return base.get(_key(key))
    if is_unknown(base):
        if base.keys is not None and not is_unknown(key) and _key(key) in base.keys:
            default = base.keys[_key(key)]
            origin = base.origin + (str(key),) if base.origin else None
            return Unknown('option', origin) if origin else default
        if base.origin and not is_unknown(key):
            if TOUCHED[0] is not None and isinstance(key, str):
                TOUCHED[0].setdefault(base.origin, set()).add(key)
            return Unknown('field', base.origin + (str(key),))
        if base.origin and is_unknown(key):
            return Unknown('field', base.origin + ('[]',))
        return Unknown('index')
    if isinstance(base, str) and isinstance(key, int):
        return base[key] if 0 <= key < len(base) else None
    return None


def _set_path(base: Any, keys: list, value: Any) -> Any:
    if not keys:
        return value
    head, rest = keys[0], keys[1:]
    if is_unknown(base):
        return base
    if not isinstance(base, dict):
        base = {}
    out = dict(base)
    if head is None:
        index = max([k for k in out if isinstance(k, int)], default=-1) + 1
        out[index] = _set_path(None, rest, value)
        return out
    if is_unknown(head) or isinstance(head, dict):
        return out
    head = _key(head)
    out[head] = _set_path(out.get(head), rest, value)
    return out


def _unset_path(base: Any, keys: list) -> Any:
    if not isinstance(base, dict) or not keys or keys[0] is None or is_unknown(keys[0]):
        return base
    out = dict(base)
    head = _key(keys[0])
    if len(keys) == 1:
        out.pop(head, None)
    elif head in out:
        out[head] = _unset_path(out[head], keys[1:])
    return out


def _elements(subject: Any) -> list:
    if isinstance(subject, dict):
        return list(subject.items())
    return []


def truth(v: Any) -> bool | None:
    if is_unknown(v) or isinstance(v, (_This, ApiRef)):
        return None
    if isinstance(v, dict):
        return bool(v)
    if isinstance(v, str):
        return v not in ('', '0')
    return bool(v)


def _loose_eq(a: Any, b: Any) -> bool:
    if isinstance(a, (int, float)) and isinstance(b, str):
        try:
            return float(b) == a
        except ValueError:
            return False
    if isinstance(b, (int, float)) and isinstance(a, str):
        return _loose_eq(b, a)
    return a == b


def binary(op: str, a: Any, b: Any) -> Any:
    try:
        return _binary(op, a, b)
    except (TypeError, ValueError, OverflowError, ZeroDivisionError):
        return Unknown(op)


def _binary(op: str, a: Any, b: Any) -> Any:
    if op == '.':
        if is_unknown(a) or is_unknown(b) or isinstance(a, dict) or isinstance(b, dict):
            return Unknown('.')
        text = _str(a) + _str(b)
        syms = {**syms_of(a), **syms_of(b)}
        return PStr(text, syms) if syms else text
    if is_unknown(a) or is_unknown(b):
        if op in ('==', '!=', '===', '!==', '<', '>', '<=', '>=', '<>'):
            return Unknown(op)
        return Unknown(op)
    if op == '|':
        value = _int(a) | _int(b)
        if isinstance(a, MaybeFlags) or isinstance(b, MaybeFlags):
            # $api_required | API_ALLOW_UNEXPECTED: a bit only some paths set stays "some paths" unless the other side sets it
            sure = (_int(a) & ~getattr(a, 'unsure', 0)) | (_int(b) & ~getattr(b, 'unsure', 0))
            return MaybeFlags(value, value & ~sure)
        return value
    if op == '&':
        return _int(a) & _int(b)
    if op == '^':
        return _int(a) ^ _int(b)
    if op == '+':
        if isinstance(a, dict) and isinstance(b, dict):
            out = dict(a)
            for k, v in b.items():
                out.setdefault(k, v)
            return out
        return _num(a) + _num(b)
    if op == '-':
        return _num(a) - _num(b)
    if op == '*':
        return _num(a) * _num(b)
    if op == '/':
        return _num(a) / _num(b) if _num(b) else Unknown('/0')
    if op == '%':
        return _int(a) % _int(b) if _int(b) else Unknown('%0')
    if op == '**':
        return _num(a) ** _num(b)
    if op == '<<':
        return _int(a) << _int(b)
    if op == '>>':
        return _int(a) >> _int(b)
    if op in ('===',):
        return type(a) is type(b) and a == b or (isinstance(a, int) and isinstance(b, int) and not isinstance(a, bool) and not isinstance(b, bool) and a == b)
    if op == '!==':
        return not binary('===', a, b)
    if op == '==':
        return _loose_eq(a, b)
    if op in ('!=', '<>'):
        return not _loose_eq(a, b)
    if op in ('<', '>', '<=', '>='):
        try:
            x, y = _num(a), _num(b)
        except (TypeError, ValueError):
            return Unknown(op)
        return {'<': x < y, '>': x > y, '<=': x <= y, '>=': x >= y}[op]
    if op == '<=>':
        return Unknown(op)
    return Unknown(op)


def _str(v: Any) -> str:
    if v is None or v is False:
        return ''
    if v is True:
        return '1'
    if isinstance(v, float) and v.is_integer():
        return str(int(v))
    return str(int(v)) if isinstance(v, int) else str(v)


def _int(v: Any) -> int:
    if isinstance(v, (int, float)):
        return int(v)
    if isinstance(v, str):
        return int(v) if v.lstrip('-').isdigit() else 0
    if v is None:
        return 0
    raise TypeError('not an int')


def _num(v: Any) -> Any:
    if isinstance(v, (int, float)):
        return int(v) if isinstance(v, bool) else v
    if isinstance(v, str):
        try:
            return int(v)
        except ValueError:
            return float(v)
    if v is None:
        return 0
    raise TypeError('not a number')


# ----------------------------------------------------------------------------------------------------------------------
# merging alternatives (both arms of a branch we could not decide)


def merge_envs(envs: list[dict]) -> dict:
    envs = [e for e in envs if e is not None]
    if not envs:
        return {}
    if len(envs) == 1:
        return envs[0]
    out = {}
    names = []
    for env in envs:
        for name in env:
            if name not in out:
                names.append(name)
                out[name] = None
    for name in names:
        values = [env[name] for env in envs if name in env]
        out[name] = merge_alternatives(values)
    return out


API_REQUIRED_BIT = 0x00001   # define('API_REQUIRED', 0x00001), defines.inc.php 7.0:1625, 7.4:1661, 8.0:1676


class MaybeFlags(int):
    """A flags value some of whose bits (``unsure``) only some paths set: API_REQUIRED under an ``if``."""

    unsure: int

    def __new__(cls, value: int, unsure: int):
        obj = int.__new__(cls, value)
        obj.unsure = unsure
        return obj


def merge_alternatives(values: list) -> Any:
    values = [v for v in values if v is not None] or values
    if not values:
        return None
    first = values[0]
    if all(v is first for v in values):
        return first
    dicts = [v for v in values if isinstance(v, dict)]
    if dicts:
        merged = dicts[0]
        for other in dicts[1:]:
            merged = merge_rule_alt(merged, other)
        return merged
    known = [v for v in values if not is_unknown(v)]
    ints = [v for v in known if isinstance(v, int) and not isinstance(v, bool)]
    if ints and len(ints) == len(values) and len({int(v) for v in ints}) > 1:
        union = 0
        common = ~0
        for v in ints:
            union |= int(v) & ~getattr(v, 'unsure', 0)
            common &= int(v) & ~getattr(v, 'unsure', 0)
        if (union ^ common) & ~API_REQUIRED_BIT == 0:
            # `$api_required = 0; if (…) { $api_required = API_REQUIRED; }` (CCepRule.php 8.0:513-519): required on some
            # paths only, which is not required (master_itemid is required for dependent items only)
            return MaybeFlags(union, union & ~common)
    if known:
        if all(isinstance(k, str) for k in known) and len({str(k) for k in known}) > 1:
            # alternative 'in' lists and similar: keep both, joined, with their constant names
            syms = {}
            for k in known:
                syms.update(syms_of(k))
            return PStr(','.join(dict.fromkeys(str(k) for k in known if str(k) != '')), syms)
        return known[0]
    origins = [v for v in values if is_unknown(v) and v.origin]
    return origins[0] if origins else first


def _is_list(d: dict) -> bool:
    return bool(d) and all(isinstance(k, int) for k in d) and sorted(d) == list(range(len(d)))


def merge_rule_alt(a: dict, b: dict) -> dict:
    """Two alternative arrays (usually two rule arrays): every key of either; flags keep API_REQUIRED only when both
    arms require; nested arrays merge the same way. Two lists are the union of their values ($is_admin ? A : B)."""
    if _is_list(a) and _is_list(b) and all(not isinstance(v, dict) for v in list(a.values()) + list(b.values())):
        values = list(a.values())
        for v in b.values():
            if not any(v is w or (type(v) is type(w) and v == w) for w in values):
                values.append(v)
        return dict(enumerate(values))
    if _is_field_map(a) and _is_field_map(b):
        # two maps of field rules ($item_type::getCreateValidationRules() for each item type, CItemGeneral.php 8.0:319):
        # a field only some alternatives name is never required (master_itemid is required for dependent items only)
        out = {}
        for key in list(a) + [k for k in b if k not in a]:
            if key in a and key in b and isinstance(a[key], dict) and isinstance(b[key], dict):
                out[key] = merge_rule_alt(a[key], b[key])
            else:
                out[key] = _optional(a[key] if key in a else b[key])
        return out
    out = dict(a)
    for key, bv in b.items():
        if key not in out:
            out[key] = bv
            continue
        av = out[key]
        if key == 'flags' and isinstance(av, int) and isinstance(bv, int):
            out[key] = (av & bv & REQUIRED_FLAG) | ((av | bv) & ~REQUIRED_FLAG)
        elif isinstance(av, dict) and isinstance(bv, dict):
            if key == 'fields':
                fields = dict(av)
                for fname, frule in bv.items():
                    if fname in fields and isinstance(fields[fname], dict) and isinstance(frule, dict):
                        fields[fname] = merge_rule_alt(fields[fname], frule)
                    elif fname not in fields:
                        fields[fname] = _optional(frule)
                for fname in av:
                    if fname not in bv:
                        fields[fname] = _optional(fields[fname])
                out[key] = fields
            else:
                out[key] = merge_rule_alt(av, bv)
        elif key == 'in' and isinstance(av, str) and isinstance(bv, str) and av != bv:
            out[key] = merge_alternatives([av, bv])
        elif key == 'type' and av != bv and isinstance(av, int) and isinstance(bv, int) and int(av) in WEAK_TYPES:
            # API_UNEXPECTED ("may not be sent here") or API_ANY in one arm: the other arm's type is the field's type
            out[key] = bv
        elif is_unknown(av) and not is_unknown(bv):
            out[key] = bv
    if 'flags' in a and 'flags' not in b or 'flags' in b and 'flags' not in a:
        f = out.get('flags')
        if isinstance(f, int):
            out['flags'] = f & ~REQUIRED_FLAG
    return out


def _is_field_map(d: dict) -> bool:
    """A map of field name → rule (not a rule itself: its 'type' is a rule, not an API_* constant)."""
    return bool(d) and all(isinstance(k, str) for k in d) and all(isinstance(v, dict) and isinstance(v.get('type'), int)
                                                                  and not isinstance(v.get('type'), bool) for v in d.values())


def _optional(rule: Any) -> Any:
    if isinstance(rule, dict) and isinstance(rule.get('flags'), int):
        rule = dict(rule)
        rule['flags'] = rule['flags'] & ~REQUIRED_FLAG
    return rule


# ----------------------------------------------------------------------------------------------------------------------
# builtins (the PHP functions the rules and their helpers use)


def _list(v: Any) -> list:
    if isinstance(v, dict):
        return list(v.values())
    raise TypeError('not an array')


def _as_arr(values: list) -> dict:
    return dict(enumerate(values))


def _implode(_, args):
    glue, pieces = (args[0], args[1]) if len(args) == 2 else ('', args[0])
    if isinstance(glue, dict):
        glue, pieces = pieces, glue
    items = _list(pieces)
    if any(is_unknown(x) or isinstance(x, dict) for x in items):
        return Unknown('implode')
    syms = {}
    for x in items:
        syms.update(syms_of(x))
    text = str(glue).join(_str(x) for x in items)
    return PStr(text, syms) if syms else text


def _array_merge(_, args):
    if any(not isinstance(a, dict) for a in args):
        unknown = next((a for a in args if is_unknown(a)), None)
        return Unknown('array_merge', unknown.origin if unknown is not None else None)
    out: dict = {}
    index = 0
    for a in args:
        for k, v in a.items():
            if isinstance(k, int):
                out[index] = v
                index += 1
            else:
                out[k] = v
    return out


def _zbx_array_merge(interp: Interpreter, args):
    known = [a for a in args if isinstance(a, dict)]
    unknown = [a for a in args if is_unknown(a) and a.origin]
    if unknown and known:
        defaults = {}
        for a in known:
            defaults.update(a)
        interp.trace.defaults.append((unknown[0].origin, defaults))
        return Unknown('options', unknown[0].origin, defaults)
    return _array_merge(interp, args)


def _array_keys(_, args):
    a = args[0]
    if isinstance(a, dict):
        return _as_arr(list(a.keys()))
    return Unknown('array_keys')


def _array_diff(_, args):
    base = args[0]
    if not isinstance(base, dict) or any(not isinstance(a, dict) for a in args[1:]):
        return Unknown('array_diff')
    remove = {_str(v) for a in args[1:] for v in a.values()}
    return {k: v for k, v in base.items() if _str(v) not in remove}


def _array_diff_key(interp, args):
    base = args[0]
    if is_unknown(base) and base.origin and len(args) == 2 and isinstance(args[1], dict):
        if interp.refusing:
            # `if (array_diff_key($drule, $allowed_fields)) { self::exception(…) }` (CDRule.php 8.0:207): an allow-list.
            # Elsewhere array_diff_key only drops keys (CHost::updateForce, 7.0:841), which says nothing of the input.
            interp.trace.allowed.append((base.origin, [str(k) for k in args[1]]))
        return Unknown('array_diff_key')
    if not isinstance(base, dict) or any(not isinstance(a, dict) for a in args[1:]):
        return Unknown('array_diff_key')
    remove = {k for a in args[1:] for k in a}
    return {k: v for k, v in base.items() if k not in remove}


def _array_intersect_key(_, args):
    base = args[0]
    if is_unknown(base) and base.origin:
        # a projection of the request (CGraphGeneral::validateCreate, 7.0:800; CHost::validateGet, 7.0:2008): what
        # validates it validates only the keys it kept
        return Unknown('partial', base.origin)
    if not isinstance(base, dict) or any(not isinstance(a, dict) for a in args[1:]):
        return Unknown('array_intersect_key')
    keep = set.intersection(*[set(a) for a in args[1:]])
    return {k: v for k, v in base.items() if k in keep}


def _array_fill_keys(_, args):
    keys, value = args
    return {_key(k): value for k in _list(keys)}


def _array_flip(_, args):
    return {_key(v): k for k, v in args[0].items()}


def _range(_, args):
    start, stop = _int(args[0]), _int(args[1])
    step = _int(args[2]) if len(args) > 2 else 1
    if abs(stop - start) > 10000:
        return Unknown('range')
    return _as_arr(list(range(start, stop + (1 if step > 0 else -1), step)))


def _array_values(_, args):
    return _as_arr(_list(args[0])) if isinstance(args[0], dict) else Unknown('array_values', getattr(args[0], 'origin', None))


def _array_column(_, args):
    rows, column = args[0], args[1]
    if not isinstance(rows, dict):
        return Unknown('array_column')
    out = []
    for row in rows.values():
        if isinstance(row, dict) and column in row:
            out.append(row[column])
    return _as_arr(out)


def _array_unique(_, args):
    seen = {}
    for k, v in args[0].items():
        if _str(v) not in {_str(x) for x in seen.values()}:
            seen[k] = v
    return seen


def _explode(_, args):
    sep, text = args[0], args[1]
    if not isinstance(text, str):
        return Unknown('explode')
    return _as_arr(text.split(sep))


def _array_key_exists(_, args):
    key, arr = args[0], args[1]
    if isinstance(arr, dict) and not is_unknown(key):
        return _key(key) in arr
    return Unknown('array_key_exists')


def _in_array(_, args):
    needle, hay = args[0], args[1]
    if isinstance(hay, dict) and not is_unknown(needle) and not any(is_unknown(v) for v in hay.values()):
        return any(_loose_eq(needle, v) for v in hay.values())
    return Unknown('in_array')


def _count(_, args):
    return len(args[0]) if isinstance(args[0], dict) else Unknown('count')


def _max(_, args):
    vals = _list(args[0]) if len(args) == 1 else args
    if any(is_unknown(v) for v in vals):
        return Unknown('max')
    return max(vals)


def _min(_, args):
    vals = _list(args[0]) if len(args) == 1 else args
    if any(is_unknown(v) for v in vals):
        return Unknown('min')
    return min(vals)


def _array_map(_, args):
    return Unknown('array_map')


def _translate(_, args):
    return args[0] if args and isinstance(args[0], str) else ''


def _array_combine(_, args):
    return dict(zip([_key(k) for k in _list(args[0])], _list(args[1])))


def _array_slice(_, args):
    items = list(args[0].items())
    start = _int(args[1])
    length = _int(args[2]) if len(args) > 2 and args[2] is not None else None
    part = items[start:start + length] if length is not None else items[start:]
    return {k: v for k, v in part} if not all(isinstance(k, int) for k, _ in part) else _as_arr([v for _, v in part])


def _array_filter(_, args):
    if len(args) == 1 and isinstance(args[0], dict):
        return {k: v for k, v in args[0].items() if truth(v)}
    return Unknown('array_filter')


def _array_replace(_, args):
    if any(not isinstance(a, dict) for a in args):
        return Unknown('array_replace')
    out = {}
    for a in args:
        out.update(a)
    return out


def _array_search(_, args):
    needle, hay = args[0], args[1]
    if isinstance(hay, dict):
        for k, v in hay.items():
            if _loose_eq(needle, v):
                return k
        return False
    return Unknown('array_search')


def _array_intersect(_, args):
    base = args[0]
    others = [{_str(v) for v in a.values()} for a in args[1:]]
    return {k: v for k, v in base.items() if all(_str(v) in o for o in others)}


def _is_array(_, args):
    return isinstance(args[0], dict) if not is_unknown(args[0]) else Unknown('is_array')


def _check_db_fields(interp, args):
    fields, data = args[0], args[1] if len(args) > 1 else None
    if isinstance(fields, dict) and is_unknown(data) and data.origin:
        interp.trace.mandatory.append((data.origin, [str(k) for k, v in fields.items() if v is None]))
    return Unknown('check_db_fields')


def _unknown_bool(name):
    return lambda _, args: Unknown(name)


BUILTINS = {
    'implode': _implode, 'join': _implode,
    'array_merge': _array_merge, 'zbx_array_merge': _zbx_array_merge,
    'array_keys': _array_keys, 'array_values': _array_values,
    'array_diff': _array_diff, 'array_diff_key': _array_diff_key,
    'array_intersect_key': _array_intersect_key, 'array_intersect': _array_intersect,
    'array_fill_keys': _array_fill_keys, 'array_flip': _array_flip,
    'array_column': _array_column, 'array_unique': _array_unique,
    'array_combine': _array_combine, 'array_slice': _array_slice,
    'array_filter': _array_filter, 'array_replace': _array_replace,
    'array_search': _array_search,
    'range': _range, 'explode': _explode,
    'array_key_exists': _array_key_exists, 'in_array': _in_array,
    'count': _count, 'max': _max, 'min': _min,
    'array_map': _array_map, 'is_array': _is_array,
    '_': _translate, '_s': _translate, '_n': _translate, '_x': _translate, '_params': _translate,
    'strtolower': lambda _, a: a[0].lower() if isinstance(a[0], str) else Unknown('strtolower'),
    'strtoupper': lambda _, a: a[0].upper() if isinstance(a[0], str) else Unknown('strtoupper'),
    'ucfirst': lambda _, a: a[0][:1].upper() + a[0][1:] if isinstance(a[0], str) else Unknown('ucfirst'),
    'intval': lambda _, a: _int(a[0]),
    'strval': lambda _, a: _str(a[0]),
    'array_key_first': lambda _, a: next(iter(a[0]), None) if isinstance(a[0], dict) else Unknown('akf'),
    'reset': lambda _, a: next(iter(a[0].values()), False) if isinstance(a[0], dict) else Unknown('reset'),
    'sprintf': lambda _, a: Unknown('sprintf'),
    'getschema': lambda interp, a: interp.schema.get(a[0], Unknown('schema')),
    'check_db_fields': lambda interp, a: _check_db_fields(interp, a),
    # request-preserving helpers: the value keeps its origin
    'zbx_toarray': lambda _, a: a[0] if is_unknown(a[0]) else (a[0] if isinstance(a[0], dict) else {0: a[0]}),
    'zbx_tohash': lambda _, a: a[0] if is_unknown(a[0]) else Unknown('zbx_toHash'),
}
