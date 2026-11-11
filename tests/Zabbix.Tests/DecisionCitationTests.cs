// This Source Code Form is subject to the terms of the Mozilla Public
// License, v. 2.0. Copyright (c) 2026 schroeder247. SPDX-License-Identifier: MPL-2.0
using System.Text.RegularExpressions;
using System.Xml.Linq;

namespace Community.Zabbix.Tests;

/// <summary>
/// The sources that point at docs/design/decisions.md keep pointing at something: a suppression's justification names
/// the decision that allows it by its heading, so a renamed heading fails here instead of leaving the justification
/// citing nothing. The banned pool bridges are the ones the decision lists, and every project builds with the language
/// version the repository pins.
/// </summary>
/// <remarks>
/// Reading every source allocates, and <see cref="ChannelsProbeTests"/> measures the whole process's allocation, so these
/// run in the probes' collection.
/// </remarks>
[Collection(nameof(ChannelsProbeTests))]
public sealed partial class DecisionCitationTests
{
    [Fact]
    public void Every_cited_decision_heading_exists()
    {
        HashSet<string> headings = [.. File.ReadLines(Path.Combine(Repository.Root, "docs", "design", "decisions.md"))
            .Where(line => line.StartsWith("### ", StringComparison.Ordinal))
            .Select(line => line[4..].Trim())];

        List<string> offenders = [.. SourceFiles("src")
            .SelectMany(file => Citation().Matches(File.ReadAllText(file)).Select(match => (File: file, Heading: match.Groups["heading"].Value)))
            .Where(cited => !headings.Any(heading => heading.StartsWith(cited.Heading, StringComparison.Ordinal)))
            .Select(cited => $"{Path.GetRelativePath(Repository.Root, cited.File)}: \"{cited.Heading}\"")];

        DesignConformanceTests.AssertNone(offenders);
    }

    [Fact]
    public void Only_the_recorded_bridges_start_work_on_the_pool()
    {
        string[] bridges = [.. SourceFiles("src")
            .Where(file => File.ReadAllText(file).Contains("Task.Run(", StringComparison.Ordinal))
            .Select(file => Path.GetFileName(file))
            .Order(StringComparer.Ordinal)];

        Assert.Equal(["ZabbixCmdlet.cs", "ZabbixNameCompleter.cs", "ZabbixSessionCredential.cs"], bridges);
    }

    [Fact]
    public void Every_project_builds_with_the_pinned_language_version()
    {
        string pinned = LangVersions(Path.Combine(Repository.Root, "Directory.Build.props")).Single();

        List<string> offenders = [.. new[] { "src", "tools", "tests" }
            .SelectMany(folder => Directory.EnumerateFiles(Path.Combine(Repository.Root, folder), "*.csproj", SearchOption.AllDirectories))
            .SelectMany(project => LangVersions(project).Where(version => version != pinned).Select(version => $"{Path.GetRelativePath(Repository.Root, project)}: {version}"))];

        Assert.Equal("14", pinned);
        DesignConformanceTests.AssertNone(offenders);
    }

    private static IEnumerable<string> LangVersions(string project)
        => XDocument.Load(project).Descendants("LangVersion").Select(element => element.Value.Trim());

    private static IEnumerable<string> SourceFiles(string folder)
    {
        string obj = $"{Path.DirectorySeparatorChar}obj{Path.DirectorySeparatorChar}";
        string bin = $"{Path.DirectorySeparatorChar}bin{Path.DirectorySeparatorChar}";
        return Directory.EnumerateFiles(Path.Combine(Repository.Root, folder), "*.cs", SearchOption.AllDirectories)
            .Where(file => !file.Contains(obj, StringComparison.Ordinal) && !file.Contains(bin, StringComparison.Ordinal));
    }

    /// <summary>A citation as the justifications write it: <c>decisions.md, \"Heading\"</c> inside a C# string.</summary>
    [GeneratedRegex("""decisions\.md, \\"(?<heading>[^\\"]+)\\"\)""")]
    private static partial Regex Citation();
}
