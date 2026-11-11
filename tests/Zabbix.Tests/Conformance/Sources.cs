// This Source Code Form is subject to the terms of the Mozilla Public
// License, v. 2.0. Copyright (c) 2026 schroeder247. SPDX-License-Identifier: MPL-2.0
using System.Diagnostics;
using System.Globalization;
using System.Text.RegularExpressions;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;

namespace Community.Zabbix.Tests.Conformance;

/// <summary>
/// The sources the Roslyn rules read. A library's sources are the <c>*.cs</c> files under <c>src/{assembly}/</c>
/// (assembly name = project directory), its <c>obj/</c> excluded; the test sources are this project's.
/// The test compilation binds against exactly what this test run loaded (the trusted platform assemblies and the files
/// beside this assembly), so a symbol a rule asks about is the one the tests ran against.
/// </summary>
internal static class Sources
{
    private static readonly Lazy<IReadOnlyList<SyntaxTree>> LibraryTrees = new(() => [.. Libraries.All.SelectMany(library => Parse(Path.Combine(Repository.Root, "src", Libraries.Folder(library))))]);

    private static readonly Lazy<CSharpCompilation> TestCompilation = new(() => CSharpCompilation.Create(
        "Community.Zabbix.Tests.Conformance",
        Parse(Path.Combine(Repository.Root, "tests", "Zabbix.Tests")),
        References(),
        new CSharpCompilationOptions(OutputKind.DynamicallyLinkedLibrary, nullableContextOptions: NullableContextOptions.Enable)));

    public static IReadOnlyList<SyntaxTree> Library => LibraryTrees.Value;

    public static CSharpCompilation Tests => TestCompilation.Value;

    public static SyntaxTree ParseText(string text, string path) => CSharpSyntaxTree.ParseText(text, CSharpParseOptions.Default.WithLanguageVersion(LanguageVersion.Preview), path);

    private static List<SyntaxTree> Parse(string directory)
    {
        string obj = $"{Path.DirectorySeparatorChar}obj{Path.DirectorySeparatorChar}";
        string bin = $"{Path.DirectorySeparatorChar}bin{Path.DirectorySeparatorChar}";
        return [.. Directory.EnumerateFiles(directory, "*.cs", SearchOption.AllDirectories)
            .Where(file => !file.Contains(obj, StringComparison.Ordinal) && !file.Contains(bin, StringComparison.Ordinal))
            .Select(file => ParseText(File.ReadAllText(file), file))];
    }

    private static List<MetadataReference> References()
    {
        string platform = (string)AppContext.GetData("TRUSTED_PLATFORM_ASSEMBLIES")!;
        return [.. platform.Split(Path.PathSeparator)
            .Concat(Directory.EnumerateFiles(AppContext.BaseDirectory, "*.dll"))
            .DistinctBy(Path.GetFileName, StringComparer.OrdinalIgnoreCase)
            .Select(path => MetadataReference.CreateFromFile(path))];
    }
}

/// <summary>
/// A violation compile: a rule is proven by a probe that breaks it. The probe project
/// <c>tests/probes/Zabbix.Analyzers.Probe</c> (outside the test projects' folders, so the <c>[tests/Zabbix*/**.cs]</c> section of <c>.editorconfig</c> does not reach it) is library-shaped (the solution's <c>Directory.Build.*</c>, <c>.editorconfig</c>,
/// banned-symbol file and analyzer set, not the test relaxations) and is not in the solution; it is built here with the
/// same SDK, and every line of it marked <c>// expect: ID[, ID]</c> must fail the build with exactly those ids. A line
/// without a mark must not fail at all, so a probe cannot drift into proving something by accident.
/// </summary>
internal static partial class AnalyzerProbe
{
    public static string Directory { get; } = Path.Combine(Repository.Root, "tests", "probes", "Zabbix.Analyzers.Probe");

    private static readonly Lazy<Task<IReadOnlyList<ProbeError>>> Build = new(BuildAsync);

    public sealed record ProbeError(string File, int Line, string Id);

    /// <summary>The <c>// expect:</c> marks in the probe sources: file, line, id.</summary>
    public static IEnumerable<ProbeError> Expected()
        => System.IO.Directory.EnumerateFiles(Directory, "*.cs").SelectMany(file => File.ReadLines(file)
            .Select((text, index) => (Text: text, Line: index + 1))
            .Select(line => (line.Line, Mark: ExpectMark().Match(line.Text)))
            .Where(line => line.Mark.Success)
            .SelectMany(line => line.Mark.Groups["ids"].Value.Split(',', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries)
                .Select(id => new ProbeError(Path.GetFileName(file), line.Line, id))));

    /// <summary>The errors the probe build reported, once per run.</summary>
    public static Task<IReadOnlyList<ProbeError>> ErrorsAsync() => Build.Value;

    private static async Task<IReadOnlyList<ProbeError>> BuildAsync()
    {
        string dotnet = Environment.GetEnvironmentVariable("DOTNET_HOST_PATH") is { Length: > 0 } host && File.Exists(host)
            ? host
            : Path.GetFullPath(Path.Combine(System.Runtime.InteropServices.RuntimeEnvironment.GetRuntimeDirectory(), "..", "..", "..", OperatingSystem.IsWindows() ? "dotnet.exe" : "dotnet"));
        var start = new ProcessStartInfo(dotnet)
        {
            WorkingDirectory = Directory,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
        };
        foreach (string argument in (string[])["build", "Zabbix.Analyzers.Probe.csproj", "-nologo", "-v:q", "-clp:NoSummary;ErrorsOnly", "-p:TreatWarningsAsErrors=true"])
            start.ArgumentList.Add(argument);
        using Process build = Process.Start(start) ?? throw new InvalidOperationException($"could not start {dotnet}");
        Task<string> output = build.StandardOutput.ReadToEndAsync(TestContext.Current.CancellationToken);
        Task<string> error = build.StandardError.ReadToEndAsync(TestContext.Current.CancellationToken);
        await build.WaitForExitAsync(TestContext.Current.CancellationToken);
        string text = await output + await error;
        List<ProbeError> errors = [.. ErrorLine().Matches(text)
            .Select(match => new ProbeError(Path.GetFileName(match.Groups["file"].Value), int.Parse(match.Groups["line"].Value, CultureInfo.InvariantCulture), match.Groups["id"].Value))
            .Distinct()];
        if (errors.Count == 0) throw new InvalidOperationException($"the probe build reported no errors (exit {build.ExitCode}):\n{text}");
        return errors;
    }

    [GeneratedRegex(@"//\s*expect:\s*(?<ids>[A-Za-z0-9, ]+)$")]
    private static partial Regex ExpectMark();

    [GeneratedRegex(@"^(?<file>[^\s(][^(]*\.cs)\((?<line>\d+),\d+\): error (?<id>[A-Za-z]+\d+):", RegexOptions.Multiline)]
    private static partial Regex ErrorLine();
}
