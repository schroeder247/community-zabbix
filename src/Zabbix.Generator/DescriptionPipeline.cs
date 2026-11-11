// This Source Code Form is subject to the terms of the Mozilla Public
// License, v. 2.0. Copyright (c) 2026 schroeder247. SPDX-License-Identifier: MPL-2.0
using System.Text;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.Text;

namespace Community.Zabbix.Generator;

/// <summary>
/// The steps both generators share: <see cref="Parse"/> (tracked as <see cref="ParseStep"/>) reads the description into
/// an equatable model, <see cref="Emit"/> (<see cref="EmitStep"/>) turns it into files and located diagnostics, and
/// <see cref="Produce"/> adds and reports them. Each step's output is a value, so an unchanged description is cached at
/// the first step and nothing after it reruns (Roslyn's incremental-generators cookbook).
/// </summary>
internal static class DescriptionPipeline
{
    /// <summary>The tracking name of the step that picks the description and the part it is emitted as.</summary>
    public const string SelectStep = "Zabbix.Select";

    /// <summary>The tracking name of the read step.</summary>
    public const string ParseStep = "Zabbix.Parse";

    /// <summary>The tracking name of the emit step.</summary>
    public const string EmitStep = "Zabbix.Emit";

    /// <summary>
    /// The description file and the part its <c>ZabbixEmit</c> metadata names (<see langword="null"/> when it names
    /// none), tracked as <see cref="SelectStep"/>. The file is picked by its name before the options join it, and the
    /// options are reduced to the one value read from them, so an <c>.editorconfig</c> or global-options edit that leaves
    /// the part alone gives an equal value and the description is not read again (Roslyn's incremental-generators
    /// cookbook: extract what a step needs before the expensive one).
    /// </summary>
    /// <param name="context">The generator's initialization context.</param>
    /// <param name="fileName">Whether a file of that name is a description this generator reads.</param>
    public static IncrementalValuesProvider<(AdditionalText Text, string? Part)> Select(IncrementalGeneratorInitializationContext context, Func<string, bool> fileName)
        => context.AdditionalTextsProvider
            .Where(text => fileName(Path.GetFileName(text.Path)))
            .Combine(context.AnalyzerConfigOptionsProvider)
            .Select(static (pair, _) => (pair.Left, pair.Right.GetOptions(pair.Left).TryGetValue("build_metadata.AdditionalFiles.ZabbixEmit", out string? part) ? part : null))
            .WithTrackingName(SelectStep);

    /// <summary>The description in <paramref name="text"/>, read once.</summary>
    /// <param name="text">The additional file.</param>
    /// <param name="part">What is emitted from it.</param>
    /// <param name="cancellationToken">The compiler's.</param>
    public static ParsedDescription Parse(AdditionalText text, string part, CancellationToken cancellationToken)
    {
        string? json = text.GetText(cancellationToken)?.ToString();
        var problems = new List<EmitProblem>();
        if (json is null)
        {
            problems.Add(new(ZabbixDiagnostics.Unreadable, "the file could not be read", "zabbix-api.json", null));
            return new(text.Path, part, string.Empty, null, [.. problems]);
        }
        cancellationToken.ThrowIfCancellationRequested();
        ApiDescription? api = ApiDescription.Read(json, problems);
        return new(text.Path, part, json, api, [.. problems]);
    }

    /// <summary>The files and the diagnostics of <paramref name="parsed"/>, emitted by <paramref name="emit"/>.</summary>
    /// <param name="parsed">The read description.</param>
    /// <param name="emit">The emitter.</param>
    /// <param name="cancellationToken">The compiler's.</param>
    /// <remarks>
    /// Two files with one hint name (the compiler compares hint names without case) would make <c>AddSource</c> throw, so
    /// the later is left out here and reported as a located diagnostic: the description's entry the file is named for,
    /// or the description's first line when no entry carries that name.
    /// </remarks>
    public static GeneratorOutput Emit(ParsedDescription parsed, Func<ApiDescription, string, CancellationToken, EmitResult> emit, CancellationToken cancellationToken)
    {
        EmitResult result = parsed.Api is null ? new([], []) : emit(parsed.Api, parsed.Part, cancellationToken);
        List<EmitProblem> problems = [.. parsed.Problems, .. result.Problems];
        var hints = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        List<GeneratedSource> files = [];
        foreach (GeneratedSource file in result.Files)
        {
            if (hints.Add(file.Name)) files.Add(file);
            else problems.Add(new(ZabbixDiagnostics.Malformed, $"two entries of the description would be one file ({file.Name}); the later one is left out", Named(file.Name), null));
        }
        if (problems.Count == 0) return new(result.Files, []);
        SourceText source = SourceText.From(parsed.Text);
        return new([.. files], [.. problems.Select(problem => Locate(parsed.Path, source, problem))]);
    }

    /// <summary>Adds the files and reports the diagnostics.</summary>
    /// <param name="production">The compiler's output.</param>
    /// <param name="output">What the emit step produced, its hint names already unique.</param>
    public static void Produce(SourceProductionContext production, GeneratorOutput output)
    {
        foreach (GeneratedSource file in output.Files)
        {
            production.CancellationToken.ThrowIfCancellationRequested();
            production.AddSource(file.Name, SourceText.From(file.Code, Encoding.UTF8));
        }
        foreach (LocatedDiagnostic diagnostic in output.Diagnostics) production.ReportDiagnostic(diagnostic.ToDiagnostic());
    }

    // The description entry a generated file is named for: its hint name less the generated-file suffix and the type
    // prefix the emitter adds (ZabbixHost.g.cs is the entry named Host).
    private static string Named(string hint)
    {
        string name = hint.EndsWith(".g.cs", StringComparison.OrdinalIgnoreCase) ? hint.Substring(0, hint.Length - ".g.cs".Length) : hint;
        return name.StartsWith("Zabbix", StringComparison.Ordinal) && name.Length > "Zabbix".Length ? name.Substring("Zabbix".Length) : name;
    }

    /// <summary>
    /// The line of the description a problem is about: the line the reader names, else the record's (or object's)
    /// entry, then the field or method in it; the file's start when neither is found.
    /// </summary>
    /// <param name="path">The description's path.</param>
    /// <param name="source">Its text.</param>
    /// <param name="problem">The problem.</param>
    public static LocatedDiagnostic Locate(string path, SourceText source, EmitProblem problem)
    {
        string subject = problem.Descriptor == ZabbixDiagnostics.Unreadable ? path : problem.Subject;
        TextLine? line = null;
        if (problem.Line is long number && number >= 0 && number < source.Lines.Count)
        {
            line = source.Lines[(int)number];
        }
        else
        {
            string text = source.ToString();
            int at = text.IndexOf("\"name\": \"" + problem.Record + "\"", StringComparison.Ordinal);
            if (at < 0) at = text.IndexOf("\"object\": \"" + problem.Record + "\"", StringComparison.Ordinal);
            if (at < 0) at = text.IndexOf("\"concept\": \"" + problem.Record + "\"", StringComparison.Ordinal);
            if (at >= 0 && problem.Wire is not null)
            {
                int inner = text.IndexOf("\"wire\": \"" + problem.Wire + "\"", at, StringComparison.Ordinal);
                if (inner < 0) inner = text.IndexOf("\"api\": \"" + problem.Wire + "\"", at, StringComparison.Ordinal);
                if (inner < 0) inner = text.IndexOf("\"name\": \"" + problem.Wire + "\"", at, StringComparison.Ordinal);
                if (inner >= 0) at = inner;
            }
            if (at >= 0) line = source.Lines.GetLineFromPosition(at);
        }
        return line is TextLine found
            ? new(problem.Descriptor, path, found.Span, source.Lines.GetLinePositionSpan(found.Span), subject, problem.Message)
            : new(problem.Descriptor, path, default, default, subject, problem.Message);
    }
}
