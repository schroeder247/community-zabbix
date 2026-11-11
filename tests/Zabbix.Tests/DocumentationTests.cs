// This Source Code Form is subject to the terms of the Mozilla Public
// License, v. 2.0. Copyright (c) 2026 schroeder247. SPDX-License-Identifier: MPL-2.0
using System.Text;
using System.Text.RegularExpressions;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;

namespace Community.Zabbix.Tests;

/// <summary>
/// What a reader copies from the documents stays true: every C# sample compiles against the libraries, as the Azure SDK's
/// documentation guidelines ask of snippets, and every link to another document and its heading resolves, so a renamed page
/// or heading fails here rather than in front of a reader. A sample may assume names of the application's own, such as
/// <c>apiToken</c> or <c>cancellationToken</c>: what it says of the SDK is what must hold.
/// </summary>
/// <remarks>Compiling allocates, so these run in the probes' collection, as the trademark rule's do.</remarks>
[Collection(nameof(ChannelsProbeTests))]
public sealed partial class DocumentationTests
{
    private const string RepositoryUrl = "https://github.com/schroeder247/community-zabbix/";

    private const string ImplicitUsings = """
        global using System;
        global using System.Collections.Generic;
        global using System.IO;
        global using System.Linq;
        global using System.Net.Http;
        global using System.Threading;
        global using System.Threading.Tasks;
        global using Community.Zabbix;
        global using Microsoft.Extensions.DependencyInjection;
        global using Microsoft.Extensions.Hosting;
        global using Microsoft.Extensions.Logging;
        """;

    private static readonly Lazy<IReadOnlyList<MetadataReference>> s_references = new(() =>
        [.. ((string)AppContext.GetData("TRUSTED_PLATFORM_ASSEMBLIES")!).Split(Path.PathSeparator)
            .Where(path => !Path.GetFileName(path).StartsWith("xunit", StringComparison.OrdinalIgnoreCase)
                && !Path.GetFileName(path).StartsWith("Microsoft.Testing", StringComparison.OrdinalIgnoreCase)
                && !Path.GetFileName(path).Contains("Zabbix.Tests", StringComparison.Ordinal))
            .Select(path => MetadataReference.CreateFromFile(path))]);

    /// <summary>What a reader follows: the pages with samples to copy.</summary>
    private static IEnumerable<string> ReaderPages()
    {
        string root = Repository.Root;
        IEnumerable<string> pages = [Path.Combine(root, "README.md"), Path.Combine(root, "samples", "README.md"), Path.Combine(root, "src", "Zabbix.PowerShell", "GETTING-STARTED.md")];
        return pages
            .Concat(Directory.EnumerateFiles(Path.Combine(root, "docs"), "*.md"))
            .Concat(Directory.EnumerateFiles(Path.Combine(root, "docs", "guides"), "*.md"))
            .Concat(Directory.EnumerateDirectories(Path.Combine(root, "src")).Select(project => Path.Combine(project, "README.md")).Where(File.Exists));
    }

    /// <summary>Every page a link can break in: the reader's, and the design records and the top-level files.</summary>
    private static IEnumerable<string> AllPages()
    {
        string root = Repository.Root;
        return ReaderPages()
            .Concat(Directory.EnumerateFiles(root, "*.md"))
            .Concat(Directory.EnumerateFiles(Path.Combine(root, "docs", "design"), "*.md"))
            .Concat(Directory.EnumerateFiles(Path.Combine(root, "docs", "adr"), "*.md"))
            .Distinct(StringComparer.Ordinal);
    }

    [Fact]
    public void Every_csharp_sample_in_the_documents_compiles()
    {
        List<string> problems = [];
        int samples = 0;
        foreach (string page in ReaderPages())
        {
            string text = File.ReadAllText(page);
            foreach (Match fence in CSharpFence().Matches(text))
            {
                samples++;
                int line = text[..fence.Index].Count(character => character == '\n') + 1;
                foreach (string problem in Problems(fence.Groups["code"].Value, line))
                    problems.Add($"{Path.GetRelativePath(Repository.Root, page)}:{problem}");
            }
        }

        Assert.True(samples > 50, $"found only {samples} samples: the fence pattern no longer matches the documents");
        DesignConformanceTests.AssertNone(problems);
    }

    [Fact]
    public void The_sample_check_finds_what_the_SDK_does_not_have_and_spares_what_the_application_owns()
    {
        Assert.NotEmpty(Problems("using var wire = new ZabbixWireClient(); wire.NoSuchMethod();", 1));
        Assert.NotEmpty(Problems("var missing = new ZabbixNoSuchClient();", 1));
        Assert.Empty(Problems("using var wire = new ZabbixWireClient(applicationsOwnServer);", 1));
    }

    [Fact]
    public void Every_link_to_another_document_and_its_heading_resolves()
    {
        List<string> broken = [];
        Dictionary<string, HashSet<string>> headings = new(StringComparer.Ordinal);
        foreach (string page in AllPages())
        {
            string text = AnyFence().Replace(File.ReadAllText(page), string.Empty);
            foreach (Match link in Link().Matches(text))
            {
                if (Target(page, link.Groups["target"].Value) is not { } target) continue;
                string relative = Path.GetRelativePath(Repository.Root, page);
                if (!File.Exists(target.Path) && !Directory.Exists(target.Path))
                    broken.Add($"{relative}: {link.Groups["target"].Value}: no such file");
                else if (target.Anchor is { } anchor && target.Path.EndsWith(".md", StringComparison.OrdinalIgnoreCase)
                    && !HeadingsOf(target.Path, headings).Contains(anchor))
                    broken.Add($"{relative}: {link.Groups["target"].Value}: no such heading");
            }
        }

        DesignConformanceTests.AssertNone(broken);
    }

    [Theory]
    [InlineData("Read an agent's item", "read-an-agents-item")]
    [InlineData("`ZabbixClient` and `ZabbixWireClient`: the two clients", "zabbixclient-and-zabbixwireclient-the-two-clients")]
    [InlineData("Status, statistics, queue", "status-statistics-queue")]
    public void A_heading_s_anchor_is_the_one_GitHub_makes(string heading, string anchor)
        => Assert.Equal(anchor, Slug(heading));

    private static IEnumerable<string> Problems(string code, int line)
    {
        CSharpCompilation compilation = CSharpCompilation.Create(
            "DocumentSample",
            [CSharpSyntaxTree.ParseText(ImplicitUsings, cancellationToken: TestContext.Current.CancellationToken), CSharpSyntaxTree.ParseText(code, cancellationToken: TestContext.Current.CancellationToken)],
            s_references.Value,
            new CSharpCompilationOptions(OutputKind.ConsoleApplication, nullableContextOptions: NullableContextOptions.Disable));
        List<Diagnostic> errors = [.. compilation.GetDiagnostics(TestContext.Current.CancellationToken).Where(diagnostic => diagnostic.Severity == DiagnosticSeverity.Error)];

        // A call given a name the sample assumes can't be judged: its argument has no type, so the compiler's complaint about the call says nothing.
        HashSet<int> assuming = [.. errors.Where(error => error.Id == "CS0103" && !NamesTheSdk(error)).Select(error => error.Location.GetLineSpan().StartLinePosition.Line)];
        return errors
            .Where(error => NamesTheSdk(error) && !assuming.Contains(error.Location.GetLineSpan().StartLinePosition.Line))
            .Select(diagnostic => $"{line + diagnostic.Location.GetLineSpan().StartLinePosition.Line + 1}: {diagnostic.Id} {diagnostic.GetMessage(System.Globalization.CultureInfo.InvariantCulture)}");
    }

    private static bool NamesTheSdk(Diagnostic diagnostic)
    {
        string message = diagnostic.GetMessage(System.Globalization.CultureInfo.InvariantCulture);
        return message.Contains("Zabbix", StringComparison.Ordinal) || message.Contains("Community.", StringComparison.Ordinal);
    }

    private static (string Path, string? Anchor)? Target(string page, string link)
    {
        if (link.StartsWith(RepositoryUrl, StringComparison.Ordinal))
        {
            Match local = RepositoryLink().Match(link);
            if (!local.Success) return null;
            link = Path.GetRelativePath(Path.GetDirectoryName(page)!, Repository.Root).Replace('\\', '/') + "/" + local.Groups["path"].Value;
        }
        else if (link.Contains(':', StringComparison.Ordinal))
        {
            return null;     // another site, or mail
        }

        string[] parts = link.Split('#', 2);
        string path = parts[0].Length == 0 ? page : Path.GetFullPath(Path.Combine(Path.GetDirectoryName(page)!, Uri.UnescapeDataString(parts[0])));
        return (path, parts.Length == 2 ? parts[1] : null);
    }

    private static HashSet<string> HeadingsOf(string page, Dictionary<string, HashSet<string>> known)
    {
        if (known.TryGetValue(page, out HashSet<string>? headings)) return headings;
        headings = new(StringComparer.Ordinal);
        foreach (Match heading in Heading().Matches(AnyFence().Replace(File.ReadAllText(page), string.Empty)))
        {
            string slug = Slug(heading.Groups["text"].Value);
            string unique = slug;
            for (int repeat = 1; !headings.Add(unique); repeat++) unique = $"{slug}-{repeat}";
        }

        known[page] = headings;
        return headings;
    }

    // GitHub's: lower case, punctuation gone, each space a hyphen. A link in a heading counts as its text.
    private static string Slug(string heading)
    {
        var slug = new StringBuilder();
        foreach (char character in LinkText().Replace(heading.Trim(), "${text}"))
        {
            if (char.IsLetterOrDigit(character) || character is '-' or '_') slug.Append(char.ToLowerInvariant(character));
            else if (character == ' ') slug.Append('-');
        }

        return slug.ToString();
    }

    [GeneratedRegex(@"```csharp\r?\n(?<code>.*?)```", RegexOptions.Singleline)]
    private static partial Regex CSharpFence();

    [GeneratedRegex(@"^[ \t]*```.*?^[ \t]*```[ \t]*$", RegexOptions.Singleline | RegexOptions.Multiline)]
    private static partial Regex AnyFence();

    [GeneratedRegex(@"\]\((?<target>[^)\s]+)\)")]
    private static partial Regex Link();

    [GeneratedRegex(@"\[(?<text>[^\]]*)\]\([^)]*\)")]
    private static partial Regex LinkText();

    [GeneratedRegex(@"^#{1,6} +(?<text>.+?) *$", RegexOptions.Multiline)]
    private static partial Regex Heading();

    [GeneratedRegex(@"^https://github\.com/schroeder247/community-zabbix/(?:blob|tree)/main/(?<path>.+)$")]
    private static partial Regex RepositoryLink();
}
