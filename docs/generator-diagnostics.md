<!--
This Source Code Form is subject to the terms of the Mozilla Public
License, v. 2.0. Copyright (c) 2026 schroeder247. SPDX-License-Identifier: MPL-2.0
-->
# Generator diagnostics

`Zabbix.Generator` turns `schema/zabbix-api.json` into the SDK's records, enums and clients and the PowerShell module's
cmdlets. It never guesses: what it cannot turn into code is a diagnostic at the description's line, and the entry or
member is left out. Each message is a sentence that starts with the entry it is about. Every diagnostic below links here (`helpLinkUri`); the release table is
`src/Zabbix.Generator/AnalyzerReleases.Unshipped.md`.

## ZBX0001

**The API description could not be read** (error). The file is not JSON, or lacks its `versions`, `enums`, `types` or
`services` array. Nothing is generated from it; a JSON syntax error points at its line. Regenerate the description with
`tools/regenerate-schema.sh`.

## ZBX0002

**A kind the generator does not know** (error). A field's, parameter's or result's `kind` is none of `string`, `id`,
`clock`, `date`, `timeofday`, `int`, `long`, `ulong`, `double`, `bool`, `apibool`, `json`, `output`, `filter`, `enum:X`,
`object:X`, `list:K` or `map:K`. The member is left out rather than typed as untyped JSON.

## ZBX0003

**A kind names a record or enum the description does not declare** (error). An `object:X`, `list:object:X` or `enum:X`
names a type that is not in the description. The member is left out.

## ZBX0004

**Two fields of a record would get one C# name** (error). Two fields, or a field and the record itself, would be one
C# member. The later field is left out. The message names the field's entry in `schema/overlay.json` as a JSON pointer
(`schema/overlay.json#/types/Host/fields/host/name`): the description is generated from the overlay, so a name given
there survives the next regeneration, and one written into `zabbix-api.json` does not. The location is the field's line
in the description.

## ZBX0005

**Two methods of a client would get one C# name** (error). Two methods of an API object would be one method of its
client. The later method is left out. The message names the method's entry in `schema/overlay.json`
(`schema/overlay.json#/methods/host.get/name`), where it takes a name of its own.

## ZBX0006

**A cmdlet, parameter or nested-record builder the cmdlet generator could not emit** (warning). The PowerShell
generator left out a shape it cannot express (a non-string field whose name reads as a secret, a builder whose record
is not described); the SDK type is still generated. The objects the description's `cmdlets` section leaves out on
purpose (`skip`) raise no diagnostic, so under `-warnaserror` a new omission fails the build.

## ZBX0008

**An entry of the description is malformed or declared twice** (error). An enum, record, field, API object or method
lacks a property it needs, holds a value of the wrong shape (a version that is not `major.minor`, a name that is not a C#
identifier, an enum value that is not an integer), or repeats a name another entry already has (records and enums share
one namespace and one file per name). The optional `references` and `cmdlets` sections are held to the same rules: a
builder's concept, a switch's members, a get switch's parameter and a value form's attributes are C# identifiers, and a
builder's concept is declared once. The entry, or the value, is left out; everything else is generated. An `int` field
whose summary names two or more defines (`<c>ZBX_EVENT_NORMAL</c>`, `<c>ZBX_EVENT_COPIED</c>`) is a closed set of values
the extractor did not type: it is reported here, kept as an `int`, and closed by naming its family in the overlay's
`fieldKinds` or `fieldEnums`.

ZBX0007 is not used: both generators report an unreadable description as ZBX0001.
