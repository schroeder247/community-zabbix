// This Source Code Form is subject to the terms of the Mozilla Public
// License, v. 2.0. Copyright (c) 2026 schroeder247. SPDX-License-Identifier: MPL-2.0
using System.Text.RegularExpressions;
using Community.Zabbix.Tests.Conformance;

namespace Community.Zabbix.Tests;

/// <summary>
/// The comment rule (docs/adr/0005-documentation-set.md): a comment in <c>src/</c>, <c>tools/</c> or <c>tests/</c> explains
/// the code to a maintainer (in a test, the behaviour under test and why it matters) or, in a public summary, to the API's
/// user. It carries no review or finding id, review branch, session date, wave, section number of the design standard,
/// pointer to a planning document or path on a developer's machine;
/// it keeps the reason and the measured result. A C# file is checked in its comments only (code is not prose); every other
/// source file (project files, manifests, scripts, the package readmes) is checked line by line.
/// </summary>
/// <remarks>
/// Reading every source allocates, and <see cref="ChannelsProbeTests"/> measures the whole process's allocation, so these
/// run in the probes' collection: one after another with them, never beside them.
/// </remarks>
[Collection(nameof(ChannelsProbeTests))]
public sealed partial class DesignConformanceCommentTests
{
    private static readonly string[] CommentedExtensions = [".cs", ".csproj", ".props", ".targets", ".psd1", ".psm1", ".ps1", ".ps1xml", ".py", ".sh", ".md"];

    /// <summary>
    /// Lines that match a pattern and are not process: <c>relative path</c> and a substring of the line. Empty: no
    /// Zabbix-source citation collides with a pattern.
    /// </summary>
    private static readonly (string Path, string Text)[] ProcessReferenceAllowed = [];

    /// <summary>
    /// This file, whose probe holds every pattern on purpose: the probe's code is a raw string, which the comment reader
    /// cannot tell from a comment.
    /// </summary>
    private const string RuleProbe = "tests/Zabbix.Tests/DesignConformanceTests.Comments.cs";

    [Fact]
    public void Source_comments_carry_no_process_references()
        => DesignConformanceTests.AssertNone(ProcessReferences(CommentedSources("src", "tools")));

    /// <summary>
    /// The same rule over the tests, reported once per file and reference (a waiver names a file's reference, not a line
    /// that moves with every edit).
    /// </summary>
    [Fact]
    public void Test_comments_carry_no_process_references()
        => DesignConformanceTests.AssertNone(Waivers.Outstanding(nameof(Test_comments_carry_no_process_references),
            ProcessReferences(CommentedSources("tests").Where(source => source.Path != RuleProbe)).Select(WithoutLine).Distinct()));

    /// <summary>
    /// The tests' code outside comments (their string literals: assertion messages, test data) under a tighter pattern:
    /// review and finding ids, review branches, the design standard's section numbers and the planning documents, but not
    /// dates, waves or short ids, which test data carries legitimately (an item delay's instants). A regular expression's
    /// own pattern names what it rejects and is not checked.
    /// </summary>
    [Fact]
    public void Test_string_literals_carry_no_process_references()
        => DesignConformanceTests.AssertNone(LiteralProcessReferences(CommentedSources("tests").Where(source => source.Path.EndsWith(".cs", StringComparison.Ordinal) && source.Path != RuleProbe)));

    [Fact]
    public void No_summary_opens_with_a_bold_label()
        => Assert.Empty(BoldLabelSummaries(CommentedSources("src", "tools", "tests").Where(source => source.Path.EndsWith(".cs", StringComparison.Ordinal) && source.Path != RuleProbe)));

    [Fact]
    public void The_comment_rules_catch_each_process_reference_and_a_bold_label()
    {
        string code = """
            namespace Community.Zabbix;
            /// <summary>
            /// <b>The sample.</b> Measured 2026-09-23 (review r-sdk-ergo SE12, ADV-PS-9).
            /// </summary>
            public sealed class A
            {
                // general IV.7; COMPLIANCE-PLAN Wave 6.4; see docs/STATUS.md and /home/someone/code/x.
                public string Literal = "PI-15 2026-09-27";
                /// <summary>Gets the value. A citation of proxy.c:120 is not process.</summary>
                public int B { get; }
            }
            """;
        List<string> found = ProcessReferences([new("src/X/A.cs", code)]);

        Assert.Equal(
            [
                "src/X/A.cs:3: Measured 2026-09-23", "src/X/A.cs:3: review r-", "src/X/A.cs:3: SE12", "src/X/A.cs:3: ADV-PS-9",
                "src/X/A.cs:7: general IV.7", "src/X/A.cs:7: COMPLIANCE-PLAN", "src/X/A.cs:7: Wave 6", "src/X/A.cs:7: STATUS.md", "src/X/A.cs:7: /home/",
            ],
            found);
        Assert.Equal(["src/X/A.cs:2"], BoldLabelSummaries([new("src/X/A.cs", code)]));
        Assert.Equal(["tools/run.sh:1: 2026-10-05"], ProcessReferences([new("tools/run.sh", "# rebuilt 2026-10-05\necho ok")]));
        Assert.Equal(
            ["src/X/B.cs:1: Wave 4", "src/X/B.cs:1: conformance C19", "src/X/B.cs:1: (C3)", "src/X/B.cs:1: LIVE", "src/X/B.cs:1: ADV-STYLE-1", "src/X/B.cs:1: E-B9"],
            ProcessReferences([new("src/X/B.cs", "// Wave 4: merged (conformance C19), whole (C3); LIVE on 8.0; ADV-STYLE-1 E-B9.")]));
        Assert.Equal(["BannedSymbols.txt:1: general V.4"], ProcessReferences([new("BannedSymbols.txt", "P:System.DateTime.Now;Time is injected (general V.4)")]));
        Assert.Equal(
            ["tests/X/C.cs:1: Finding 20", "tests/X/C.cs:5: review r-"],
            LiteralProcessReferences([new("tests/X/C.cs", string.Join('\n',
                "Assert.False(x, \"x is mandatory (Finding 20)\"); // a comment's own rule",
                "[InlineData(\"2026-09-21 10:00:00 +00:00\", \"SE12\")]",
                "[GeneratedRegex(@\"COMPLIANCE-PLAN|\\bFinding \\d\")]",
                "string text = \"\"\"",
                "    see review r-sdk",
                "    \"\"\";"))]));
    }

    private sealed record Source(string Path, string Text);

    /// <summary>The build's own files at the root, which every library is built under and which a reader reads as prose.</summary>
    private static readonly string[] RootBuildFiles = [".editorconfig", "BannedSymbols.txt", "Directory.Build.props", "Directory.Build.targets"];

    /// <summary>The commented files under <paramref name="folders"/>; with <c>src</c>, the root build files too.</summary>
    private static List<Source> CommentedSources(params string[] folders)
        => [.. folders
            .SelectMany(folder => SourceFiles(Path.Combine(Repository.Root, folder)))
            .Concat(folders.Contains("src") ? RootBuildFiles.Select(file => Path.Combine(Repository.Root, file)).Where(File.Exists) : [])
            .Select(file => new Source(Path.GetRelativePath(Repository.Root, file).Replace('\\', '/'), File.ReadAllText(file)))];

    /// <summary>The commented files under <paramref name="directory"/>, never descending into build output.</summary>
    private static IEnumerable<string> SourceFiles(string directory)
        => Directory.EnumerateFiles(directory)
            .Where(file => CommentedExtensions.Contains(Path.GetExtension(file), StringComparer.OrdinalIgnoreCase))
            .Concat(Directory.EnumerateDirectories(directory)
                .Where(child => Path.GetFileName(child) is not ("bin" or "obj" or "__pycache__"))
                .SelectMany(SourceFiles));

    private static List<string> ProcessReferences(IEnumerable<Source> sources)
        => [.. sources.SelectMany(source => ProseLines(source)
            .SelectMany(line => ProcessReference().Matches(line.Text)
                .Where(match => !ProcessReferenceAllowed.Any(allowed => allowed.Path == source.Path && line.Text.Contains(allowed.Text, StringComparison.Ordinal)))
                .Select(match => $"{source.Path}:{line.Number}: {match.Value}")))];

    private static List<string> LiteralProcessReferences(IEnumerable<Source> sources)
        => [.. sources.SelectMany(source => CodeLines(source)
            .SelectMany(line => LiteralProcessReference().Matches(line.Text)
                .Select(match => $"{source.Path}:{line.Number}: {match.Value}")))];

    /// <summary>
    /// Each line of a C# file up to its line comment, or nothing where it is inside a block comment, less the lines that
    /// declare a regular expression.
    /// </summary>
    private static IEnumerable<(int Number, string Text)> CodeLines(Source source)
    {
        string[] lines = source.Text.Split('\n');
        bool block = false;
        for (int i = 0; i < lines.Length; i++)
        {
            string line = lines[i];
            if (block)
            {
                block = !line.Contains("*/", StringComparison.Ordinal);
                continue;
            }
            if (CommentStart().Match(line) is { Success: true } comment) line = line[..comment.Index];
            int open = line.IndexOf("/*", StringComparison.Ordinal);
            if (open >= 0)
            {
                block = !line.AsSpan(open).Contains("*/", StringComparison.Ordinal);
                line = line[..open];
            }
            if (!line.Contains("Regex(", StringComparison.Ordinal)) yield return (i + 1, line);
        }
    }

    // "path:line: match" as "path: match".
    private static string WithoutLine(string reference)
    {
        int path = reference.IndexOf(':', StringComparison.Ordinal);
        int line = reference.IndexOf(':', path + 1);
        return reference[..path] + reference[line..];
    }

    private static List<string> BoldLabelSummaries(IEnumerable<Source> sources)
        => [.. sources.SelectMany(source => BoldLabelSummary().Matches(source.Text)
            .Select(match => $"{source.Path}:{source.Text.AsSpan(0, match.Index).Count('\n') + 1}"))];

    /// <summary>
    /// The text a reader reads as prose: in a C# file, each line from its comment marker on (<c>//</c>, <c>///</c>, or
    /// a line inside a <c>/* */</c> block); in any other file, every line.
    /// </summary>
    private static IEnumerable<(int Number, string Text)> ProseLines(Source source)
    {
        string[] lines = source.Text.Split('\n');
        if (!source.Path.EndsWith(".cs", StringComparison.Ordinal))
        {
            for (int i = 0; i < lines.Length; i++)
                yield return (i + 1, lines[i]);
            yield break;
        }

        bool block = false;
        for (int i = 0; i < lines.Length; i++)
        {
            string line = lines[i];
            if (block)
            {
                block = !line.Contains("*/", StringComparison.Ordinal);
                yield return (i + 1, line);
                continue;
            }

            int comment = CommentStart().Match(line) is { Success: true } match ? match.Index : -1;
            int open = line.IndexOf("/*", StringComparison.Ordinal);
            if (open >= 0 && (comment < 0 || open < comment))
            {
                block = !line.AsSpan(open).Contains("*/", StringComparison.Ordinal);
                yield return (i + 1, line[open..]);
            }
            else if (comment >= 0)
            {
                yield return (i + 1, line[comment..]);
            }
        }
    }

    // A line comment's marker: "//" that is not the "://" of a URL.
    [GeneratedRegex(@"(?<!:)//")]
    private static partial Regex CommentStart();

    // Review and finding ids (ADV-PS-9, PI-15, SE12, ZS16, PP4, NP6, EX-1-05, Finding 12), review branches and passes
    // (review r-sdk-ergo, parity7), session dates and measurements, the design standard's section numbers (general IV.7),
    // the planning documents and their waves, the documents that left main (ADR 0005), and paths on a developer's machine.
    [GeneratedRegex(@"review r-|(?i:measured (?:on |at the )?)20\d\d-\d\d-\d\d|\bADV-[A-Z]+-\d+|\b(?:PI|RI|SE|ZS|PP|NP|EX|PWSH)-?\d+\b|\bFinding \d+|\b(?:parity|pwfix)\d+|\bgeneral [IVX]+\.\d+|COMPLIANCE-PLAN|\bWave [0-9A-E]|\b20\d\d-\d\d-\d\d\b|PHILOSOPHY-APPLIED|ARCHITECTURE-TARGET|PLATFORM-SURVEY|CSharp-Philosophy-Conformance|PARITY(?:-REPORT|-PARAMETERS)?\.md|\bSTATUS\.md|review-20\d\d|/home/|~/code/|(?i:\bwave\b)|\bconformance C\d+|\(C\d+\)|\bLIVE\b|\bADV-STYLE-\d+|\b[A-Z]{1,3}-B\d+\b")]
    private static partial Regex ProcessReference();

    // The part of ProcessReference that test data does not carry legitimately: no dates, waves, short ids or paths.
    [GeneratedRegex(@"review r-|\bADV-[A-Z]+-\d+|\bFinding \d+|\b(?:parity|pwfix)\d+|\bgeneral [IVX]+\.\d+|COMPLIANCE-PLAN|PHILOSOPHY-APPLIED|ARCHITECTURE-TARGET|PLATFORM-SURVEY|CSharp-Philosophy-Conformance|PARITY(?:-REPORT|-PARAMETERS)?\.md|\bSTATUS\.md|review-20\d\d|\bconformance C\d+")]
    private static partial Regex LiteralProcessReference();

    // A summary whose first text is a bold label: "<summary>\n/// <b>The root:</b> …" or "<summary><b>…".
    [GeneratedRegex(@"<summary>\s*(?:\r?\n[ \t]*///[ \t]*)?<b>")]
    private static partial Regex BoldLabelSummary();
}
