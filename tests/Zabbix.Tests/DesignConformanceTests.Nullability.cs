// This Source Code Form is subject to the terms of the Mozilla Public
// License, v. 2.0. Copyright (c) 2026 schroeder247. SPDX-License-Identifier: MPL-2.0
using Community.Zabbix.Tests.Conformance;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;

namespace Community.Zabbix.Tests;

/// <summary>
/// The null-forgiving operator (<c>x!</c>) tells the compiler a value is not null without proving it. Each one is a
/// place where a validated, non-nullable type or a pattern (<c>is { } value</c>) would have carried the proof instead, so
/// a project's count may only fall: lower its ceiling when one goes.
/// </summary>
public sealed partial class DesignConformanceTests
{
    /// <summary>The most null-forgiving operators each library's hand-written sources may hold.</summary>
    private static readonly IReadOnlyDictionary<string, int> NullForgivingCeilings = new Dictionary<string, int>(StringComparer.Ordinal)
    {
        ["Zabbix.Abstractions"] = 0,
        ["Zabbix.Api"] = 0,
        ["Zabbix.Sdk"] = 1,
        ["Zabbix.Wire"] = 0,
        ["Zabbix.Agent"] = 6,
        ["Zabbix.Agent.Storage.EntityFrameworkCore"] = 0,
        ["Zabbix.Proxy"] = 9,
    };

    [Fact]
    public void Null_forgiving_operators_per_library_only_fall()
    {
        Dictionary<string, int> counts = NullForgivingCounts(Sources.Library);
        List<string> over = [.. counts
            .Where(count => count.Value > NullForgivingCeilings.GetValueOrDefault(count.Key))
            .Select(count => $"{count.Key}: {count.Value} null-forgiving operators, ceiling {NullForgivingCeilings.GetValueOrDefault(count.Key)}")];

        AssertNone(over);
    }

    [Fact]
    public void The_null_forgiving_count_sees_a_forgiven_member_and_not_a_logical_not()
    {
        SyntaxTree tree = Sources.ParseText("class A { string? B; int M() => B!.Length + (!true ? 1 : 0); }", "/r/src/Zabbix.Probe/A.cs");

        Assert.Equal(1, NullForgivingCounts([tree])["Zabbix.Probe"]);
    }

    internal static Dictionary<string, int> NullForgivingCounts(IEnumerable<SyntaxTree> trees)
        => trees
            .GroupBy(tree => Project(tree.FilePath))
            .ToDictionary(group => group.Key, group => group.Sum(tree => tree.GetRoot().DescendantNodes().Count(node => node.IsKind(SyntaxKind.SuppressNullableWarningExpression))), StringComparer.Ordinal);

    private static string Project(string path)
    {
        string normalized = path.Replace('\\', '/');
        int src = normalized.LastIndexOf("/src/", StringComparison.Ordinal);
        return src < 0 ? string.Empty : normalized[(src + 5)..].Split('/')[0];
    }
}
