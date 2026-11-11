// This Source Code Form is subject to the terms of the Mozilla Public
// License, v. 2.0. Copyright (c) 2026 schroeder247. SPDX-License-Identifier: MPL-2.0
using System.Text.Json;
using Community.Zabbix.Tests.Conformance;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;

namespace Community.Zabbix.Tests;

/// <summary>
/// The rules the build enforces, proven active by a violation compile, and the gaps the analyzers leave, closed by a rule
/// that is itself proven on a probe (a mechanism nobody has seen fail is prose).
/// </summary>
public sealed partial class DesignConformanceTests
{
    /// <summary>Every <c>// expect:</c> mark in the probe project, one row each.</summary>
    public static TheoryData<string, int, string> ProbeMarks
        => [.. AnalyzerProbe.Expected().Select(mark => new TheoryDataRow<string, int, string>(mark.File, mark.Line, mark.Id))];

    /// <summary>
    /// The build rejects what it claims to. Wall-clock reads, sync-over-async in a synchronous method
    /// (which CA1849 does not see), raw threads, <c>SemaphoreSlim</c> and clockless delays (RS0030 through
    /// <c>BannedSymbols.txt</c>), an unfiltered catch-all (CA1031), a direct list or array on a public surface
    /// (CA1002/CA1819), an <c>async</c> method without the suffix (IDE1006): each is a marked line of
    /// <c>tests/probes/Zabbix.Analyzers.Probe</c>, and the probe build must report exactly that id on exactly that line.
    /// </summary>
    [Theory]
    [MemberData(nameof(ProbeMarks))]
    public async Task The_build_rejects_each_probe_violation(string file, int line, string id)
    {
        IReadOnlyList<AnalyzerProbe.ProbeError> errors = await AnalyzerProbe.ErrorsAsync();

        Assert.Contains(new AnalyzerProbe.ProbeError(file, line, id), errors);
    }

    [Fact]
    public async Task The_probe_build_fails_only_where_a_probe_is_marked()
    {
        HashSet<AnalyzerProbe.ProbeError> expected = [.. AnalyzerProbe.Expected()];

        IReadOnlyList<AnalyzerProbe.ProbeError> errors = await AnalyzerProbe.ErrorsAsync();

        AssertNone(errors.Where(error => !expected.Contains(error)).Select(error => $"{error.File}({error.Line}): {error.Id} is not marked"));
    }

    /// <summary>
    /// The probe proves the banned-symbol rule for a library-shaped project; this proves every actual library is one:
    /// its restore took <c>Microsoft.CodeAnalysis.BannedApiAnalyzers</c> (the condition in <c>Directory.Build.targets</c>
    /// holds for it). A library that became an executable or a test project would silently lose the wall-clock rule.
    /// </summary>
    public static TheoryData<string> LibraryNames => [.. Libraries.All.Select(Libraries.Folder)];

    [Theory]
    [MemberData(nameof(LibraryNames))]
    public void Every_library_builds_with_the_banned_symbol_analyzer(string library)
    {
        using JsonDocument assets = JsonDocument.Parse(File.ReadAllBytes(Path.Combine(Repository.Root, "src", library, "obj", "project.assets.json")));

        Assert.Contains(assets.RootElement.GetProperty("libraries").EnumerateObject(), package => package.Name.StartsWith("Microsoft.CodeAnalysis.BannedApiAnalyzers/", StringComparison.Ordinal));
    }

    /// <summary>
    /// No catch-all drops an exception. CA1031 rejects <c>catch (Exception)</c> but not the FILTERED
    /// form: <c>catch (Exception e) when (Log(e)) { }</c> passes it (measured on the probe). A filter narrows only when it
    /// is a positive type test of the caught exception (<c>e is A or B</c>, optionally <c>&amp;&amp;</c> more); any other
    /// filter keeps the clause a catch-all, which the design standard allows at a boundary (one bad element must not stop the
    /// cadence) on one condition this rule checks: the block rethrows, or hands the caught exception on (logs it, records
    /// it, traces it, answers with it). A block that does neither has made the failure vanish.
    /// </summary>
    [Fact]
    public void No_filtered_catch_all_drops_the_exception()
        => AssertNone(Waivers.Outstanding(nameof(No_filtered_catch_all_drops_the_exception), SwallowingCatchAlls(Sources.Library)));

    [Fact]
    public void The_catch_all_rule_finds_the_filtered_drop_the_analyzer_misses()
    {
        string path = Path.Combine(AnalyzerProbe.Directory, "Catching.cs");
        string[] lines = File.ReadAllLines(path);
        List<string> marked = [.. lines.Select((text, index) => (text, index)).Where(line => line.text.Contains("// rule: filtered-catch-all", StringComparison.Ordinal)).Select(line => $"Catching.cs:{line.index + 1}")];

        List<string> found = [.. SwallowingCatchAlls([Sources.ParseText(File.ReadAllText(path), path)])];

        Assert.NotEmpty(marked);
        Assert.Equal(marked, found);
    }

    /// <summary>
    /// Generated code obeys the library rules, <c>the generator</c> meaning ours
    /// (<c>Community.Zabbix.Generator.ZabbixApiEmitter</c>, under every project's own
    /// <c>obj/.../generated/Zabbix.Generator/Community.Zabbix.Generator.ZabbixApiGenerator/</c>), not the BCL's: System.Text.Json's,
    /// <c>[LoggerMessage]</c>'s, <c>[OptionsValidator]</c>'s and the configuration binder's generators write their own
    /// <c>&lt;auto-generated&gt;</c> (a Microsoft contract this SDK does not own and cannot turn off), the same scope a
    /// member rule draws when the <see cref="System.Text.Json.Serialization.Metadata.JsonTypeInfo{T}"/>
    /// exemption pins the same way. Ours carries no blanket <c>#pragma warning disable</c> and no
    /// <c>&lt;auto-generated&gt;</c> marker (either one turns every analyzer off for the file), and <c>.editorconfig</c>
    /// does not declare <c>*.g.cs</c> generated.
    /// </summary>
    [Fact]
    public void Generated_code_is_held_to_the_library_rules()
    {
        IEnumerable<string> generated = Directory.EnumerateFiles(Path.Combine(Repository.Root, "src"), "*.g.cs", SearchOption.AllDirectories)
            .Where(file => file.Replace('\\', '/').Contains("/generated/Zabbix.Generator/Community.Zabbix.Generator.ZabbixApiGenerator/", StringComparison.Ordinal));
        List<string> offenders = [];
        foreach (string file in generated)
        {
            string text = File.ReadAllText(file);
            if (text.Contains("<auto-generated", StringComparison.Ordinal)) offenders.Add($"{Path.GetFileName(file)}: <auto-generated> marker");
            if (text.Split('\n').Any(line => line.Trim() == "#pragma warning disable")) offenders.Add($"{Path.GetFileName(file)}: blanket #pragma warning disable");
        }
        if (File.ReadAllText(Path.Combine(Repository.Root, ".editorconfig")).Contains("generated_code = true", StringComparison.Ordinal))
            offenders.Add(".editorconfig: generated_code = true");

        AssertNone(offenders);
    }

    internal static IEnumerable<string> SwallowingCatchAlls(IEnumerable<SyntaxTree> trees)
    {
        foreach (SyntaxTree tree in trees)
        {
            foreach (CatchClauseSyntax clause in tree.GetRoot().DescendantNodes().OfType<CatchClauseSyntax>())
            {
                if (!IsCatchAll(clause.Declaration?.Type) || clause.Filter is null) continue;       // typed, or CA1031's case
                string? variable = clause.Declaration?.Identifier.ValueText;
                if (variable is { Length: > 0 } && Narrows(clause.Filter.FilterExpression, variable)) continue;
                if (Throws(clause.Block) || (variable is { Length: > 0 } && HandsOn(clause.Block, variable))) continue;
                yield return $"{Path.GetFileName(tree.FilePath)}:{clause.GetLocation().GetLineSpan().StartLinePosition.Line + 1}";
            }
        }
    }

    private static bool IsCatchAll(TypeSyntax? type)
        => type is null || (type switch
        {
            IdentifierNameSyntax name => name.Identifier.ValueText,
            QualifiedNameSyntax qualified => qualified.Right.Identifier.ValueText,
            AliasQualifiedNameSyntax alias => alias.Name.Identifier.ValueText,
            _ => null,
        }) is "Exception" or "SystemException";

    private static bool Narrows(ExpressionSyntax filter, string variable) => filter switch
    {
        ParenthesizedExpressionSyntax inner => Narrows(inner.Expression, variable),
        BinaryExpressionSyntax { RawKind: (int)SyntaxKind.LogicalAndExpression } both => Narrows(both.Left, variable) || Narrows(both.Right, variable),
        BinaryExpressionSyntax { RawKind: (int)SyntaxKind.IsExpression, Left: IdentifierNameSyntax tested } => tested.Identifier.ValueText == variable,
        IsPatternExpressionSyntax { Expression: IdentifierNameSyntax tested } pattern => tested.Identifier.ValueText == variable && IsTypeTest(pattern.Pattern),
        _ => false,
    };

    private static bool IsTypeTest(PatternSyntax pattern) => pattern switch
    {
        TypePatternSyntax or DeclarationPatternSyntax => true,
        RecursivePatternSyntax { Type: not null } => true,
        ConstantPatternSyntax { Expression: NameSyntax or MemberAccessExpressionSyntax } => true,   // `e is IOException`, parsed as a constant
        ParenthesizedPatternSyntax inner => IsTypeTest(inner.Pattern),
        BinaryPatternSyntax { RawKind: (int)SyntaxKind.OrPattern } either => IsTypeTest(either.Left) && IsTypeTest(either.Right),
        BinaryPatternSyntax { RawKind: (int)SyntaxKind.AndPattern } both => IsTypeTest(both.Left) || IsTypeTest(both.Right),
        _ => false,
    };

    private static bool HandsOn(BlockSyntax block, string variable)
        => block.DescendantNodes().OfType<IdentifierNameSyntax>().Any(name => name.Identifier.ValueText == variable);

    // A throw in the block itself, not in a lambda or local function declared inside it (that one throws elsewhere).
    private static bool Throws(BlockSyntax block)
        => block.DescendantNodes(node => node is not (AnonymousFunctionExpressionSyntax or LocalFunctionStatementSyntax))
            .Any(node => node is ThrowStatementSyntax or ThrowExpressionSyntax);
}
