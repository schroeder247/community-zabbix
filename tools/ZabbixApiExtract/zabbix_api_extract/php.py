# This Source Code Form is subject to the terms of the Mozilla Public
# License, v. 2.0. Copyright (c) 2026 schroeder247. SPDX-License-Identifier: MPL-2.0
"""A lexer, parser and abstract interpreter for the subset of PHP the Zabbix frontend's API services are written in.

The interpreter never runs PHP for effect. It follows an API method from its entry point through the class chain and
the helper classes it calls, evaluates every constant it can (``defines.inc.php``, class constants with ``parent``
walking, ``DB::getFieldLength``), and treats everything that depends on the request or the database as an ``Unknown``
that remembers *where in the request* it came from (its origin). When the code hands a rule array and a request-derived
value to ``CApiInputValidator::validate``, the interpreter records the pair: that record is the fact the extractor is
after. Both arms of a branch whose condition it cannot decide are taken; loops run their body once.
"""
from __future__ import annotations

import re
from dataclasses import dataclass, field
from typing import Any

# ----------------------------------------------------------------------------------------------------------------------
# values

# a file that declares a namespace (namespace Zabbix\Core;): its classes are not the global ones of the same short name
NAMESPACE = re.compile(r'^\s*namespace\s+[\w\\]+\s*;', re.M)


class Unknown:
    """A value the interpreter cannot know. ``origin`` is the request path it came from, if any: ``('in',)`` is the
    method's parameter, ``('in', '[]')`` an element of it, ``('in', '[]', 'interfaces')`` a field of an element.
    ``keys`` carries the defaults a ``zbx_array_merge($defOptions, $options)`` laid under the request."""

    __slots__ = ('why', 'origin', 'keys')

    def __init__(self, why: str = '?', origin: tuple | None = None, keys: dict | None = None):
        self.why = why
        self.origin = origin
        self.keys = keys

    def __repr__(self) -> str:
        return f'?{self.why}' + (f'@{"/".join(self.origin)}' if self.origin else '')


class Sym(int):
    """An integer that remembers the constant it was read from, so an ``in`` list can be named from the defines."""

    name: str

    def __new__(cls, value: int, name: str):
        obj = int.__new__(cls, value)
        obj.name = name
        return obj


class PStr(str):
    """A string that remembers which constants were concatenated or imploded into it (value → constant name)."""

    syms: dict

    def __new__(cls, value: str, syms: dict | None = None):
        obj = str.__new__(cls, value)
        obj.syms = syms or {}
        return obj


def syms_of(value: Any) -> dict:
    if isinstance(value, Sym):
        return {int(value): value.name}
    if isinstance(value, PStr):
        return dict(value.syms)
    return {}


def is_unknown(v: Any) -> bool:
    return isinstance(v, Unknown)


# ----------------------------------------------------------------------------------------------------------------------
# lexer

_TOKEN = re.compile(r'''
    (?P<ws>\s+)
  | (?P<comment>//[^\n]*|\#(?!\[)[^\n]*|/\*.*?\*/)
  | (?P<attr>\#\[)
  | (?P<heredoc><<<\s*(?P<hq>['"]?)(?P<hid>\w+)(?P=hq)\n.*?\n\s*(?P=hid)\b)
  | (?P<num>0[xX][0-9a-fA-F_]+|0[bB][01_]+|\d[\d_]*\.\d+(?:[eE][+-]?\d+)?|\d[\d_]*(?:[eE][+-]?\d+)?|\.\d+)
  | (?P<sq>'(?:[^'\\]|\\.)*')
  | (?P<dq>"(?:[^"\\]|\\.)*")
  | (?P<var>\$\w+)
  | (?P<name>\\?[A-Za-z_]\w*(?:\\\w+)*)
  | (?P<op><=>|\*\*=|\?\?=|\.\.\.|<<=|>>=|===|!==|\?->|::|->|=>|\+\+|--|\+=|-=|\*=|/=|\.=|%=|&=|\|=|\^=|\?\?|==|!=|<>|<=|>=|&&|\|\||<<|>>|\*\*|[-+*/%.=<>!&|^~?:;,()\[\]{}@$])
''', re.S | re.X)


@dataclass(frozen=True)
class Tok:
    kind: str   # num, str, var, name, op, eof
    text: str
    pos: int


def tokenize(src: str) -> list[Tok]:
    out: list[Tok] = []
    i = 0
    # Skip everything before the first "<?php".
    start = src.find('<?php')
    if start >= 0:
        i = start + 5
    n = len(src)
    while i < n:
        m = _TOKEN.match(src, i)
        if not m:
            # An unknown character: skip it, keep going (the parser recovers at statement level).
            i += 1
            continue
        kind = m.lastgroup
        text = m.group(kind) if kind not in ('hq', 'hid') else m.group(0)
        i = m.end()
        if kind in ('ws', 'comment'):
            continue
        if kind == 'attr':
            # A PHP 8 attribute: skip to the matching ']'.
            depth = 1
            while i < n and depth:
                if src[i] == '[':
                    depth += 1
                elif src[i] == ']':
                    depth -= 1
                i += 1
            continue
        if kind == 'heredoc':
            out.append(Tok('str', '', m.start()))
            continue
        if kind == 'sq':
            body = text[1:-1].replace("\\'", "'").replace('\\\\', '\\')
            out.append(Tok('str', body, m.start()))
            continue
        if kind == 'dq':
            body = text[1:-1]
            if '$' in body:
                out.append(Tok('istr', body, m.start()))  # interpolated: unknown at analysis time
            else:
                out.append(Tok('str', body.replace('\\"', '"').replace('\\n', '\n').replace('\\t', '\t').replace('\\\\', '\\'), m.start()))
            continue
        out.append(Tok(kind, text, m.start()))
    out.append(Tok('eof', '', n))
    return out


# ----------------------------------------------------------------------------------------------------------------------
# AST


@dataclass
class Node:
    kind: str
    a: Any = None
    b: Any = None
    c: Any = None
    d: Any = None
    pos: int = 0


class ParseError(Exception):
    pass


_BINARY = {
    'or': 1, 'xor': 2, 'and': 3,
    '??': 7, '||': 8, '&&': 9, '|': 10, '^': 11, '&': 12,
    '==': 13, '!=': 13, '===': 13, '!==': 13, '<>': 13, '<=>': 13,
    '<': 14, '>': 14, '<=': 14, '>=': 14,
    '<<': 15, '>>': 15,
    '+': 16, '-': 16, '.': 16,
    '*': 17, '/': 17, '%': 17,
    'instanceof': 19,
    '**': 20,
}
_ASSIGN = {'=', '+=', '-=', '*=', '/=', '.=', '%=', '&=', '|=', '^=', '??=', '**=', '<<=', '>>='}
_CASTS = {'int', 'integer', 'string', 'array', 'bool', 'boolean', 'float', 'double', 'object'}


class Parser:
    def __init__(self, toks: list[Tok]):
        self.t = toks
        self.i = 0

    # -- helpers
    def peek(self, k: int = 0) -> Tok:
        return self.t[min(self.i + k, len(self.t) - 1)]

    def next(self) -> Tok:
        tok = self.t[self.i]
        if self.i < len(self.t) - 1:
            self.i += 1
        return tok

    def at(self, text: str, k: int = 0) -> bool:
        tok = self.peek(k)
        if tok.kind == 'op':
            return tok.text == text
        if tok.kind == 'name':
            return tok.text.lower() == text
        return False

    def accept(self, text: str) -> bool:
        if self.at(text):
            self.next()
            return True
        return False

    def expect(self, text: str) -> Tok:
        if not self.at(text):
            tok = self.peek()
            raise ParseError(f'expected {text!r} at {tok.pos}, got {tok.kind} {tok.text!r}')
        return self.next()

    def skip_balanced(self, open_: str, close: str) -> None:
        """At an opening bracket: skip to just past its matching close."""
        self.expect(open_)
        depth = 1
        while depth and self.peek().kind != 'eof':
            tok = self.next()
            if tok.kind == 'op' and tok.text == open_:
                depth += 1
            elif tok.kind == 'op' and tok.text == close:
                depth -= 1

    # -- statements
    def block(self) -> list[Node]:
        if self.accept('{'):
            out = []
            while not self.at('}') and self.peek().kind != 'eof':
                out.append(self.statement())
            self.expect('}')
            return out
        if self.accept(':'):   # alternative syntax (if: ... endif;)
            out = []
            while not (self.peek().kind == 'name' and self.peek().text.lower() in ('endif', 'endforeach', 'endfor', 'endwhile', 'else', 'elseif', 'endswitch')):
                out.append(self.statement())
            return out
        return [self.statement()]

    def statement(self) -> Node:
        start = self.i
        try:
            return self._statement()
        except (ParseError, IndexError, RecursionError):
            # Recover: skip to the end of this statement (a ';' at depth 0 or a balanced block) and move on.
            self.i = start
            depth = 0
            while self.peek().kind != 'eof':
                tok = self.next()
                if tok.kind == 'op' and tok.text in ('(', '[', '{'):
                    depth += 1
                elif tok.kind == 'op' and tok.text in (')', ']', '}'):
                    depth -= 1
                    if depth < 0:
                        self.i -= 1
                        break
                    if depth == 0 and tok.text == '}':
                        break
                elif tok.kind == 'op' and tok.text == ';' and depth == 0:
                    break
            return Node('skip', pos=self.t[start].pos)

    def _statement(self) -> Node:
        tok = self.peek()
        pos = tok.pos
        if tok.kind == 'op' and tok.text == '{':
            return Node('block', self.block(), pos=pos)
        if tok.kind == 'op' and tok.text == ';':
            self.next()
            return Node('nop', pos=pos)
        if tok.kind == 'name':
            word = tok.text.lower()
            if word == 'return':
                self.next()
                if self.accept(';'):
                    return Node('return', None, pos=pos)
                e = self.expr()
                self.accept(';')
                return Node('return', e, pos=pos)
            if word == 'if':
                self.next()
                self.expect('(')
                cond = self.expr()
                self.expect(')')
                then = self.block()
                arms = [(cond, then)]
                other = None
                while True:
                    if self.at('elseif'):
                        self.next()
                        self.expect('(')
                        c = self.expr()
                        self.expect(')')
                        arms.append((c, self.block()))
                        continue
                    if self.at('else') and self.peek(1).kind == 'name' and self.peek(1).text.lower() == 'if':
                        self.next()
                        self.next()
                        self.expect('(')
                        c = self.expr()
                        self.expect(')')
                        arms.append((c, self.block()))
                        continue
                    if self.at('else'):
                        self.next()
                        other = self.block()
                    break
                if self.at('endif'):
                    self.next()
                    self.accept(';')
                return Node('if', arms, other, pos=pos)
            if word == 'foreach':
                self.next()
                self.expect('(')
                subject = self.expr()
                self.expect('as')
                key = None
                self.accept('&')
                value = self.expr_no_assign()
                if self.accept('=>'):
                    key = value
                    self.accept('&')
                    value = self.expr_no_assign()
                self.expect(')')
                body = self.block()
                if self.at('endforeach'):
                    self.next()
                    self.accept(';')
                return Node('foreach', subject, key, value, body, pos=pos)
            if word in ('for',):
                self.next()
                self.skip_balanced('(', ')')
                body = self.block()
                return Node('loop', body, pos=pos)
            if word == 'while':
                self.next()
                self.expect('(')
                cond = self.expr()
                self.expect(')')
                body = self.block()
                return Node('while', cond, body, pos=pos)
            if word == 'do':
                self.next()
                body = self.block()
                self.expect('while')
                self.skip_balanced('(', ')')
                self.accept(';')
                return Node('loop', body, pos=pos)
            if word == 'switch':
                self.next()
                self.expect('(')
                subject = self.expr()
                self.expect(')')
                self.expect('{')
                cases = []
                current = None
                while not self.at('}'):
                    if self.at('case'):
                        self.next()
                        value = self.expr()
                        if not self.accept(':'):
                            self.expect(';')
                        current = (value, [])
                        cases.append(current)
                    elif self.at('default'):
                        self.next()
                        if not self.accept(':'):
                            self.expect(';')
                        current = (None, [])
                        cases.append(current)
                    else:
                        stmt = self.statement()
                        if current is not None:
                            current[1].append(stmt)
                self.expect('}')
                return Node('switch', subject, cases, pos=pos)
            if word == 'try':
                self.next()
                body = self.block()
                handlers = []
                while self.at('catch'):
                    self.next()
                    self.skip_balanced('(', ')')
                    handlers.append(self.block())
                fin = []
                if self.at('finally'):
                    self.next()
                    fin = self.block()
                return Node('try', body, handlers, fin, pos=pos)
            if word in ('break', 'continue'):
                self.next()
                if self.peek().kind == 'num':
                    self.next()
                self.accept(';')
                return Node(word, pos=pos)
            if word == 'throw':
                self.next()
                self.expr()
                self.accept(';')
                return Node('throw', pos=pos)
            if word in ('global', 'echo', 'unset', 'static') and not (word == 'static' and self.peek(1).kind == 'op' and self.peek(1).text in ('::',)):
                if word == 'static' and self.peek(1).kind == 'var':
                    self.next()
                    target = self.expr()
                    self.accept(';')
                    return Node('expr', target, pos=pos)
                if word == 'unset':
                    self.next()
                    self.expect('(')
                    targets = []
                    while not self.at(')'):
                        targets.append(self.expr())
                        if not self.accept(','):
                            break
                    self.expect(')')
                    self.accept(';')
                    return Node('unset', targets, pos=pos)
                self.next()
                while not self.at(';') and self.peek().kind != 'eof':
                    self.next()
                self.accept(';')
                return Node('nop', pos=pos)
            if word == 'function' and self.peek(1).kind == 'name':
                # A nested named function (rare): skip it.
                self.next()
                self.next()
                self.skip_balanced('(', ')')
                if self.accept(':'):
                    self.type_hint()
                self.skip_balanced('{', '}')
                return Node('nop', pos=pos)
        e = self.expr()
        self.accept(';')
        return Node('expr', e, pos=pos)

    def type_hint(self) -> None:
        self.accept('?')
        while True:
            tok = self.peek()
            if tok.kind == 'name' or (tok.kind == 'op' and tok.text in ('(', ')')):
                self.next()
            else:
                break
            if not (self.accept('|') or self.accept('&')):
                break

    # -- expressions
    def expr(self, min_prec: int = 0) -> Node:
        left = self.unary()
        while True:
            tok = self.peek()
            text = tok.text.lower() if tok.kind == 'name' else tok.text
            if tok.kind == 'op' and text in _ASSIGN:
                if min_prec > 4:
                    return left
                self.next()
                right = self.expr(4)
                left = Node('assign', text, left, right, pos=tok.pos)
                continue
            if tok.kind == 'op' and text == '?':
                if min_prec > 6:
                    return left
                self.next()
                if self.accept(':'):
                    other = self.expr(7)
                    left = Node('elvis', left, other, pos=tok.pos)
                    continue
                then = self.expr(5)
                self.expect(':')
                other = self.expr(7)
                left = Node('ternary', left, then, other, pos=tok.pos)
                continue
            prec = _BINARY.get(text) if tok.kind in ('op', 'name') else None
            if prec is None or prec < min_prec or (tok.kind == 'name' and text not in ('and', 'or', 'xor', 'instanceof')):
                return left
            self.next()
            if text == 'instanceof':
                # $this instanceof CTriggerPrototype: the class a shared method runs for (CTriggerGeneral.php 8.0:1134)
                left = Node('instanceof', left, self.unary(), pos=tok.pos)
                continue
            # '??' and '**' are right-associative.
            right = self.expr(prec if text in ('??', '**') else prec + 1)
            left = Node('bin', text, left, right, pos=tok.pos)

    def expr_no_assign(self) -> Node:
        return self.expr(5)

    def unary(self) -> Node:
        tok = self.peek()
        if tok.kind == 'op':
            if tok.text in ('!', '-', '+', '~'):
                self.next()
                operand = self.expr(18)
                return Node('unary', tok.text, operand, pos=tok.pos)
            if tok.text == '@':
                self.next()
                return self.unary()
            if tok.text == '&':
                self.next()
                return self.unary()
            if tok.text in ('++', '--'):
                self.next()
                operand = self.unary()
                return Node('incdec', operand, pos=tok.pos)
            if tok.text == '(' and self.peek(1).kind == 'name' and self.peek(1).text.lower() in _CASTS and self.peek(2).kind == 'op' and self.peek(2).text == ')':
                self.next()
                cast = self.next().text.lower()
                self.next()
                operand = self.expr(18)
                return Node('cast', cast, operand, pos=tok.pos)
            if tok.text == '...':
                self.next()
                return Node('spread', self.unary(), pos=tok.pos)
        if tok.kind == 'name':
            word = tok.text.lower()
            if word == 'new':
                self.next()
                if self.at('class'):
                    raise ParseError('anonymous class')
                cname = None
                if self.peek().kind in ('name', 'var'):
                    cname = self.next().text.lstrip('\\')
                    while self.peek().kind == 'op' and self.peek().text in ('::', '->'):
                        self.next()
                        self.next()
                        cname = None
                args = self.args() if self.at('(') else []
                return self.postfix(Node('new', args, cname, pos=tok.pos))
            if word == 'clone':
                self.next()
                return self.unary()
            if word == 'print':
                self.next()
                self.expr()
                return Node('unknown', 'print', pos=tok.pos)
            if word in ('include', 'require', 'include_once', 'require_once'):
                self.next()
                self.expr()
                return Node('unknown', word, pos=tok.pos)
            if word in ('function', 'fn') or (word == 'static' and self.peek(1).kind == 'name' and self.peek(1).text.lower() in ('function', 'fn')):
                return self.closure()
        return self.postfix(self.primary())

    def closure(self) -> Node:
        pos = self.peek().pos
        if self.at('static'):
            self.next()
        arrow = self.next().text.lower() == 'fn'
        self.accept('&')
        self.skip_balanced('(', ')')
        if self.at('use'):
            self.next()
            self.skip_balanced('(', ')')
        if self.accept(':'):
            self.type_hint()
        if arrow:
            self.expect('=>')
            self.expr(5)
        else:
            self.skip_balanced('{', '}')
        return Node('closure', pos=pos)

    def args(self) -> list[Node]:
        self.expect('(')
        out = []
        while not self.at(')'):
            # named arguments: name: value
            if self.peek().kind == 'name' and self.peek(1).kind == 'op' and self.peek(1).text == ':' and not (self.peek(2).kind == 'op' and self.peek(2).text == ':'):
                self.next()
                self.next()
            out.append(self.expr())
            if not self.accept(','):
                break
        self.expect(')')
        return out

    def array_literal(self, close: str) -> Node:
        pos = self.peek().pos
        items = []
        while not self.at(close):
            if self.at(','):   # list() holes
                self.next()
                items.append((None, Node('lit', None)))
                continue
            k = self.expr()
            if self.accept('=>'):
                v = self.expr()
                items.append((k, v))
            else:
                items.append((None, k))
            if not self.accept(','):
                break
        self.expect(close)
        return Node('array', items, pos=pos)

    def primary(self) -> Node:
        tok = self.next()
        pos = tok.pos
        if tok.kind == 'num':
            text = tok.text.replace('_', '')
            if text.lower().startswith('0x'):
                return Node('lit', int(text, 16), pos=pos)
            if text.lower().startswith('0b'):
                return Node('lit', int(text[2:], 2), pos=pos)
            if '.' in text or 'e' in text.lower():
                return Node('lit', float(text), pos=pos)
            return Node('lit', int(text), pos=pos)
        if tok.kind == 'str':
            return Node('lit', tok.text, pos=pos)
        if tok.kind == 'istr':
            return Node('unknown', 'interpolated', pos=pos)
        if tok.kind == 'var':
            return Node('var', tok.text[1:], pos=pos)
        if tok.kind == 'op':
            if tok.text == '[':
                return self.array_literal(']')
            if tok.text == '(':
                e = self.expr()
                self.expect(')')
                return e
            if tok.text == '$':   # variable variable
                self.unary()
                return Node('unknown', 'varvar', pos=pos)
            raise ParseError(f'unexpected {tok.text!r} at {pos}')
        if tok.kind == 'name':
            word = tok.text.lower()
            name = tok.text.lstrip('\\')
            if word in ('array', 'list') and self.at('('):
                self.next()
                return self.array_literal(')')
            if word in ('true', 'false', 'null'):
                return Node('lit', {'true': True, 'false': False, 'null': None}[word], pos=pos)
            if word == 'isset' or word == 'empty':
                args = self.args()
                return Node('isset' if word == 'isset' else 'empty', args, pos=pos)
            if word == 'match' and self.at('('):
                self.next()
                subject = self.expr()
                self.expect(')')
                self.expect('{')
                arms = []
                while not self.at('}'):
                    conds = []
                    if self.at('default'):
                        self.next()
                        conds = None
                    else:
                        while True:
                            conds.append(self.expr())
                            if not self.accept(','):
                                break
                            if self.at('=>'):
                                break
                    self.expect('=>')
                    arms.append((conds, self.expr()))
                    if not self.accept(','):
                        break
                self.expect('}')
                return Node('match', subject, arms, pos=pos)
            if self.at('::'):
                self.next()
                member = self.next()
                if member.kind == 'var':
                    return Node('sprop', name, member.text[1:], pos=pos)
                if member.kind == 'name' and member.text.lower() == 'class':
                    return Node('lit', name, pos=pos)
                if self.at('('):
                    return Node('scall', name, member.text, self.args(), pos=pos)
                return Node('cconst', name, member.text, pos=pos)
            if self.at('('):
                return Node('call', name, self.args(), pos=pos)
            return Node('const', name, pos=pos)
        raise ParseError(f'unexpected token {tok.kind} {tok.text!r} at {pos}')

    def postfix(self, node: Node) -> Node:
        while True:
            tok = self.peek()
            if tok.kind != 'op':
                return node
            if tok.text == '[':
                self.next()
                if self.accept(']'):
                    node = Node('index', node, None, pos=tok.pos)
                    continue
                key = self.expr()
                self.expect(']')
                node = Node('index', node, key, pos=tok.pos)
            elif tok.text == '{' and node.kind == 'var':
                return node
            elif tok.text in ('->', '?->'):
                self.next()
                member = self.next()
                if member.kind == 'op' and member.text == '{':
                    self.expr()
                    self.expect('}')
                    node = Node('unknown', 'dynprop', pos=tok.pos)
                    continue
                mname = member.text[1:] if member.kind == 'var' else member.text
                if self.at('('):
                    node = Node('mcall', node, mname, self.args(), pos=tok.pos)
                else:
                    node = Node('prop', node, mname, pos=tok.pos)
            elif tok.text == '::':
                self.next()
                member = self.next()
                if member.kind == 'var':
                    node = Node('unknown', 'dynsprop', pos=tok.pos)
                elif self.at('('):
                    node = Node('dscall', node, member.text, self.args(), pos=tok.pos)
                else:
                    node = Node('dcconst', node, member.text, pos=tok.pos)
            elif tok.text == '(' and node.kind in ('var', 'closure', 'index', 'prop'):
                args = self.args()
                node = Node('unknown', 'dyncall', args, pos=tok.pos)
            elif tok.text in ('++', '--'):
                self.next()
                node = Node('incdec', node, pos=tok.pos)
            else:
                return node


# ----------------------------------------------------------------------------------------------------------------------
# class and function model


@dataclass
class Function:
    name: str
    cls: str | None
    static: bool
    params: list[tuple[str, Node | None, bool]]   # name, default, by-reference
    body: list[Node] | None                      # None: abstract / interface
    file: str
    line: int
    _toks: list[Tok] | None = None
    _span: tuple[int, int] | None = None

    def parsed_body(self) -> list[Node]:
        if self.body is None and self._span is not None:
            p = Parser(self._toks)
            p.i = self._span[0]
            self.body = p.block()
        return self.body or []


@dataclass
class ClassDef:
    name: str
    parent: str | None
    traits: list[str]
    kind: str                                   # class, trait, interface
    consts: dict[str, Node] = field(default_factory=dict)
    props: dict[str, tuple[Node | None, bool]] = field(default_factory=dict)   # name -> (default, static)
    methods: dict[str, Function] = field(default_factory=dict)                 # lower-cased name -> function
    file: str = ''
    abstract: bool = False


def line_of(src: str, pos: int) -> int:
    return src.count('\n', 0, pos) + 1


def parse_file(path: str, src: str) -> tuple[list[ClassDef], dict[str, Function]]:
    """Every class, trait and interface in a file, and its free functions. Method bodies are parsed lazily."""
    toks = tokenize(src)
    classes: list[ClassDef] = []
    functions: dict[str, Function] = {}
    p = Parser(toks)
    depth = 0
    while p.peek().kind != 'eof':
        tok = p.peek()
        word = tok.text.lower() if tok.kind == 'name' else None
        if tok.kind == 'op' and tok.text == '{':
            depth += 1
            p.next()
            continue
        if tok.kind == 'op' and tok.text == '}':
            depth -= 1
            p.next()
            continue
        if depth == 0 and word in ('class', 'trait', 'interface', 'enum') and p.peek(1).kind == 'name':
            abstract = p.i > 0 and toks[p.i - 1].kind == 'name' and toks[p.i - 1].text.lower() == 'abstract'
            p.next()
            name = p.next().text
            parent = None
            if p.at('extends'):
                p.next()
                parent = p.next().text.lstrip('\\')
                while p.accept(','):
                    p.next()
            if p.at('implements'):
                p.next()
                while p.peek().kind == 'name':
                    p.next()
                    if not p.accept(','):
                        break
            cls = ClassDef(name, parent, [], word, file=path, abstract=abstract)
            _class_body(p, cls, toks, src, path)
            classes.append(cls)
            continue
        if depth == 0 and word == 'function' and p.peek(1).kind in ('name',) and not (p.peek(1).kind == 'op'):
            fn = _function(p, None, False, toks, src, path)
            if fn:
                functions[fn.name.lower()] = fn
            continue
        p.next()
    return classes, functions


def _params(p: Parser) -> list[tuple[str, Node | None, bool]]:
    out = []
    p.expect('(')
    while not p.at(')'):
        # modifiers (promoted constructor properties), type hints
        while p.peek().kind == 'name' and p.peek().text.lower() in ('public', 'protected', 'private', 'readonly'):
            p.next()
        if not (p.peek().kind == 'var' or p.at('&') or p.at('...')):
            p.type_hint()
        byref = p.accept('&')
        p.accept('...')
        name = p.next().text[1:]
        default = None
        if p.accept('='):
            default = p.expr()
        out.append((name, default, byref))
        if not p.accept(','):
            break
    p.expect(')')
    return out


def _function(p: Parser, cls: str | None, static: bool, toks: list[Tok], src: str, path: str) -> Function | None:
    start = p.peek().pos
    p.expect('function')
    p.accept('&')
    name = p.next().text
    params = _params(p)
    if p.accept(':'):
        p.type_hint()
    if p.accept(';'):
        return Function(name, cls, static, params, None, path, line_of(src, start))
    body_start = p.i
    p.skip_balanced('{', '}')
    return Function(name, cls, static, params, None, path, line_of(src, start), toks, (body_start, p.i))


def _class_body(p: Parser, cls: ClassDef, toks: list[Tok], src: str, path: str) -> None:
    p.expect('{')
    while not p.at('}') and p.peek().kind != 'eof':
        modifiers = set()
        while p.peek().kind == 'name' and p.peek().text.lower() in ('public', 'protected', 'private', 'static', 'abstract', 'final', 'readonly', 'var'):
            modifiers.add(p.next().text.lower())
        tok = p.peek()
        word = tok.text.lower() if tok.kind == 'name' else None
        if word == 'use':
            p.next()
            while True:
                cls.traits.append(p.next().text.lstrip('\\'))
                if not p.accept(','):
                    break
            if p.at('{'):
                p.skip_balanced('{', '}')
            else:
                p.accept(';')
            continue
        if word == 'const':
            p.next()
            # optional type
            if p.peek(1).kind == 'name':
                p.next()
            while True:
                cname = p.next().text
                p.expect('=')
                cls.consts[cname] = p.expr()
                if not p.accept(','):
                    break
            p.accept(';')
            continue
        if word == 'case':   # enum case
            while not p.at(';'):
                p.next()
            p.next()
            continue
        if word == 'function':
            fn = _function(p, cls.name, 'static' in modifiers, toks, src, path)
            if fn:
                cls.methods[fn.name.lower()] = fn
            continue
        # property: [type] $name [= default] (, $name2 ...);
        if tok.kind in ('name',) or (tok.kind == 'op' and tok.text == '?'):
            p.type_hint()
        if p.peek().kind == 'var':
            while True:
                pname = p.next().text[1:]
                default = p.expr() if p.accept('=') else None
                cls.props[pname] = (default, 'static' in modifiers)
                if not p.accept(','):
                    break
            p.accept(';')
            continue
        p.next()
    p.expect('}')
