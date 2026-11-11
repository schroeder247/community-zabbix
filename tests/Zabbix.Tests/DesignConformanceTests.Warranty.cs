// This Source Code Form is subject to the terms of the Mozilla Public
// License, v. 2.0. Copyright (c) 2026 schroeder247. SPDX-License-Identifier: MPL-2.0
using System.Reflection;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Xml.Linq;
using Community.Zabbix.Tests.Conformance;

namespace Community.Zabbix.Tests;

/// <summary>
/// The warranty rule: the software is provided "as is" (the LICENSE's disclaimer of warranty, MPL-2.0 sections 6 and 7),
/// and every artefact a user meets says so in one sentence. The sentence has one source, the <c>ZabbixWarrantyNotice</c>
/// property in <c>Directory.Build.props</c>; every package description and module manifest description carries it
/// before the trademark notice, and every document shown to users, every package page, both walk-throughs, the module's
/// help and the documentation footer carry it word for word. Text is compared with its whitespace collapsed.
/// </summary>
/// <remarks>Reading the documents allocates, so these run in the probes' collection, as the trademark rule's do.</remarks>
[Collection(nameof(ChannelsProbeTests))]
public sealed partial class DesignConformanceWarrantyTests
{
    private const string TrademarkNotice = "This project is not affiliated with, endorsed by or sponsored by Zabbix LLC.";

    /// <summary>The documents shown to users that are not a package's own page, relative to the root.</summary>
    private static readonly string[] Documents =
    [
        "README.md", "index.md", "CONTRIBUTING.md", "SECURITY.md", "THIRD-PARTY-NOTICES.md", "docs/GETTING-STARTED.md", "samples/README.md",
        "src/Zabbix.PowerShell/GETTING-STARTED.md", "src/Zabbix.PowerShell/en-US/about_Community.Zabbix.help.txt",
        "src/Zabbix.PowerShell.Agent/en-US/about_Community.Zabbix.Agent.help.txt",
    ];

    private static string Notice { get; } = ReadNotice();

    [Fact]
    public void The_notice_has_one_source_and_says_as_is_and_at_your_own_risk()
    {
        Assert.Contains("\"as is\", without warranty of any kind", Notice, StringComparison.Ordinal);
        Assert.Contains("at your own risk", Notice, StringComparison.Ordinal);
    }

    [Fact]
    public void Every_document_shown_to_users_and_every_package_page_carries_the_notice()
    {
        IEnumerable<string> pages = Directory.EnumerateDirectories(Path.Combine(Repository.Root, "src"))
            .Select(project => Path.Combine(project, "README.md"))
            .Where(File.Exists);
        List<string> files = [.. Documents.Select(path => Path.Combine(Repository.Root, path)).Concat(pages)];

        Assert.Contains(files, path => path.EndsWith(Path.Combine("Zabbix.Sdk", "README.md"), StringComparison.Ordinal));
        DesignConformanceTests.AssertNone(files
            .Where(path => !Collapse(File.ReadAllText(path)).Contains(Notice, StringComparison.Ordinal))
            .Select(path => Path.GetRelativePath(Repository.Root, path)));
    }

    [Fact]
    public void Every_module_manifest_description_carries_the_notice_before_the_trademark_notice()
    {
        List<string> manifests = [.. Directory.EnumerateDirectories(Path.Combine(Repository.Root, "src"))
            .SelectMany(project => Directory.EnumerateFiles(project, "*.psd1", SearchOption.TopDirectoryOnly))];

        Assert.Equal(3, manifests.Count);
        DesignConformanceTests.AssertNone(manifests
            .Where(path => ManifestDescription().Match(File.ReadAllText(path)) is not { Success: true } match
                || !match.Groups["text"].Value.Contains(" " + Notice + " " + TrademarkNotice, StringComparison.Ordinal))
            .Select(path => Path.GetRelativePath(Repository.Root, path)));
    }

    [Fact]
    public void Every_library_description_and_the_documentation_footer_carry_the_notice()
    {
        DesignConformanceTests.AssertNone(Libraries.All
            .Where(library => library.GetCustomAttribute<AssemblyDescriptionAttribute>() is not { } description
                || !Collapse(description.Description).Contains(" " + Notice + " " + TrademarkNotice, StringComparison.Ordinal))
            .Select(library => library.GetName().Name!));

        using JsonDocument docfx = JsonDocument.Parse(File.ReadAllText(Path.Combine(Repository.Root, "docfx.json")));
        string footer = docfx.RootElement.GetProperty("build").GetProperty("globalMetadata").GetProperty("_appFooter").GetString()!;
        Assert.Contains(" " + Notice + " ", footer, StringComparison.Ordinal);
    }

    private static string Collapse(string text) => Whitespace().Replace(text, " ");

    private static string ReadNotice()
        => XDocument.Load(Path.Combine(Repository.Root, "Directory.Build.props"))
            .Descendants("ZabbixWarrantyNotice").Single().Value;

    [GeneratedRegex(@"\s+")]
    private static partial Regex Whitespace();

    [GeneratedRegex(@"(?m)^\s*Description\s*=\s*'(?<text>[^']*)'")]
    private static partial Regex ManifestDescription();
}
