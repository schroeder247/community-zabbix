// This Source Code Form is subject to the terms of the Mozilla Public
// License, v. 2.0. Copyright (c) 2026 schroeder247. SPDX-License-Identifier: MPL-2.0
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.Text;

namespace Community.Zabbix.Generator;

/// <summary>A diagnostic as plain values (a <see cref="Location"/> is not cached across runs), made into one when reported.</summary>
/// <param name="Descriptor">The diagnostic.</param>
/// <param name="Path">The description's path.</param>
/// <param name="Span">The line's span in the description.</param>
/// <param name="Lines">The line's position.</param>
/// <param name="Subject">What it is about (the first message argument).</param>
/// <param name="Message">What is wrong (the second).</param>
internal sealed record LocatedDiagnostic(DiagnosticDescriptor Descriptor, string Path, TextSpan Span, LinePositionSpan Lines, string Subject, string Message)
{
    /// <summary>The diagnostic to report.</summary>
    public Diagnostic ToDiagnostic() => Diagnostic.Create(Descriptor, Location.Create(Path, Span, Lines), Subject, Message);
}
