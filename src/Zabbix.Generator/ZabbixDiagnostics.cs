// This Source Code Form is subject to the terms of the Mozilla Public
// License, v. 2.0. Copyright (c) 2026 schroeder247. SPDX-License-Identifier: MPL-2.0
using Microsoft.CodeAnalysis;

namespace Community.Zabbix.Generator;

/// <summary>
/// Provides every diagnostic the two generators report, each with its description and its help page
/// (<c>docs/generator-diagnostics.md</c>). An <see cref="EmitProblem"/> carries its descriptor, so no id is looked up.
/// </summary>
internal static class ZabbixDiagnostics
{
    private const string Category = "Zabbix.Generator";

    private const string Help = "https://github.com/schroeder247/community-zabbix/blob/main/docs/generator-diagnostics.md#";

    /// <summary>ZBX0001: the description is not JSON, or lacks a section every description has.</summary>
    public static readonly DiagnosticDescriptor Unreadable = Create(
        "ZBX0001", "The API description could not be read", "'{0}' is not a readable API description: {1}", DiagnosticSeverity.Error,
        "The AdditionalFiles item zabbix-api.json is not JSON, or lacks its versions, enums, types or services section. Nothing is generated from it. Regenerate it with tools/regenerate-schema.sh.");

    /// <summary>ZBX0002: a field, parameter or result names a kind the generator does not know.</summary>
    public static readonly DiagnosticDescriptor UnknownKind = Create(
        "ZBX0002", "A kind the generator does not know", "{0} has a kind the generator does not know: {1}", DiagnosticSeverity.Error,
        "A field's, parameter's or result's kind is none of the kinds the generator maps to a C# type. The member is left out rather than typed as untyped JSON.");

    /// <summary>ZBX0003: a kind names a record or enum the description does not declare.</summary>
    public static readonly DiagnosticDescriptor Undeclared = Create(
        "ZBX0003", "A kind names a record or enum the description does not declare", "{0} names a type the description does not declare: {1}", DiagnosticSeverity.Error,
        "An object:, list:object: or enum: kind names a record or enum that is not in the description. The member is left out.");

    /// <summary>ZBX0004: two fields of a record would get one C# name.</summary>
    public static readonly DiagnosticDescriptor FieldCollision = Create(
        "ZBX0004", "Two fields of a record would get one C# name", "{0} is left out because its C# name is taken: {1}", DiagnosticSeverity.Error,
        "Two fields of a record, or a field and the record itself, would be one C# member. The later field is left out; the message names its entry in schema/overlay.json, where it takes a name of its own.");

    /// <summary>ZBX0005: two methods of a client would get one C# name.</summary>
    public static readonly DiagnosticDescriptor MethodCollision = Create(
        "ZBX0005", "Two methods of a client would get one C# name", "{0} is left out because its C# name is taken: {1}", DiagnosticSeverity.Error,
        "Two methods of an API object would be one C# method of its client. The later method is left out; the message names its entry in schema/overlay.json, where it takes a name of its own.");

    /// <summary>ZBX0006: a cmdlet, parameter or builder the cmdlet generator could not emit.</summary>
    public static readonly DiagnosticDescriptor NotGenerated = Create(
        "ZBX0006", "A cmdlet, parameter or nested-record builder the cmdlet generator could not emit", "{0} has no generated cmdlet, parameter or builder: {1}", DiagnosticSeverity.Warning,
        "The cmdlet generator left out a cmdlet, a parameter or a nested-record builder whose shape it cannot express; the SDK type is still generated.");

    /// <summary>ZBX0008: an entry of the description is malformed or declared twice.</summary>
    public static readonly DiagnosticDescriptor Malformed = Create(
        "ZBX0008", "An entry of the description is malformed or declared twice", "{0} is malformed or declared twice: {1}", DiagnosticSeverity.Error,
        "An enum, record, field, API object or method lacks a property it needs, holds a value of the wrong shape (a version that is not major.minor, a name that is not a C# identifier), or repeats a name another entry already has. The entry or the value is left out. An int field whose summary names two or more defines is a closed set the extractor did not type, reported here and kept as an int.");

    private static DiagnosticDescriptor Create(string id, string title, string format, DiagnosticSeverity severity, string description)
        => new(id, title, format, Category, severity, isEnabledByDefault: true, description, Help + "zbx" + id.Substring(3));
}
