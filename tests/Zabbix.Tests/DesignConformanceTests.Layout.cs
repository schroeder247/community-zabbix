// This Source Code Form is subject to the terms of the Mozilla Public
// License, v. 2.0. Copyright (c) 2026 schroeder247. SPDX-License-Identifier: MPL-2.0
using Community.Zabbix.Tests.Conformance;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;

namespace Community.Zabbix.Tests;

/// <summary>
/// The source layout dotnet/runtime keeps (docs/adr/0004-conventions.md): one public type per file, the file named after
/// the type. A generic type whose name a non-generic type in the same folder also takes is named <c>Name_N.cs</c>
/// (<c>ZabbixEntityShape_1.cs</c>, as <c>ImmutableArray_1.cs</c>); a partial part of a type declared elsewhere is
/// <c>Name.Part.cs</c> (<c>ZabbixHostClient.MassUpdate.cs</c>). Internal helpers may stay beside their user. The folder
/// half of the rule (namespace = RootNamespace + folder) is IDE0130, an error in the build.
/// </summary>
public sealed partial class DesignConformanceTests
{
    /// <summary>Files that hold two public types on purpose. Empty: every split was possible.</summary>
    private static readonly HashSet<string> TwoPublicTypesAllowed = new(StringComparer.Ordinal);

    [Fact]
    public void Every_public_type_is_in_a_file_named_after_it()
        => Assert.Empty(MisnamedPublicTypes(SourceTrees()));

    [Fact]
    public void A_source_file_declares_at_most_one_public_type()
    {
        List<string> crowded = CrowdedFiles(SourceTrees());
        crowded.RemoveAll(TwoPublicTypesAllowed.Contains);
        Assert.Empty(crowded);
    }

    [Fact]
    public void The_layout_rules_catch_a_misnamed_and_a_crowded_file()
    {
        SyntaxTree probe = Sources.ParseText("namespace Community.Zabbix; public sealed class A; public sealed class B; internal sealed class C;", Path.Combine(Repository.Root, "src", "X", "A.cs"));
        SyntaxTree generic = Sources.ParseText("namespace Community.Zabbix; public static class Q; ", Path.Combine(Repository.Root, "src", "X", "Q.cs"));
        SyntaxTree arity = Sources.ParseText("namespace Community.Zabbix; public sealed class Q<T1, T2>; ", Path.Combine(Repository.Root, "src", "X", "Q_2.cs"));
        SyntaxTree part = Sources.ParseText("namespace Community.Zabbix; public partial class Q; ", Path.Combine(Repository.Root, "src", "X", "Q.Part.cs"));

        Assert.Equal(["X/A.cs: B"], MisnamedPublicTypes([probe, generic, arity, part]));
        Assert.Equal(["X/A.cs"], CrowdedFiles([probe, generic, arity, part]));
    }

    private static List<SyntaxTree> SourceTrees()
    {
        string src = Path.Combine(Repository.Root, "src");
        string obj = $"{Path.DirectorySeparatorChar}obj{Path.DirectorySeparatorChar}";
        string bin = $"{Path.DirectorySeparatorChar}bin{Path.DirectorySeparatorChar}";
        return [.. Directory.EnumerateFiles(src, "*.cs", SearchOption.AllDirectories)
            .Where(file => !file.Contains(obj, StringComparison.Ordinal) && !file.Contains(bin, StringComparison.Ordinal))
            .Select(file => Sources.ParseText(File.ReadAllText(file), file))];
    }

    private static IEnumerable<BaseTypeDeclarationOrDelegate> PublicTopLevelTypes(SyntaxTree tree)
        => tree.GetRoot().DescendantNodes(node => node is CompilationUnitSyntax or BaseNamespaceDeclarationSyntax)
            .Select(BaseTypeDeclarationOrDelegate.From).OfType<BaseTypeDeclarationOrDelegate>()
            .Where(type => type.IsPublic);

    private static List<string> MisnamedPublicTypes(IEnumerable<SyntaxTree> trees)
        => [.. trees.SelectMany(tree => PublicTopLevelTypes(tree)
                .Where(type => !Matches(Path.GetFileNameWithoutExtension(tree.FilePath), type))
                .Select(type => $"{Relative(tree)}: {type.Name}"))];

    private static List<string> CrowdedFiles(IEnumerable<SyntaxTree> trees)
        => [.. trees.Where(tree => PublicTopLevelTypes(tree).Count() > 1).Select(Relative)];

    private static bool Matches(string stem, BaseTypeDeclarationOrDelegate type)
        => stem == type.Name
            || (type.Arity > 0 && stem == $"{type.Name}_{type.Arity}")
            || (type.IsPartial && stem.StartsWith(type.Name + ".", StringComparison.Ordinal));

    private static string Relative(SyntaxTree tree) => Path.GetRelativePath(Path.Combine(Repository.Root, "src"), tree.FilePath).Replace('\\', '/');

    private sealed record BaseTypeDeclarationOrDelegate(string Name, int Arity, bool IsPublic, bool IsPartial)
    {
        public static BaseTypeDeclarationOrDelegate? From(SyntaxNode node) => node switch
        {
            TypeDeclarationSyntax type => new(type.Identifier.ValueText, type.Arity, Has(type.Modifiers, SyntaxKind.PublicKeyword), Has(type.Modifiers, SyntaxKind.PartialKeyword)),
            EnumDeclarationSyntax type => new(type.Identifier.ValueText, 0, Has(type.Modifiers, SyntaxKind.PublicKeyword), false),
            DelegateDeclarationSyntax type => new(type.Identifier.ValueText, type.Arity, Has(type.Modifiers, SyntaxKind.PublicKeyword), false),
            _ => null,
        };

        private static bool Has(SyntaxTokenList modifiers, SyntaxKind kind) => modifiers.Any(modifier => modifier.IsKind(kind));
    }
}
