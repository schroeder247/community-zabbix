// This Source Code Form is subject to the terms of the Mozilla Public
// License, v. 2.0. Copyright (c) 2026 schroeder247. SPDX-License-Identifier: MPL-2.0
using System.Text.RegularExpressions;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;

namespace Community.Zabbix.PowerShell.Tests;

/// <summary>
/// The module conventions a reflection check cannot see, read from the hand-written command source of the three
/// modules (comments stripped): what a parameter does, where a call may throw, how a prompt reads.
/// </summary>
public sealed partial class SourceConformanceTests
{
    /// <summary>Every hand-written C# file of the three module projects, without its comment lines.</summary>
    private static IReadOnlyList<string> Sources { get; } = [.. ModuleUnderTest.All
        .SelectMany(module => Directory.EnumerateFiles(module.ProjectDirectory, "*.cs", SearchOption.AllDirectories))
        .Where(path => !path.Contains($"{Path.DirectorySeparatorChar}obj{Path.DirectorySeparatorChar}", StringComparison.Ordinal)
            && !path.Contains($"{Path.DirectorySeparatorChar}bin{Path.DirectorySeparatorChar}", StringComparison.Ordinal))
        .Select(path => Path.GetRelativePath(ModuleUnderTest.RepositoryRoot, path))
        .Order(StringComparer.Ordinal)];

    private static string Code(string relative)
        => string.Join('\n', File.ReadLines(Path.Combine(ModuleUnderTest.RepositoryRoot, relative)).Where(line => !line.TrimStart().StartsWith("//", StringComparison.Ordinal)));

    [Fact]
    public void The_testing_guide_states_only_the_floors_the_test_projects_pin()
    {
        // docs/TESTING.md names where a floor lives; a number it quotes (a table cell or an example's
        // --minimum-expected-tests) must be one a test project's .csproj pins, so the guide cannot drift from the build.
        string root = ModuleUnderTest.RepositoryRoot;
        HashSet<string> pinned = [.. Directory.EnumerateFiles(Path.Combine(root, "tests"), "*.csproj", SearchOption.AllDirectories)
            .SelectMany(project => FloorOption().Matches(File.ReadAllText(project)).Select(floor => floor.Groups[1].Value))];
        string guide = File.ReadAllText(Path.Combine(root, "docs", "TESTING.md"));
        string[] quoted = [.. FloorOption().Matches(guide).Concat(FloorCell().Matches(guide)).Select(floor => floor.Groups[1].Value)];

        Assert.NotEmpty(pinned);
        Assert.All(quoted, floor => Assert.Contains(floor, pinned));
    }

    [Fact]
    public void Every_lab_image_is_pinned_by_digest_and_every_lab_server_names_its_line_of_the_sources_lock()
    {
        // A tag on Docker Hub moves; a digest does not. And each lab pod's zabbix-release label must be the release
        // tools/zabbix-sources.lock extracts that version's model from, so the live matrix tests the model it claims to.
        string root = ModuleUnderTest.RepositoryRoot;
        Dictionary<string, string> locked = File.ReadLines(Path.Combine(root, "tools", "zabbix-sources.lock"))
            .Select(line => line.Split(' ')).Where(columns => columns.Length == 3 && char.IsAsciiDigit(columns[0][0]))
            .ToDictionary(columns => columns[0], columns => columns[1]);
        string[] pods = Directory.GetFiles(Path.Combine(root, "tests", "lab"), "*.yaml");

        Assert.NotEmpty(pods);
        List<string> versions = [];
        foreach (string pod in pods)
        {
            string text = File.ReadAllText(pod);
            Assert.All(LabImage().Matches(text), image => Assert.Contains("@sha256:", image.Groups[1].Value, StringComparison.Ordinal));
            if (LabVersion().Match(text) is { Success: true } version)
            {
                versions.Add(version.Groups[1].Value);
                Assert.True(locked.TryGetValue(version.Groups[1].Value, out string? release), $"{Path.GetFileName(pod)}: no lock line for {version.Groups[1].Value}");
                Assert.Equal(release, LabRelease().Match(text).Groups[1].Value);
            }
        }

        Assert.Equal(locked.Keys.Order(StringComparer.Ordinal), versions.Order(StringComparer.Ordinal));
    }

    [GeneratedRegex(@"^\s*image: (\S+)", RegexOptions.Multiline)]
    private static partial Regex LabImage();

    [GeneratedRegex("zabbix-version: \"([^\"]+)\"")]
    private static partial Regex LabVersion();

    [GeneratedRegex("zabbix-release: \"([^\"]+)\"")]
    private static partial Regex LabRelease();

    [GeneratedRegex(@"--minimum-expected-tests (\d+)")]
    private static partial Regex FloorOption();

    [GeneratedRegex(@"\| (\d{3,}) \|\s*$", RegexOptions.Multiline)]
    private static partial Regex FloorCell();

    [Fact]
    public void Every_Force_parameter_has_an_effect()
    {
        // A -Force a command accepts and ignores is a switch a script relies on for nothing: it must reach
        // ShouldProcessUnlessForced, ShouldChange, or the operation itself.
        string[] ignored = [.. Sources.Where(source => Code(source) is var code
            && code.Contains("SwitchParameter Force ", StringComparison.Ordinal) && ForceWord().Count(code) < 2)];

        Assert.Empty(ignored);
    }

    [Fact]
    public void Every_confirmation_names_the_kind_of_change()
    {
        // "Remove host" on "host 'web-01' (10084)", never a bare "Remove" or "Set": the action names what changes.
        string[] bare = [.. Sources.SelectMany(source => BareAction().Matches(Code(source)).Select(prompt => $"{source}: {prompt.Value}"))];

        Assert.Empty(bare);
    }

    [Fact]
    public void File_system_calls_live_in_the_document_file_helper_or_off_the_pipeline_thread()
    {
        // A File.* call on the pipeline thread throws a bare IOException out of ProcessRecord; ZabbixDocumentFile turns
        // it into an error record. The others: the assembly and native-library resolvers (not a command), the script
        // runspace's manifest probe, a connector stream read inside TryRun, and a key file read by -Psk's transformation
        // (a binding: its failure is the engine's ParameterArgumentTransformationError).
        string[] allowed = ["ZabbixDocumentFile.cs", "ZabbixModuleDependencies.cs", "ZabbixModulePlatformAssemblies.cs", "ZabbixScriptRunspace.cs", "ReadZbxConnectorStreamCommand.cs", "ZabbixPskAttribute.cs"];
        string[] offenders = [.. Sources.Where(source => FileSystemCall().IsMatch(Code(source)) && !allowed.Contains(Path.GetFileName(source)))];

        Assert.Empty(offenders);
    }

    [Fact]
    public void No_command_throws_from_its_pipeline_methods()
    {
        // An exception thrown from BeginProcessing/ProcessRecord/EndProcessing reaches the user as the engine's
        // CmdletInvocationException with no category or error id of ours: a failure there is WriteError or
        // ThrowTerminatingError. A throw inside a lambda or local function (a TryRun operation) is the operation's.
        string[] offenders = [.. Sources.SelectMany(source =>
            CSharpSyntaxTree.ParseText(File.ReadAllText(Path.Combine(ModuleUnderTest.RepositoryRoot, source))).GetRoot()
                .DescendantNodes().OfType<MethodDeclarationSyntax>()
                .Where(method => method.Identifier.Text is "BeginProcessing" or "ProcessRecord" or "EndProcessing")
                .SelectMany(method => method.DescendantNodes(node => node is not (LambdaExpressionSyntax or LocalFunctionStatementSyntax or AnonymousMethodExpressionSyntax))
                    .Where(node => node is ThrowStatementSyntax or ThrowExpressionSyntax)
                    .Select(node => $"{source}:{node.GetLocation().GetLineSpan().StartLinePosition.Line + 1}: {node}")))];

        Assert.Empty(offenders);
    }

    [Fact]
    public void Every_terminating_error_is_in_the_modules_decision_table()
    {
        // The module README's "Terminating or not" table names the type of every command or base that ends a command
        // with ThrowTerminatingError; a new one is a decision the table has to record.
        string readme = File.ReadAllText(Path.Combine(ModuleUnderTest.RepositoryRoot, "docs", "design", "powershell-module.md"));
        string table = readme[readme.IndexOf("## Terminating or not", StringComparison.Ordinal)..readme.IndexOf("## What the hand-written commands share", StringComparison.Ordinal)];
        string[] unrecorded = [.. Sources
            .Where(source => Code(source).Contains("ThrowTerminatingError(", StringComparison.Ordinal))
            .Select(source => Path.GetFileNameWithoutExtension(source)!)
            .Where(type => !table.Contains(type, StringComparison.Ordinal) && !table.Contains(Command(type), StringComparison.Ordinal))];

        Assert.Empty(unrecorded);
    }

    // NewZbxNetworkDiscoveryCheckCommand -> New-ZbxNetworkDiscoveryCheck.
    private static string Command(string type) => VerbNoun().Replace(type.EndsWith("Command", StringComparison.Ordinal) ? type[..^"Command".Length] : type, "$1-$2");

    [GeneratedRegex("^([A-Z][a-z]+)(Zbx.*)$")]
    private static partial Regex VerbNoun();

    [Fact]
    public void Every_command_that_calls_a_changing_api_method_supports_ShouldProcess()
    {
        // A command that creates, changes, deletes, executes or imports on the server is a state change whatever its
        // verb (Invoke-ZbxDiagnostic creates a task): -WhatIf must preview it.
        string[] offenders = [.. Sources
            .Where(source => Code(source) is var code && code.Contains("[Cmdlet(", StringComparison.Ordinal) && ChangingCall().IsMatch(code)
                && !code.Contains("SupportsShouldProcess = true", StringComparison.Ordinal))];

        Assert.Empty(offenders);
    }

    [GeneratedRegex(@"\.Api\.\w+\.(Create|Update|Delete|Execute|Import|Push|Propagate|Unblock|ResetTotp|Provision|MassAdd|MassRemove|MassUpdate|Clear)Async\(")]
    private static partial Regex ChangingCall();

    [GeneratedRegex(@"(?<![\w.])(System\.IO\.)?(File|Directory)\.[A-Z]\w*\(")]
    private static partial Regex FileSystemCall();

    [GeneratedRegex(@"\bForce\b")]
    private static partial Regex ForceWord();

    [GeneratedRegex("""(ShouldProcess|ShouldProcessUnlessForced|Confirmed|TryConfirm|ShouldChange)\([^;]*?,\s*"[A-Z][a-z]+"\s*[,)]""")]
    private static partial Regex BareAction();

    [Fact]
    public void Every_written_error_id_follows_one_scheme()
    {
        // FullyQualifiedErrorId is what a script branches on: one PascalCase scheme, Zabbix<Layer>.<Kind> or one word for
        // the module's own rules (ZabbixErrors), never snake_case or a CLR type name.
        string[] ids = [.. Sources.SelectMany(source => WrittenErrorId().Matches(Code(source)).Select(match => match.Groups["id"].Value))];
        string[] offenders = [.. ids.Where(id => !Regex.IsMatch(id, ApiAndDiagnosticsCommandsTests.ErrorIdPattern)).Distinct(StringComparer.Ordinal)];
        // An engine id the module reproduces is the engine's, not the module's: a Get-'s hidden InputObject writes
        // InputObjectNotBound (ZabbixGetCmdlet.EngineInputNotBound) through a constant, which this scan does not see.

        Assert.NotEmpty(ids);
        Assert.Empty(offenders);
    }

    [GeneratedRegex("""(ErrorRecord\([^;]*?,\s*|ZabbixParameterException\(\s*)"(?<id>[^"]+)"\s*,""")]
    private static partial Regex WrittenErrorId();
}
