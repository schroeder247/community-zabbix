// This Source Code Form is subject to the terms of the Mozilla Public
// License, v. 2.0. Copyright (c) 2026 schroeder247. SPDX-License-Identifier: MPL-2.0
using System.Xml.Linq;
using Microsoft.Build.Framework;
using Microsoft.CodeAnalysis.CSharp.Syntax;
// Not `using Microsoft.Build.Utilities;`: ImplicitUsings brings in System.Threading.Tasks, whose Task type would
// then collide with Microsoft.Build.Utilities.Task (CS0104) on the base-class reference below.
using MSBuildTask = Microsoft.Build.Utilities.Task;

namespace Community.Zabbix.PowerShell.HelpGen;

/// <summary>
/// The MSBuild task the module build's <c>ZabbixBuildHelp</c> target runs after <c>CoreCompile</c>: reads every
/// <c>[Cmdlet]</c> class in <see cref="Sources"/> (<see cref="CommandSourceParser"/>), and writes the resulting MAML
/// help XML to every path in <see cref="OutputPaths"/> and one reference page per command to
/// <see cref="ReferenceDirectory"/> — each file only when its content changed (<see cref="GeneratedFile"/>), so a build
/// that changes no command leaves every timestamp alone.
/// </summary>
public sealed class ZabbixBuildHelpTask : MSBuildTask
{
    /// <summary>The cmdlet source files to read: hand-written <c>Commands/*.cs</c> and the generator's <c>obj/generated/**/*.g.cs</c>,
    /// the latter with the metadata <c>Generated="true"</c> (<see cref="GeneratedExamplesPath"/>).</summary>
    [Required]
    public ITaskItem[] Sources { get; set; } = [];

    /// <summary>The files declaring the cmdlet base classes (<c>ZabbixCmdlet.cs</c>, <c>ZabbixServerCmdlet.cs</c>,
    /// <c>ZabbixCrudCmdlets.cs</c>): <c>-Connection</c>, and the CRUD bases' own <c>-Id</c>/<c>-Filter</c>/<c>-Limit</c>/
    /// <c>-Force</c>, which a derived <c>[Cmdlet]</c> class inherits rather than redeclares.</summary>
    [Required]
    public ITaskItem[] BaseClassSources { get; set; } = [];

    /// <summary>The help scripts, <c>Examples/&lt;Verb-Noun&gt;.ps1</c> (<see cref="HelpScriptParser"/>): a command's
    /// <c>.SYNOPSIS</c>, <c>.DESCRIPTION</c> and examples. A file whose name is not an exported cmdlet, a malformed one, or
    /// a command left with no example from either a script or the generator fails the build.</summary>
    public ITaskItem[] ExampleSources { get; set; } = [];

    /// <summary>Every path the built MAML document is written to.</summary>
    [Required]
    public ITaskItem[] OutputPaths { get; set; } = [];

    /// <summary>
    /// The folder the per-command reference pages (<c>&lt;Verb-Noun&gt;.md</c>) are written to: the build's
    /// <c>reference/</c> folder under the intermediate output (never the source tree), which docfx publishes at <see cref="OnlineBase"/>.
    /// </summary>
    [Required]
    public string ReferenceDirectory { get; set; } = string.Empty;

    /// <summary>
    /// Where <see cref="ReferenceDirectory"/> is read online (a URL ending in <c>/</c>): each command's MAML carries
    /// <c>&lt;base&gt;&lt;Verb-Noun&gt;.md</c> as its first related link, which is what <c>Get-Help -Online</c> opens.
    /// </summary>
    [Required]
    public string OnlineBase { get; set; } = string.Empty;

    /// <summary>
    /// The compiler's XML documentation files beside the module (<c>&lt;assembly&gt;.xml</c>): what the types its commands
    /// take and write are, for INPUTS and OUTPUTS (<see cref="TypeSummaries"/>).
    /// </summary>
    public ITaskItem[] DocumentationFiles { get; set; } = [];

    /// <summary>
    /// Where the generated commands' examples are written as JSON (<see cref="GeneratedExamples"/>) for the live examples
    /// run, <c>tools/Test-ZbxHelpExamples.ps1</c>; nothing is written when empty.
    /// </summary>
    public string GeneratedExamplesPath { get; set; } = string.Empty;

    /// <summary>The module's name (Community.Zabbix, Community.Zabbix.Agent, Community.Zabbix.Proxy): the reference index's title.</summary>
    [Required]
    public string ModuleName { get; set; } = string.Empty;

    /// <summary>
    /// The concept topic the help names as each command's related link and the reference index names: the module's own
    /// <c>about_</c> topic when it ships one, <c>about_Community.Zabbix</c> otherwise.
    /// </summary>
    public string ConceptTopic { get; set; } = "about_Community.Zabbix";

    /// <inheritdoc/>
    public override bool Execute()
    {
        IReadOnlyList<CmdletDoc> parsed = CommandSourceParser.Cmdlets(Parse(Sources), Parse(BaseClassSources));
        Dictionary<string, HelpScript> scripts = Scripts(parsed);
        IReadOnlyList<CmdletDoc> cmdlets = [.. parsed.Select(cmdlet => scripts.TryGetValue(cmdlet.Name, out HelpScript? script) ? cmdlet.With(script) : cmdlet)];
        foreach (CmdletDoc cmdlet in cmdlets.Where(cmdlet => cmdlet.Examples.Count == 0))
            Log.LogError($"{cmdlet.Name} has no example: write Examples/{cmdlet.Name}.ps1 (a generated command takes the generator's).");
        Dictionary<string, string> types = TypeSummaries.Load(DocumentationFiles.Select(file => file.ItemSpec));
        int maml = WriteMaml(cmdlets, types);
        int pages = WriteReferencePages(cmdlets, types);
        if (GeneratedExamplesPath.Length > 0) WriteGeneratedExamples(scripts);

        Log.LogMessage(MessageImportance.High, $"Zabbix help: {cmdlets.Count} cmdlet(s) documented in MAML and in {pages} reference page(s), {scripts.Count} with a help script; {maml} MAML file(s) changed.");
        return !Log.HasLoggedErrors;
    }

    private static CompilationUnitSyntax[] Parse(ITaskItem[] sources)
        => [.. sources.Select(source => CommandSourceParser.Parse(File.ReadAllText(source.ItemSpec)))];

    // The help scripts by command; a file named for no command of this module, a malformed file or one that neither
    // describes the command nor shows an example fails the build.
    private Dictionary<string, HelpScript> Scripts(IReadOnlyList<CmdletDoc> cmdlets)
    {
        var scripts = new Dictionary<string, HelpScript>(StringComparer.OrdinalIgnoreCase);
        var known = new HashSet<string>(cmdlets.Select(c => c.Name), StringComparer.OrdinalIgnoreCase);
        foreach (ITaskItem source in ExampleSources)
        {
            string cmdletName = Path.GetFileNameWithoutExtension(source.ItemSpec);
            if (!known.Contains(cmdletName))
            {
                Log.LogError($"{source.ItemSpec}: '{cmdletName}' is not a cmdlet of this module; a help script is named for the cmdlet it documents.");
                continue;
            }
            List<string> errors = [];
            HelpScript script = HelpScriptParser.Parse(File.ReadAllText(source.ItemSpec), errors);
            foreach (string error in errors) Log.LogError($"{source.ItemSpec}: {error}");
            if (errors.Count == 0 && script.Examples.Count == 0) Log.LogError($"{source.ItemSpec}: no #region example in the file.");
            scripts[cmdletName] = script;
        }
        return scripts;
    }

    // The generated commands' own examples, shown or replaced by a help script, for the live examples run.
    private void WriteGeneratedExamples(Dictionary<string, HelpScript> scripts)
    {
        ITaskItem[] generated = [.. Sources.Where(source => string.Equals(source.GetMetadata("Generated"), "true", StringComparison.OrdinalIgnoreCase))];
        IReadOnlyList<CmdletDoc> commands = CommandSourceParser.Cmdlets(Parse(generated), Parse(BaseClassSources));
        GeneratedFile.Write(GeneratedExamplesPath, GeneratedExamples.Build(commands, new HashSet<string>(scripts.Keys, StringComparer.OrdinalIgnoreCase)));
    }

    // The MAML document, written to every output path whose content it changes.
    private int WriteMaml(IReadOnlyList<CmdletDoc> cmdlets, IReadOnlyDictionary<string, string> types)
    {
        XDocument document = MamlWriter.Build(cmdlets, OnlineBase, types, ConceptTopic);
        return OutputPaths.Count(output => GeneratedFile.Write(output.ItemSpec, document));
    }

    // The reference pages: one per command, the same model, and nothing else in the folder (a page for a command that
    // no longer exists is deleted, so the folder never drifts from the module). Returns the number of pages.
    private int WriteReferencePages(IReadOnlyList<CmdletDoc> cmdlets, IReadOnlyDictionary<string, string> types)
    {
        Directory.CreateDirectory(ReferenceDirectory);
        var written = new HashSet<string>(StringComparer.Ordinal);
        foreach ((string name, string page) in MarkdownWriter.Build(cmdlets, OnlineBase, ModuleName, types, ConceptTopic))
        {
            string path = Path.Combine(ReferenceDirectory, name);
            written.Add(Path.GetFullPath(path));
            GeneratedFile.Write(path, page);
        }
        foreach (string stale in Directory.EnumerateFiles(ReferenceDirectory, "*.md").Where(f => !written.Contains(Path.GetFullPath(f))))
            File.Delete(stale);
        return written.Count;
    }
}
