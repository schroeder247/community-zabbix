// This Source Code Form is subject to the terms of the Mozilla Public
// License, v. 2.0. Copyright (c) 2026 schroeder247. SPDX-License-Identifier: MPL-2.0
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;

namespace Community.Zabbix.PowerShell.HelpGen;

/// <summary>
/// One <c>[Parameter(...)]</c> attribute on a property: the set it declares the parameter in (<see cref="string.Empty"/>
/// for every set), and how the parameter binds there.
/// </summary>
internal sealed record ParameterUse(string Set, bool Mandatory, int? Position, bool ValueFromPipeline, bool ValueFromPipelineByPropertyName);

/// <summary>One documented cmdlet parameter, as read from its <c>[Parameter]</c> attributes and doc comment.</summary>
internal sealed class ParameterDoc
{
    /// <summary>The property name (the parameter name PowerShell binds by).</summary>
    public required string Name { get; init; }

    /// <summary>The property's declared type, as written in source (e.g. <c>string[]?</c>, <c>SecureString?</c>).</summary>
    public required string TypeText { get; init; }

    /// <summary>The stripped-to-plain-text doc comment on the property.</summary>
    public required string Description { get; init; }

    /// <summary>True if any <c>[Parameter]</c> occurrence on this property carries <c>Mandatory = true</c>.</summary>
    public bool Mandatory { get; init; }

    /// <summary>The <c>ParameterSetName</c> text on this property's <c>[Parameter]</c> attribute, or <see cref="string.Empty"/>
    /// when the attribute gives none (meaning: valid in every parameter set, never mutually exclusive with another parameter).</summary>
    public string ParameterSet { get; init; } = string.Empty;

    /// <summary>Aliases declared on the property via <c>[Alias(...)]</c>.</summary>
    public IReadOnlyList<string> Aliases { get; init; } = [];

    /// <summary>True for a <c>SwitchParameter</c>-typed property (no value in example code, just the flag).</summary>
    public bool IsSwitch => TypeText.Contains("SwitchParameter", StringComparison.Ordinal);

    /// <summary>Every <c>[Parameter(...)]</c> attribute on the property, in source order (one per parameter set it is in).</summary>
    public IReadOnlyList<ParameterUse> Uses { get; init; } = [];

    /// <summary>True when the property carries <c>[SupportsWildcards]</c>.</summary>
    public bool Wildcards { get; init; }

    /// <summary>The value the parameter has when it is not given, as its initializer says it; <see langword="null"/> when none says.</summary>
    public string? DefaultValue { get; init; }

    /// <summary>The type as a reader should see it: no <c>global::</c>, no namespaces, no nullable marker.</summary>
    public string DisplayType => DisplayTypeOf(TypeText);

    /// <summary>A C# type as written in source, as a reader should see it.</summary>
    /// <param name="typeText">The type as written.</param>
    public static string DisplayTypeOf(string typeText)
        => DisplayTypeRewriter.Instance.Visit(SyntaxFactory.ParseTypeName(typeText)).ToString();

    // Every qualified name becomes its rightmost part (global::System.Security.SecureString → SecureString, inside
    // generic arguments too) and every nullable type its underlying one (string[]? → string[]).
    private sealed class DisplayTypeRewriter : CSharpSyntaxRewriter
    {
        public static readonly DisplayTypeRewriter Instance = new();

        public override SyntaxNode? VisitQualifiedName(QualifiedNameSyntax node) => Visit(node.Right)!.WithTriviaFrom(node);

        public override SyntaxNode? VisitAliasQualifiedName(AliasQualifiedNameSyntax node) => Visit(node.Name)!.WithTriviaFrom(node);

        public override SyntaxNode? VisitNullableType(NullableTypeSyntax node) => Visit(node.ElementType)!.WithTriviaFrom(node);
    }
}

/// <summary>One <c>Get-Help</c> example: a hand-written one from the module's <c>Examples/&lt;Verb-Noun&gt;.ps1</c>, or one
/// the generator wrote into a generated cmdlet's <c>&lt;example&gt;</c> doc comment.</summary>
/// <param name="Title">What the example does, as a short imperative phrase.</param>
/// <param name="Code">The example's code, exactly as a user would type it.</param>
/// <param name="Remarks">What the example does and what it shows, in sentences.</param>
internal sealed record ExampleDoc(string Title, string Code, string Remarks);

/// <summary>
/// A cmdlet's help script, <c>Examples/&lt;Verb-Noun&gt;.ps1</c>: an optional comment-based help block whose
/// <c>.SYNOPSIS</c> and <c>.DESCRIPTION</c> replace the class's own paragraphs, and the <c>#region</c> examples.
/// </summary>
/// <param name="Synopsis">The <c>.SYNOPSIS</c> text, or <see langword="null"/> when the script gives none.</param>
/// <param name="Description">The <c>.DESCRIPTION</c> text, or <see langword="null"/> when the script gives none.</param>
/// <param name="Examples">The examples, in file order.</param>
internal sealed record HelpScript(string? Synopsis, string? Description, IReadOnlyList<ExampleDoc> Examples);

/// <summary>One documented cmdlet, as read from its <c>[Cmdlet]</c> attribute and class-level doc comment.</summary>
internal sealed record CmdletDoc
{
    /// <summary>The PowerShell verb (e.g. <c>Get</c>).</summary>
    public required string Verb { get; init; }

    /// <summary>The PowerShell noun (e.g. <c>ZbxHost</c>).</summary>
    public required string Noun { get; init; }

    /// <summary>The full cmdlet name, <c>Verb-Noun</c>.</summary>
    public string Name => $"{Verb}-{Noun}";

    /// <summary>The synopsis text: the short one-line "what this does", from <c>&lt;para type="synopsis"&gt;</c>.</summary>
    public required string Synopsis { get; init; }

    /// <summary>The full description text, from <c>&lt;para type="description"&gt;</c>; falls back to <see cref="Synopsis"/> when the class has none.</summary>
    public required string Description { get; init; }

    /// <summary>Every <c>[Parameter]</c>-decorated property found in the class body, in source order.</summary>
    public required IReadOnlyList<ParameterDoc> Parameters { get; init; }

    /// <summary>The base class's short name (e.g. <c>ZabbixGetCmdlet</c>), stripped of generic arguments and
    /// namespace qualification — which base <c>[Parameter]</c>-decorated properties (<c>-Connection</c>, the CRUD
    /// bases' <c>-Id</c>/<c>-Filter</c>/<c>-Limit</c>/<c>-Force</c>) this cmdlet inherits and so must also document.</summary>
    public required string BaseType { get; init; }

    /// <summary>The <c>DefaultParameterSetName</c> of the <c>[Cmdlet]</c> attribute, or <see cref="string.Empty"/>.</summary>
    public string DefaultParameterSet { get; init; } = string.Empty;

    /// <summary>Whether the <c>[Cmdlet]</c> attribute says <c>SupportsShouldProcess = true</c> (so <c>-WhatIf</c>/<c>-Confirm</c> apply).</summary>
    public bool SupportsShouldProcess { get; init; }

    /// <summary>Whether the <c>[Cmdlet]</c> attribute says <c>SupportsPaging = true</c> (so <c>-First</c>/<c>-Skip</c>/<c>-IncludeTotalCount</c> apply).</summary>
    public bool SupportsPaging { get; init; }

    /// <summary>Every type named by the class's <c>[OutputType(typeof(...))]</c> attributes, as a reader should see it.</summary>
    public IReadOnlyList<string> OutputTypes { get; init; } = [];

    /// <summary>The command's own aliases, from the class's <c>[Alias(...)]</c> attributes (the manifest's <c>AliasesToExport</c>).</summary>
    public IReadOnlyList<string> Aliases { get; init; } = [];

    /// <summary>The examples: the class's <c>&lt;example&gt;</c> doc comments, or the help script's when it has one
    /// (<see cref="With(HelpScript)"/>).</summary>
    public IReadOnlyList<ExampleDoc> Examples { get; init; } = [];

    /// <summary>Returns a copy with <paramref name="parameters"/> appended to <see cref="Parameters"/>.</summary>
    public CmdletDoc WithAdditionalParameters(IEnumerable<ParameterDoc> parameters) => this with { Parameters = [.. Parameters, .. parameters] };

    /// <summary>
    /// Returns the help <paramref name="script"/> gives this command: its <c>.SYNOPSIS</c> and <c>.DESCRIPTION</c>
    /// replace the class's, and its examples replace the class's <c>&lt;example&gt;</c> ones when it has any.
    /// </summary>
    /// <param name="script">The command's <c>Examples/&lt;Verb-Noun&gt;.ps1</c>.</param>
    public CmdletDoc With(HelpScript script) => this with
    {
        Synopsis = script.Synopsis ?? Synopsis,
        Description = script.Description ?? Description,
        Examples = script.Examples.Count > 0 ? script.Examples : Examples,
    };

    /// <summary>
    /// The parameter sets, the default first: every set a parameter names, or one unnamed set when none does. A
    /// parameter with no set (<see cref="ParameterUse.Set"/> empty) is in every set, as PowerShell binds it.
    /// </summary>
    public IReadOnlyList<string> ParameterSets()
    {
        List<string> sets = [.. Parameters.SelectMany(p => p.Uses).Select(u => u.Set).Where(s => s.Length > 0).Distinct(StringComparer.Ordinal)];
        if (sets.Count == 0) return [string.Empty];
        // The default set exists even when no parameter names it (Connect-ZbxServer's Anonymous: only the parameters
        // that are in every set), as PowerShell builds it.
        if (DefaultParameterSet.Length > 0)
        {
            sets.Remove(DefaultParameterSet);
            sets.Insert(0, DefaultParameterSet);
        }
        return sets;
    }

    /// <summary>The parameters of <paramref name="set"/>, each with the attribute that puts it there.</summary>
    /// <param name="set">A set from <see cref="ParameterSets"/>.</param>
    public IEnumerable<(ParameterDoc Parameter, ParameterUse Use)> In(string set)
    {
        foreach (ParameterDoc parameter in Parameters)
        {
            ParameterUse? use = parameter.Uses.FirstOrDefault(u => string.Equals(u.Set, set, StringComparison.Ordinal))
                ?? parameter.Uses.FirstOrDefault(u => u.Set.Length == 0);
            if (use is not null) yield return (parameter, use);
        }
    }
}
