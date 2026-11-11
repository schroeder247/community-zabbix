// This Source Code Form is subject to the terms of the Mozilla Public
// License, v. 2.0. Copyright (c) 2026 schroeder247. SPDX-License-Identifier: MPL-2.0
using Microsoft.CodeAnalysis;

namespace Community.Zabbix.Generator;

/// <summary>
/// Represents something in the description the generator will not turn into code by guessing: reported as its
/// descriptor's diagnostic at the description's line, and the entry or member it concerns is left out.
/// </summary>
/// <param name="Descriptor">The diagnostic (<see cref="ZabbixDiagnostics"/>).</param>
/// <param name="Message">What is wrong.</param>
/// <param name="Record">The record, enum or API object it is in, as the description names it.</param>
/// <param name="Wire">The field's wire name, or the method, when it concerns one.</param>
/// <param name="Line">The zero-based line, when the reader knows it (a JSON syntax error); else it is found by name.</param>
internal sealed record EmitProblem(DiagnosticDescriptor Descriptor, string Message, string Record, string? Wire, long? Line = null)
{
    /// <summary>The diagnostic id.</summary>
    public string Id => Descriptor.Id;

    /// <summary>What the problem is about: the record, or the record and its field or method.</summary>
    public string Subject => Wire is null ? Record : Record + "." + Wire;
}
