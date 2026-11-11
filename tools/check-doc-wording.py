#!/usr/bin/env python3
# This Source Code Form is subject to the terms of the Mozilla Public
# License, v. 2.0. Copyright (c) 2026 schroeder247. SPDX-License-Identifier: MPL-2.0
"""Checks that every description in schema/overlay.json is the project's own wording.

Zabbix's API documentation is proprietary: reproducing it in whole or in part needs Zabbix's written consent. The
overlay's summaries become the SDK's XML documentation and the modules' help, so none of them may follow the
documentation's sentences. This tool compares every `summary` in the overlay against the documentation of the API
reference (the object pages, the `get` method pages and the reference commentary) and fails on:

  - an exact match: a sentence or clause of six words or more that equals a documentation sentence or clause, once
    case, punctuation and spacing are set aside;
  - a near match: a sentence or clause of six words or more whose word sequence has a difflib ratio of 0.85 or more
    against a documentation sentence or clause of the same property or parameter (or, for a type, object or method
    summary, against the prose of the page it describes).

Allowed: units of five words or fewer ("ID of the host."), which state a fact in the only few ways it can be put, and
the units tools/doc-wording-allow.txt lists (one normalised unit per line, each with the reason after a `#`).

The documentation's text never enters the repository. `--fetch` downloads the pages and keeps only the parsed
sentences in a cache outside it: $ZABBIX_DOC_CACHE, default ${XDG_CACHE_HOME:-~/.cache}/zabbix-doc-wording.

  tools/check-doc-wording.py --fetch     fetch the documentation into the cache, then check
  tools/check-doc-wording.py             check against the cache

Standard library only.
"""

from __future__ import annotations

import argparse
import concurrent.futures
import difflib
import html.parser
import json
import os
import re
import sys
import time
import urllib.error
import urllib.request
from pathlib import Path

ROOT = Path(__file__).resolve().parent.parent
OVERLAY = ROOT / "schema" / "overlay.json"
ALLOW = ROOT / "tools" / "doc-wording-allow.txt"
BASE = "https://www.zabbix.com/documentation/{version}/en/manual/api/"
VERSIONS = ("7.0", "7.4", "8.0")
MIN_WORDS = 6
NEAR = 0.85
# Documentation paragraphs that hold value lists, behaviour flags or cross references rather than prose.
NOT_PROSE = re.compile(r"^(possible (bitmap )?values|property behavior|parameter behavior|supported|accepts|default)\b", re.I)


def cache_dir() -> Path:
    configured = os.environ.get("ZABBIX_DOC_CACHE")
    if configured:
        return Path(configured)
    base = os.environ.get("XDG_CACHE_HOME") or str(Path.home() / ".cache")
    return Path(base) / "zabbix-doc-wording"


def norm(text: str) -> str:
    text = text.lower().replace("’", "'")
    text = re.sub(r"[^a-z0-9 ]+", " ", text)
    return re.sub(r"\s+", " ", text).strip()


def units(text: str) -> list[str]:
    """The sentences of a text and the clauses they are built from, normalised, de-duplicated."""
    found: list[str] = []
    for sentence in re.split(r"(?<=[.!?])\s+", text):
        for piece in [sentence, *re.split(r"\s*(?:[;:(),]|\s-\s|—|–)\s*", sentence)]:
            unit = norm(piece)
            if unit and unit not in found:
                found.append(unit)
    return found


class _Page(html.parser.HTMLParser):
    """Collects the main content of a documentation page: its property and parameter tables, and its paragraphs."""

    def __init__(self) -> None:
        super().__init__(convert_charrefs=True)
        self.started = False
        self.rows: list[tuple[str, str]] = []
        self.prose: list[str] = []
        self._table_depth = 0
        self._header: list[str] = []
        self._in_th = False
        self._keep_table = False
        self._cells: list[str] | None = None
        self._cell: list[str] | None = None
        self._para: list[str] | None = None

    def handle_starttag(self, tag, attrs):
        if tag == "h1":
            self.started = True
        if not self.started:
            return
        if tag == "table":
            self._table_depth += 1
            self._header, self._keep_table = [], False
        elif tag == "th":
            self._in_th = True
            self._header.append("")
        elif tag == "tr" and self._table_depth:
            self._cells = []
        elif tag == "td" and self._cells is not None:
            self._cell = []
        elif tag == "br" and self._cell is not None:
            self._cell.append("\n")
        elif tag == "p" and not self._table_depth:
            self._para = []

    def handle_endtag(self, tag):
        if not self.started:
            return
        if tag == "table":
            self._table_depth = max(0, self._table_depth - 1)
        elif tag == "th":
            self._in_th = False
            self._keep_table = bool(self._header) and self._header[0].strip() in ("Property", "Parameter")
        elif tag == "td" and self._cell is not None and self._cells is not None:
            self._cells.append("".join(self._cell))
            self._cell = None
        elif tag == "tr" and self._cells is not None:
            if self._keep_table and len(self._cells) >= 2:
                self.rows.append((self._cells[0].strip(), self._cells[-1]))
            self._cells = None
        elif tag == "p" and self._para is not None:
            self.prose.append(re.sub(r"\s+", " ", "".join(self._para)).strip())
            self._para = None

    def handle_data(self, data):
        if self._in_th and self._header:
            self._header[-1] += data
        if self._cell is not None:
            self._cell.append(data)
        if self._para is not None:
            self._para.append(data)


def parse_page(markup: str) -> dict:
    page = _Page()
    page.feed(markup)
    properties: dict[str, list[str]] = {}
    for name, description in page.rows:
        paragraphs = [re.sub(r"\s+", " ", p).strip() for p in re.split(r"\n\s*\n", description)]
        prose = [p for p in paragraphs if p and not NOT_PROSE.match(p)]
        properties.setdefault(name, []).extend(u for p in prose for u in units(p))
    prose = [u for p in page.prose if p for u in units(p)]
    return {"properties": properties, "prose": prose}


def _get(url: str) -> str | None:
    request = urllib.request.Request(url, headers={"User-Agent": "zabbix-doc-wording-check"})
    for attempt in range(4):
        try:
            with urllib.request.urlopen(request, timeout=60) as response:
                return response.read().decode("utf-8", "replace")
        except urllib.error.HTTPError as error:
            if error.code == 404:
                return None
            time.sleep(2 * (attempt + 1))
        except (urllib.error.URLError, TimeoutError):
            time.sleep(2 * (attempt + 1))
    raise SystemExit(f"check-doc-wording: could not fetch {url}")


def fetch(versions: list[str]) -> None:
    for version in versions:
        base = BASE.format(version=version)
        index = _get(base + "reference")
        if index is None:
            raise SystemExit(f"check-doc-wording: no API reference for Zabbix {version}")
        pages = sorted(set(re.findall(r"manual/api/reference/([a-z_]+)/(object|get)\b", index)))
        targets = [(f"{name}.{kind}", f"{base}reference/{name}/{kind}") for name, kind in pages]
        targets.append(("reference_commentary", base + "reference_commentary"))
        out = cache_dir() / version
        out.mkdir(parents=True, exist_ok=True)

        def one(target):
            key, url = target
            markup = _get(url)
            if markup is not None:
                (out / f"{key}.json").write_text(json.dumps(parse_page(markup)), encoding="utf-8")
            return key, markup is not None

        with concurrent.futures.ThreadPoolExecutor(max_workers=4) as pool:
            fetched = sum(ok for _, ok in pool.map(one, targets))
        print(f"check-doc-wording: cached {fetched} pages of the Zabbix {version} API reference in {out}", file=sys.stderr)


def load_docs(versions: list[str]):
    by_property: dict[str, set[str]] = {}
    by_page: dict[str, set[str]] = {}
    everything: set[str] = set()
    pages = 0
    for version in versions:
        folder = cache_dir() / version
        for path in sorted(folder.glob("*.json")) if folder.is_dir() else []:
            pages += 1
            page = json.loads(path.read_text(encoding="utf-8"))
            name = path.stem.split(".")[0]
            for prop, found in page["properties"].items():
                by_property.setdefault(prop, set()).update(found)
                by_page.setdefault(name, set()).update(found)
                everything.update(found)
            by_page.setdefault(name, set()).update(page["prose"])
            everything.update(page["prose"])
    if pages == 0:
        raise SystemExit(f"check-doc-wording: no cached documentation in {cache_dir()}; run with --fetch")
    return by_property, by_page, everything


def summaries(overlay: dict):
    """Every summary with its path and, for a field, the wire name it describes."""

    def walk(node, path):
        if isinstance(node, dict):
            for key, value in node.items():
                if key == "summary" and isinstance(value, str):
                    yield path, value
                else:
                    yield from walk(value, path + [key])
        elif isinstance(node, list):
            for index, value in enumerate(node):
                yield from walk(value, path + [str(index)])

    yield from walk(overlay, [])


def page_of(path: list[str]) -> str | None:
    """The documentation page a type, object or method summary describes (types by their name's leading word)."""
    if path[0] in ("objects", "methods") and len(path) > 1:
        return path[1].split(".")[0].lower()
    if path[0] in ("types", "resultRecords") and len(path) > 1:
        lead = re.match(r"[A-Z][a-z]*", path[1])
        return lead.group(0).lower() if lead else None
    return None


def load_allow() -> set[str]:
    if not ALLOW.is_file():
        return set()
    allowed = set()
    for line in ALLOW.read_text(encoding="utf-8").splitlines():
        unit = norm(line.split("#", 1)[0])
        if unit:
            allowed.add(unit)
    return allowed


def check(versions: list[str]) -> list[dict]:
    by_property, by_page, everything = load_docs(versions)
    allowed = load_allow()
    overlay = json.loads(OVERLAY.read_text(encoding="utf-8"))
    findings = []
    for path, text in summaries(overlay):
        if "fields" in path and path.index("fields") + 1 < len(path):
            candidates = by_property.get(path[path.index("fields") + 1], set())
        else:
            candidates = by_page.get(page_of(path) or "", set())
        split_candidates = [(c, c.split()) for c in candidates]
        for unit in units(text):
            words = unit.split()
            if len(words) < MIN_WORDS or unit in allowed:
                continue
            if unit in everything:
                findings.append({"path": "/".join(path), "unit": unit, "match": "exact", "doc": unit})
                continue
            best = (0.0, "")
            for candidate, candidate_words in split_candidates:
                if 2 * min(len(words), len(candidate_words)) / (len(words) + len(candidate_words)) < NEAR:
                    continue
                ratio = difflib.SequenceMatcher(None, words, candidate_words).ratio()
                if ratio > best[0]:
                    best = (ratio, candidate)
            if best[0] >= NEAR:
                findings.append({"path": "/".join(path), "unit": unit, "match": f"near {best[0]:.2f}", "doc": best[1]})
    return findings


def main() -> int:
    parser = argparse.ArgumentParser(description=__doc__.split("\n\n")[0])
    parser.add_argument("--fetch", action="store_true", help="download the documentation into the cache first")
    parser.add_argument("--versions", default=",".join(VERSIONS), help="Zabbix versions to compare against")
    parser.add_argument("--json", action="store_true", help="print the findings as JSON")
    arguments = parser.parse_args()
    versions = [v for v in arguments.versions.split(",") if v]
    if arguments.fetch:
        fetch(versions)
    findings = check(versions)
    if arguments.json:
        print(json.dumps(findings, indent=1))
    else:
        for finding in findings:
            print(f"{finding['path']}: {finding['match']}: \"{finding['unit']}\"")
    if findings:
        print(f"check-doc-wording: {len(findings)} overlay sentence(s) follow the Zabbix documentation's wording;"
              " put them in the project's own words (tools/check-doc-wording.py)", file=sys.stderr)
        return 1
    print("check-doc-wording: every overlay summary is in the project's own words")
    return 0


if __name__ == "__main__":
    sys.exit(main())
