// This Source Code Form is subject to the terms of the Mozilla Public
// License, v. 2.0. Copyright (c) 2026 schroeder247. SPDX-License-Identifier: MPL-2.0
using System.Reflection;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Xml.Linq;
using Community.Zabbix.Tests.Conformance;

namespace Community.Zabbix.Tests;

/// <summary>
/// The trademark rule (CONTRIBUTING.md, "The Zabbix name"; docs/design/decisions.md, "Zabbix's trademark and licences"):
/// Zabbix's trademark policy asks every use of the mark to carry the sentence "Zabbix is a registered trademark of Zabbix
/// LLC", and the project states beside it that it is not affiliated with Zabbix LLC. The notice has one source, the
/// <c>ZabbixTrademarkNotice</c> property in <c>Directory.Build.props</c>, which every package description ends with; every
/// other artefact that ships or is shown to users carries it word for word, and no tracked document words either
/// sentence differently. Line breaks and indentation are not wording: text is compared with its whitespace collapsed.
/// </summary>
/// <remarks>
/// Reading the tracked documents allocates, and <see cref="ChannelsProbeTests"/> measures the whole process's allocation,
/// so these run in the probes' collection: one after another with them, never beside them.
/// </remarks>
[Collection(nameof(ChannelsProbeTests))]
public sealed partial class DesignConformanceTrademarkTests
{
    /// <summary>The policy's own sentence, as its "Rules of use" give it.</summary>
    private const string TrademarkSentence = "Zabbix is a registered trademark of Zabbix LLC.";

    private const string AffiliationPhrase = "not affiliated with, endorsed by or sponsored by Zabbix LLC";

    /// <summary>The documents shown to users that are not a package's or a module's own, relative to the root.</summary>
    private static readonly string[] Documents = ["README.md", "index.md", "CONTRIBUTING.md", "SECURITY.md", "THIRD-PARTY-NOTICES.md", "src/Zabbix.PowerShell/en-US/about_Community.Zabbix.help.txt",
        "src/Zabbix.PowerShell.Agent/en-US/about_Community.Zabbix.Agent.help.txt"];

    /// <summary>The tracked kinds whose words a reader sees; LICENSE and the API model are not the project's words.</summary>
    private static readonly string[] WordedExtensions = [".md", ".txt", ".psd1", ".json", ".props", ".targets", ".csproj"];

    private static string Notice { get; } = ReadNotice();

    [Fact]
    public void The_notice_has_one_source_and_states_the_policy_sentence_and_the_affiliation()
    {
        Assert.EndsWith(TrademarkSentence, Notice, StringComparison.Ordinal);
        Assert.Contains(AffiliationPhrase, Notice, StringComparison.Ordinal);
    }

    [Fact]
    public void Every_document_shown_to_users_and_every_package_readme_carries_the_notice()
    {
        IEnumerable<string> readmes = Directory.EnumerateDirectories(Path.Combine(Repository.Root, "src"))
            .Select(project => Path.Combine(project, "README.md"))
            .Where(File.Exists);
        List<string> files = [.. Documents.Select(path => Path.Combine(Repository.Root, path)).Concat(readmes)];

        Assert.Contains(files, path => path.EndsWith(Path.Combine("Zabbix.Sdk", "README.md"), StringComparison.Ordinal));
        DesignConformanceTests.AssertNone(files
            .Where(path => !Collapse(File.ReadAllText(path)).Contains(Notice, StringComparison.Ordinal))
            .Select(path => Path.GetRelativePath(Repository.Root, path)));
    }

    [Fact]
    public void Every_module_manifest_description_ends_with_the_notice()
    {
        List<string> manifests = [.. Directory.EnumerateDirectories(Path.Combine(Repository.Root, "src"))
            .SelectMany(project => Directory.EnumerateFiles(project, "*.psd1", SearchOption.TopDirectoryOnly))];

        Assert.Equal(3, manifests.Count);
        DesignConformanceTests.AssertNone(manifests
            .Where(path => ManifestDescription().Match(File.ReadAllText(path)) is not { Success: true } match
                || !match.Groups["text"].Value.EndsWith(" " + Notice, StringComparison.Ordinal))
            .Select(path => Path.GetRelativePath(Repository.Root, path)));
    }

    [Fact]
    public void Every_library_description_and_the_documentation_footer_end_with_the_notice()
    {
        DesignConformanceTests.AssertNone(Libraries.All
            .Where(library => library.GetCustomAttribute<AssemblyDescriptionAttribute>() is not { } description
                || !Collapse(description.Description).EndsWith(" " + Notice, StringComparison.Ordinal))
            .Select(library => library.GetName().Name!));

        using JsonDocument docfx = JsonDocument.Parse(File.ReadAllText(Path.Combine(Repository.Root, "docfx.json")));
        string footer = docfx.RootElement.GetProperty("build").GetProperty("globalMetadata").GetProperty("_appFooter").GetString()!;
        Assert.EndsWith(" " + Notice, footer, StringComparison.Ordinal);
    }

    [Fact]
    public async Task No_tracked_document_words_the_trademark_or_the_affiliation_differently()
    {
        IReadOnlyList<string> tracked = await DesignConformanceHeaderTests.TrackedFilesAsync();
        List<string> worded = [.. tracked.Where(IsWorded)];

        Assert.Contains("README.md", worded);
        DesignConformanceTests.AssertNone(worded.SelectMany(path => Variants(path, File.ReadAllText(Path.Combine(Repository.Root, path)))));
    }

    [Fact]
    public void The_variant_rule_finds_a_reworded_notice_and_accepts_a_wrapped_one()
    {
        Assert.Empty(Variants("a.md", "This project is not affiliated with, endorsed by or sponsored by Zabbix\n    LLC. Zabbix is a registered trademark of Zabbix LLC."));
        Assert.Single(Variants("a.md", "Not affiliated with Zabbix LLC."));
        Assert.NotEmpty(Variants("a.md", "Zabbix® is a registered trademark of Zabbix LLC."));
        Assert.Single(Variants("a.md", "not affiliated with, endorsed by, or sponsored by Zabbix LLC"));
        Assert.True(IsWorded("src/Zabbix.Sdk/README.md"));
        Assert.False(IsWorded("LICENSE"));
        Assert.False(IsWorded("schema/zabbix-api.json"));
    }

    /// <summary>Each way a document departs from the notice's two sentences: a reworded one, or the mark with a symbol.</summary>
    private static IEnumerable<string> Variants(string path, string text)
    {
        string collapsed = Collapse(text);
        if (Count(collapsed, "registered trademark") != Count(collapsed, TrademarkSentence))
            yield return $"{path}: words the trademark sentence differently from \"{TrademarkSentence}\"";
        if (Count(collapsed, "ffiliated with") != Count(collapsed, AffiliationPhrase))
            yield return $"{path}: words the affiliation differently from \"{AffiliationPhrase}\"";
        if (collapsed.Contains("Zabbix®", StringComparison.Ordinal))
            yield return $"{path}: marks the name with a symbol instead of the sentence";
    }

    private static bool IsWorded(string path)
        => WordedExtensions.Contains(Path.GetExtension(path), StringComparer.OrdinalIgnoreCase)
            && !path.StartsWith("schema/", StringComparison.Ordinal)
            && !path.EndsWith("packages.lock.json", StringComparison.Ordinal);

    private static int Count(string text, string value)
    {
        int count = 0;
        for (int at = text.IndexOf(value, StringComparison.Ordinal); at >= 0; at = text.IndexOf(value, at + value.Length, StringComparison.Ordinal))
            count++;
        return count;
    }

    private static string Collapse(string text) => Whitespace().Replace(text, " ");

    private static string ReadNotice()
        => XDocument.Load(Path.Combine(Repository.Root, "Directory.Build.props"))
            .Descendants("ZabbixTrademarkNotice").Single().Value;

    [GeneratedRegex(@"\s+")]
    private static partial Regex Whitespace();

    [GeneratedRegex(@"(?m)^\s*Description\s*=\s*'(?<text>[^']*)'")]
    private static partial Regex ManifestDescription();
}
