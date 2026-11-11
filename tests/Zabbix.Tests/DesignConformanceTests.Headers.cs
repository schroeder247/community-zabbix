// This Source Code Form is subject to the terms of the Mozilla Public
// License, v. 2.0. Copyright (c) 2026 schroeder247. SPDX-License-Identifier: MPL-2.0
using System.Diagnostics;

namespace Community.Zabbix.Tests;

/// <summary>
/// The licence header rule (CONTRIBUTING.md, "Source file headers"): every tracked C#, PowerShell, shell, Python, YAML and
/// MSBuild file and every hand-written Markdown document starts with the MPL-2.0 header in its own comment syntax. A
/// package's own README is its nuget.org page, which shows HTML as text, so it carries none; the analyzers' release
/// tables are the analyzer's input; the code of conduct is the Contributor Covenant's text, under its own licence; the cmdlet
/// reference under <c>docs/reference/</c> is written by the build and carries its "generated, do not edit" line instead. The
/// files are the ones git tracks, so build output and an ignored checkout of the Zabbix sources are never read.
/// </summary>
/// <remarks>
/// Reading every source allocates, and <see cref="ChannelsProbeTests"/> measures the whole process's allocation, so these
/// run in the probes' collection: one after another with them, never beside them.
/// </remarks>
[Collection(nameof(ChannelsProbeTests))]
public sealed class DesignConformanceHeaderTests
{
    private static readonly string[] HeaderedExtensions = [".cs", ".ps1", ".psm1", ".psd1", ".sh", ".py", ".yml", ".csproj", ".props", ".targets"];

    /// <summary>The header's two lines; each is checked without its comment marker, which differs by file kind.</summary>
    private const string FirstLine = "This Source Code Form is subject to the terms of the Mozilla Public";
    private const string SecondLine = "License, v. 2.0. Copyright (c) 2026 schroeder247. SPDX-License-Identifier: MPL-2.0";

    /// <summary>How far down the header may start: after a <c>#!</c> line, a project's opening tag, or a comment opener.</summary>
    private const int HeaderWindow = 6;

    [Fact]
    public async Task Every_tracked_source_and_document_carries_the_licence_header()
    {
        IReadOnlyList<string> tracked = await TrackedFilesAsync();
        List<string> headered = [.. tracked.Where(IsHeadered)];

        Assert.Contains("CHANGELOG.md", tracked);   // git answered with the repository's files
        DesignConformanceTests.AssertNone(headered.Where(path => !HasHeader(File.ReadLines(Path.Combine(Repository.Root, path)).Take(HeaderWindow))));
    }

    [Fact]
    public void The_header_rule_finds_a_file_without_the_header_and_accepts_each_comment_syntax()
    {
        Assert.True(HasHeader(["// " + FirstLine, "// " + SecondLine]));
        Assert.True(HasHeader(["#!/usr/bin/env bash", "# " + FirstLine, "# " + SecondLine]));
        Assert.True(HasHeader(["<Project Sdk=\"Microsoft.NET.Sdk\">", "  <!--", "  " + FirstLine, "  " + SecondLine]));
        Assert.False(HasHeader(["#region Get a host", "Get-ZbxHost -Name 'web-01'", "#endregion"]));
        Assert.False(HasHeader(["// " + FirstLine]));

        Assert.True(IsHeadered("src/Zabbix.PowerShell/Examples/Get-ZbxHost.ps1"));
        Assert.True(IsHeadered("docs/design/architecture.md"));
        Assert.False(IsHeadered("src/Zabbix.Sdk/README.md"));
        Assert.True(IsHeadered("README.md"));
        Assert.True(IsHeadered(".github/workflows/ci.yml"));
        Assert.False(IsHeadered("CODE_OF_CONDUCT.md"));
        Assert.False(IsHeadered("schema/zabbix-api.json"));
        Assert.False(IsHeadered("docs/reference/Community.Zabbix/Get-ZbxHost.md"));
        Assert.True(IsHeadered("docs/guides/api-client.md"));
    }

    /// <summary>
    /// A file of a headered kind: by extension, or a Markdown document but a package's page, an adopted text and the
    /// generated cmdlet reference.
    /// </summary>
    private static bool IsHeadered(string path)
        => HeaderedExtensions.Contains(Path.GetExtension(path), StringComparer.OrdinalIgnoreCase)
            || (path.EndsWith(".md", StringComparison.OrdinalIgnoreCase) && !IsPackagePage(path)
                && !path.StartsWith("docs/reference/", StringComparison.Ordinal)
                && Path.GetFileName(path) is not "CODE_OF_CONDUCT.md" && !Path.GetFileName(path).StartsWith("AnalyzerReleases.", StringComparison.Ordinal));

    /// <summary>A package's README, packed as its nuget.org page: <c>src/{project}/README.md</c>.</summary>
    private static bool IsPackagePage(string path)
        => path.StartsWith("src/", StringComparison.Ordinal) && path.Count(c => c == '/') == 2 && path.EndsWith("/README.md", StringComparison.Ordinal);

    private static bool HasHeader(IEnumerable<string> lines)
    {
        List<string> head = [.. lines.Select(line => line.Trim().TrimStart('/', '#').Trim())];
        int first = head.IndexOf(FirstLine);
        return first >= 0 && first + 1 < head.Count && head[first + 1] == SecondLine;
    }

    /// <summary>The files git tracks, relative to the repository root with forward slashes.</summary>
    internal static async Task<IReadOnlyList<string>> TrackedFilesAsync()
    {
        var start = new ProcessStartInfo("git")
        {
            WorkingDirectory = Repository.Root,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
        };
        foreach (string argument in (string[])["ls-files", "-z"])
            start.ArgumentList.Add(argument);
        using Process git = Process.Start(start) ?? throw new InvalidOperationException("Could not start git.");
        Task<string> output = git.StandardOutput.ReadToEndAsync(TestContext.Current.CancellationToken);
        Task<string> error = git.StandardError.ReadToEndAsync(TestContext.Current.CancellationToken);
        await git.WaitForExitAsync(TestContext.Current.CancellationToken);
        if (git.ExitCode != 0) throw new InvalidOperationException($"git ls-files failed with exit code {git.ExitCode}: {await error}");
        _ = await error;
        // A file deleted in the working tree but not yet from the index is still listed; it has nothing to check.
        return [.. (await output).Split('\0', StringSplitOptions.RemoveEmptyEntries)
            .Where(path => File.Exists(Path.Combine(Repository.Root, path)))];
    }
}
