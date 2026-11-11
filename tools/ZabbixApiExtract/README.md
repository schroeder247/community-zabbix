<!--
This Source Code Form is subject to the terms of the Mozilla Public
License, v. 2.0. Copyright (c) 2026 schroeder247. SPDX-License-Identifier: MPL-2.0
-->
# ZabbixApiExtract

Host-side dev tooling. It reads the Zabbix frontend's PHP source for every supported version and writes the API
description the source generator (`src/Zabbix.Generator`) builds the SDK's records, enums and clients from. Zabbix
publishes no machine-readable schema (ZBXNEXT-9301 asks for one); its PHP is the authority.

```
tools/regenerate-schema.sh [--check]                       # from the repository root: fetch, extract, merge, verify
python3 -m zabbix_api_extract [--sources <dir>] [--facts <dir>] [--schema ../../schema]
python3 -m zabbix_api_extract verify [--sources <dir>] [--all] [--codes FIELD-MISSING,KIND]
```

**The sources are pinned to Zabbix's repository.** `tools/zabbix-sources.lock` names, per supported version, the
upstream ref and the commit the model is extracted from, on `https://github.com/zabbix/zabbix` (the official mirror of
git.zabbix.com):

| Version | Ref | Commit |
| --- | --- | --- |
| 7.0 | tag `7.0.31` | `e7da53eea214` |
| 7.4 | tag `7.4.15` | `bd350f540cdb` |
| 8.0 | tag `8.0.0rc1` (no `8.0.0` tag on the mirror yet) | `dd0d9c26a3ad` |

`tools/fetch-zabbix-sources.sh` makes a shallow, blob-filtered, sparse clone of each pinned commit into
`<sources>/<version>` (default `.zabbix-sources/` at the repository root, ignored by git; `$ZABBIX_SOURCES` overrides
it), checking out only what this tool reads and the database schema:

| Path | Read by |
| --- | --- |
| `ui/include/*.php` | the interpreter and `verify` (every top-level include): `defines.inc.php` (constants), `schema.inc.php` (field lengths, the tables a request is stored in), the helpers the services call |
| `ui/include/classes/` | the interpreter and `verify` (every class, recursively): `api/services/*` (the API), `api/CApiServiceFactory.php` (the object list), `validators/CApiInputValidator.php`, `api/CAudit.php` (masked paths) |
| `create/src/schema.tmpl` | nothing yet: the database schema (tables, primary and foreign keys), kept for the reserved `Zabbix.Data` package ([ADR 0006](../../docs/adr/0006-protocol-split.md)) |

A checkout whose commit differs from the lock, or a tag that no longer names the pinned commit, fails the fetch. Run
the module from this directory; `--sources` defaults to `$ZABBIX_SOURCES`, else `.zabbix-sources/`. It writes:

| File | What it is |
| --- | --- |
| `<facts>/zabbix-api-<version>.json` | The facts of one version, and nothing else: every object and method, the rules each method validates its input with (`$api_input_rules` and the older validators), the defaults the older `.get`s accept, `ACCESS_RULES` (`min_user_type`), `requiresAuthentication()`, whether the method writes (anything reachable that calls `DB::insert`/`update`/`delete`, the audit log, or the server), what a `.get` returns and what each `select*` adds, with the file and line each fact came from. An intermediate: `--facts` defaults to the sources folder, and nothing reads it but a reviewer. |
| `schema/zabbix-api.json` | The one description the generator reads: every version merged, with `since`/`removed` on objects, methods, fields and enum members, the C# names, and the capability of every method. Checked in. |

Regenerating from the pinned sources reproduces the committed `schema/zabbix-api.json` byte for byte; the
`schema-drift` workflow (`.github/workflows/schema-drift.yml`, weekly and on demand) runs `regenerate-schema.sh --check`
and fails on any difference. Moving a pin is a model change: update the lock, regenerate, and commit the lock and the
reviewed model diff together.

`schema/overlay.json` is **reviewed input**: C# names that stay stable across regenerations, documentation sentences
keyed by wire name, enum names for define families, and the handful of facts the PHP computes at run time (each entry
carries the source that states it). It supplies names and sentences,
never structure. The tool reports on stderr everything it could not decide.

The overlay's sections that shape the model (each with a `_source` naming the Zabbix file and line it rests on):
`fieldEnums` (a field's define family, a list of defines, `none` for a number), `nested` (a nested record's name;
the paths given one name are one record, its required flags unioned), `selects` (what a `select*` returns: an API object, a table, or a reviewed field list), `extraFields` (fields a method
takes that its rules do not name: another method's element `list:obj.method`, another method's field
`field:obj.method/wire`, a table's rows `list:table:t`, named fields `list:fields:a=id!,b=string` with `!` for
required; any other kind is a generator kind as written, `attributes=map:string`), `nestedTables`, `readFields`,
`fieldKinds` (a reviewed kind for one record field: a generator kind, `enum:<family>`, a table or field list),
`nullMeaningful` (an option whose `null` is its own request: emitted as `ZabbixOptional<T>`), `internalColumns`,
`keepEnums`, `objectTables`, and `notFields` (a field a read, keyed by the object, or a request or get, keyed by
`object.method`, does not have although a table or a shared validation names it: `hostinterface.create`'s
`interface_ref`, which only the configuration import uses; a dotted path names a nested field). Under `enums`,
`memberSummaries` gives a member its own sentence, keyed like `members` by what follows the family's prefix.

Three more sections are carried into the description as written (less their `_source` and `_comment` notes) and checked
against the merged model, so a stale entry fails the run like any other merge problem:

- `objects.<object>.idField` and `objects.<object>.requiredWhen` (Zabbix facts, on the object's `services` entry): the
  read model's own id where the delete names none (a host dashboard is the dashboard its `dashboardid` names), and a
  field required only under a condition a run-time rule class decides (`CItemTypeFactory`'s per-type classes: an item's
  `master_itemid` is required when its type is a dependent item), with the condition. The id must be a field of the read
  model, the condition's field one of the create request.
- `references` (a Zabbix fact): the object an id field refers to, by its wire name (`ids`: `hostid` is a host's), and
  where an owner's requests and get name another (`owners`: a template's `groupid` is a template group's). Every object
  named must be an API object.
- `cmdlets` (the PowerShell module's reviewed shape, read only by the cmdlet generator): per object, whether it is skipped
  (a hand-written command or a singleton covers it), whether its `Get-` is hand-written, the list field the
  single-interface shorthand fills, the id field `-Id` selects many records by, enum fields taken as switches, the
  value-form attributes a parameter carries, the visible and technical name fields of an object that has both
  (`names`: a host's `name` is `-DisplayName` and `-Name` is an alias of its `host`), and the property a field takes
  where its own name would hide a `PSCmdlet` member (`unhide`: a host's `host` is `TechnicalName`; a field it does not
  list takes its name followed by `Value`); the nested-record builders (`New-Zbx<Concept>`: the record, by its
  description name, the fields taken by position, the help's words, their value forms and the fields the owner's command
  fills); and the get options every `Get-` takes as a switch (`editable`: `-Editable`). Objects, fields and records must
  exist in the model.

A field's `name` in the description is the extractor's (`pascal`: each part capitalised, a trailing `id`/`ids` split
off), or the overlay's. The generator names a field whose `name` is exactly that mechanical capitalisation by its words
instead (`src/Zabbix.Generator/ZabbixNames.cs`, vocabulary in `schema/words.json`: `sortorder` → `SortOrder`,
`opmessage_grp` → `OperationMessageGroup`); an overlay name is never touched, and the description is not rewritten.
Record and enum names are named by the same words, part by capitalised part, a word the part before it ends in said once
(`ActionOperationOpgroupRequest` → `ActionOperationGroupRequest`, `MapSelement` → `MapElement`); an overlay name is made
of known words already and stays as it is, and every kind, model and `cref` that names a renamed type follows it.

An overlay `types` entry (or one of its `fields`) that no record reads is **stale**: the run prints `STALE overlay
entry` for it and exits non-zero, like `verify` on a recorded exception that no longer occurs. Remove it; it documents
a record or field the description no longer has.

## verify: the regression gate

`verify` is a second, independent reading of every version's source (its own walk and constant evaluator; it shares
only the tokenizer and parser) compared mechanically with `schema/zabbix-api.json`: objects, methods, `min_user_type`,
authentication, every request field and nested field, required flags, `in` lists and enums, lengths, deprecations,
kinds, filter/search/sort whitelists, output fields, relation shapes and the tables a request is stored in. Every
difference is one line (`<version> <CODE> <api> <path>: … [file:line]`). The run fails on any line not recorded in
`schema/verify-exceptions.json` with the source reason it is not a defect (globs allowed), and on a recorded exception
that no longer occurs. Run it after every regeneration; a new line is either a defect to fix here or a reason to record.

## Facts about secrets, defaults and nulls

- **Secret.** A field the audit log masks (`CAudit::MASKED_PATHS`, read from each version) or named as a password,
  passphrase, PSK (and its identity), client secret, private key or token carries `secret`; the generator puts
  `[ZabbixSecret]` on it and keeps it out of the record's text form (`***`). A macro's value is masked only for a secret
  macro and is not marked.
- **A create's status default.** The sentence of a create's `status` says what the server stores when it is left out
  (the table's default: a media type and a module are created disabled).
- **Paths.** The interpreter records the arms of every undecided branch (and the code past an arm that left early) on
  each validation; the validations of one execution path compose as all running, the paths as alternatives (a value
  either path takes; required only when every path requires). A flag set on some paths only
  (`$api_required = 0; if (…) { $api_required = API_REQUIRED; }`) is not required.

## How it reads PHP

`php.py` tokenizes and parses the subset of PHP the services are written in; `interp.py` is an abstract interpreter
over it. It follows an API method from its entry point with the request as an `Unknown` that remembers where in the
request it came from, evaluates the constants (`defines.inc.php`, class constants with `parent` walking,
`DB::getFieldLength` from `schema.inc.php`), takes both arms of a branch it cannot decide, and records every
`CApiInputValidator::validate($rules, $data)` whose data came from the request. `facts.py` composes those records into
each method's input; `merge.py` merges the versions and applies the overlay.

## Facts beyond the validator rules

- **What a read record carries.** The fields the get's output rule or `OUTPUT_FIELDS` names in any version, the fields
  it computes (`outputIsRequested`), and, where `output=extend` answers every column of the table, the columns the
  object's own create or update takes; less what the get takes out of every object (`unset($template['hostid'],
  $template['discover'])` in the get or a helper it calls, on every path but a condition on the reply's shape, and an
  `unsetExtraFields` with no output to keep for) and, for a get that reads a table it shares with another object
  (`hosts`: hosts, templates, host prototypes), the columns only that other object writes. A secret column the output
  does not name is write-only (a host's `tls_psk`, `CHost.php` 8.0:468).
- **The service's own class.** A shared method's `$this instanceof CTriggerPrototype` and `get_class($this)` are
  decided for the service it runs for, and `APP::getMode()` is the API's mode, so what only a frontend call adds is not
  the API's (`CProxy::get`'s write-only `tls_psk`).
- **Options no call reaches a service with.** What `CLocalApiClient` takes out of every external request
  (`unset($params['nopermissions'])`, `CLocalApiClient.php` 8.0:94) is no `*GetParameters` member.

- **A removed field the server refuses.** A field gone from a later version carries `removed`; it also carries
  `refused` when that version's validator lists no such field *and* has no `API_ALLOW_UNEXPECTED` (so the server answers
  `unexpected parameter`). Only `refused` fields are gated by the client before sending (`ZabbixApiMethods.EnsureFieldSupported`):
  under `API_ALLOW_UNEXPECTED` absence proves nothing (8.0 `CDRule::create` validates its fields in a later pass the
  interpreter does not follow, and the old `.get`s ignore unknown options).
- **Its replacement.** The deprecated rule's own `'replacement' => '…'` key (`CHostGroup.php` 7.0:74), or the field the
  service converts it into before validating further (`$maintenance['hosts'] = zbx_toObject($maintenance['hostids'],
  'hostid')`, `CMaintenance.php` 7.0:329).
- **One or many.** A `select*` whose key is filled with `array_values(…)` is a list whatever its name says
  (`selectChildren`); one read by the service's own `$this->doGet` returns the service's own objects.
- **Composed enums.** `overlay.json` `fieldEnums` may name defines of more than one family (`service.status` is
  `ZBX_SEVERITY_OK` or a `TRIGGER_SEVERITY_*`); the members are named by what follows the last word the defines share.

## Why Python and not a .NET console tool

The repository prefers .NET for its tooling when the effort is similar. It is not similar here: the work is an exploratory evaluator of a
foreign language's source, iterated against three source trees many times an hour, and it existed as a working Python
prototype. The same program in C# would be a larger evaluator (a recursive-descent parser and interpreter over a
dynamically typed language, where Python's own dynamic values carry PHP's) and, inside this repository, subject to the
library analyzer set (`latest-all` at error) that host tooling does not need. Python 3 ships on every developer host;
the tool has no dependencies beyond the standard library and never runs in a build. Its merged output is checked in and
reviewed, and the tests hold the SDK to it.
