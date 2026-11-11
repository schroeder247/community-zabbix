// This Source Code Form is subject to the terms of the Mozilla Public
// License, v. 2.0. Copyright (c) 2026 schroeder247. SPDX-License-Identifier: MPL-2.0
using Community.Zabbix.Tests.Conformance;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;

namespace Community.Zabbix.Tests;

/// <summary>
/// A reply the client half reads is checked for its shape before it is read. <c>JsonElement.GetProperty</c> throws
/// <see cref="KeyNotFoundException"/> for a missing member, the base of <c>ZabbixNotFoundException</c>, so a malformed
/// reply would read as "not found"; <c>GetString()!</c> turns a member of the wrong kind into a null passed on as text.
/// Both are refused in the client packages: a member is read with <c>TryGetProperty</c> and a kind check, and a reply of
/// the wrong shape is an <see cref="InvalidDataException"/>.
/// </summary>
public sealed partial class DesignConformanceTests
{
    // The client packages, by package id, which is also their assembly name; each one's sources are under src/ by the id
    // without its "Community." prefix.
    private static readonly string[] ClientPackages = ["Community.Zabbix.Abstractions", "Community.Zabbix.Api", "Community.Zabbix.Sdk", "Community.Zabbix.Wire"];

    [Fact]
    public void No_client_reply_is_read_with_an_accessor_that_throws_the_wrong_exception()
        => AssertNone(UncheckedReplyReads(Sources.Library.Where(tree => ClientPackages.Any(package => tree.FilePath.Replace('\\', '/').Contains($"/src/{package["Community.".Length..]}/", StringComparison.Ordinal)))));

    [Fact]
    public void The_reply_rule_finds_get_property_and_a_forgiven_get_string()
    {
        SyntaxTree tree = Sources.ParseText("""
            class A
            {
                string M(System.Text.Json.JsonElement row)
                {
                    string a = row.GetProperty("id").GetString()!;
                    return row.TryGetProperty("id", out var id) ? id.GetString() ?? "" : a;
                }
            }
            """, "src/X/A.cs");

        Assert.Equal(["A.cs:5: GetString()!", "A.cs:5: GetProperty"], UncheckedReplyReads([tree]));
    }

    internal static IEnumerable<string> UncheckedReplyReads(IEnumerable<SyntaxTree> trees)
    {
        foreach (SyntaxTree tree in trees)
        {
            foreach (SyntaxNode node in tree.GetRoot().DescendantNodes())
            {
                if (node is InvocationExpressionSyntax { Expression: MemberAccessExpressionSyntax { Name.Identifier.ValueText: "GetProperty" } } get
                    && get.ArgumentList.Arguments is [{ Expression: LiteralExpressionSyntax or IdentifierNameSyntax or InterpolatedStringExpressionSyntax }])
                    yield return $"{Path.GetFileName(tree.FilePath)}:{get.GetLocation().GetLineSpan().StartLinePosition.Line + 1}: GetProperty";
                if (node is PostfixUnaryExpressionSyntax { RawKind: (int)SyntaxKind.SuppressNullableWarningExpression, Operand: InvocationExpressionSyntax { Expression: MemberAccessExpressionSyntax { Name.Identifier.ValueText: "GetString" } } } forgiven)
                    yield return $"{Path.GetFileName(tree.FilePath)}:{forgiven.GetLocation().GetLineSpan().StartLinePosition.Line + 1}: GetString()!";
            }
        }
    }
}
