// This Source Code Form is subject to the terms of the Mozilla Public
// License, v. 2.0. Copyright (c) 2026 schroeder247. SPDX-License-Identifier: MPL-2.0
using System.Text.RegularExpressions;
using Microsoft.Build.Framework;
// Not `using Microsoft.Build.Utilities;`: ImplicitUsings brings in System.Threading.Tasks, whose Task type would
// then collide with Microsoft.Build.Utilities.Task (CS0104) on the base-class reference below.
using MSBuildTask = Microsoft.Build.Utilities.Task;

namespace Community.Zabbix.PowerShell.HelpGen;

/// <summary>
/// The MSBuild task the module build's <c>ZabbixGenerateManifest</c> target runs after <c>CoreCompile</c>: fills the
/// hand-written manifest's <c>CmdletsToExport</c> and <c>AliasesToExport</c> with every <c>[Cmdlet]</c> class in
/// <see cref="Sources"/> and that class's own <c>[Alias]</c> names — read by <see cref="CommandSourceParser"/>, the
/// same reading the help is built from — and writes the completed manifest to every path in <see cref="OutputPaths"/>
/// whose content it changes. A parameter's <c>[Alias]</c> is the parameter's, not the command's. With
/// <see cref="RequiredVersion"/> set, every module the template's <c>RequiredModules</c> names by name is pinned to it.
/// </summary>
public sealed class ZabbixWriteManifestExportsTask : MSBuildTask
{
    /// <summary>The cmdlet source files to read: hand-written <c>Commands/*.cs</c> and the generator's <c>obj/generated/**/*.g.cs</c>.</summary>
    [Required]
    public ITaskItem[] Sources { get; set; } = [];

    /// <summary>The hand-written manifest (both export arrays empty): read, never written.</summary>
    [Required]
    public string Template { get; set; } = string.Empty;

    /// <summary>Every path the completed manifest is written to.</summary>
    [Required]
    public ITaskItem[] OutputPaths { get; set; } = [];

    /// <summary>
    /// The family's release version, empty for a prerelease. When set, each name in <c>RequiredModules</c> becomes
    /// <c>@{ ModuleName = '…'; RequiredVersion = '…' }</c>: the siblings compile against the module <c>Community.Zabbix</c>'s
    /// internals, so they load only beside the copy they were built with. A prerelease keeps the names, because the
    /// Gallery turns <c>RequiredVersion</c> into a dependency on the release version, which a prerelease does not satisfy.
    /// </summary>
    public string RequiredVersion { get; set; } = string.Empty;

    /// <inheritdoc/>
    public override bool Execute()
    {
        var cmdlets = new SortedSet<string>(StringComparer.Ordinal);
        var aliases = new SortedSet<string>(StringComparer.Ordinal);
        foreach (ITaskItem source in Sources)
        {
            foreach (CmdletDoc cmdlet in CommandSourceParser.ParseFile(CommandSourceParser.Parse(File.ReadAllText(source.ItemSpec))))
            {
                cmdlets.Add(cmdlet.Name);
                aliases.UnionWith(cmdlet.Aliases);
            }
        }

        // The template is PowerShell data, not C#: its two arrays are replaced in place, everything else kept as written.
        string manifest = File.ReadAllText(Template);
        manifest = Regex.Replace(manifest, @"CmdletsToExport\s*=\s*@\(.*?\)", Block("CmdletsToExport     ", cmdlets).Replace("$", "$$", StringComparison.Ordinal), RegexOptions.Singleline);
        manifest = Regex.Replace(manifest, @"AliasesToExport\s*=\s*@\(.*?\)", Block("AliasesToExport     ", aliases).Replace("$", "$$", StringComparison.Ordinal), RegexOptions.Singleline);
        if (RequiredVersion.Length > 0)
            manifest = Regex.Replace(manifest, @"(RequiredModules\s*=\s*)@\(([^)]*)\)", match => match.Groups[1].Value + "@(" + string.Join(", ", Regex.Matches(match.Groups[2].Value, @"'([^']+)'")
                .Select(name => "@{ ModuleName = '" + name.Groups[1].Value + "'; RequiredVersion = '" + RequiredVersion + "' }")) + ")");
        foreach (ITaskItem output in OutputPaths) GeneratedFile.Write(output.ItemSpec, manifest);

        Log.LogMessage(MessageImportance.High, $"{Path.GetFileName(Template)}: {cmdlets.Count} cmdlets, {aliases.Count} aliases.");
        return !Log.HasLoggedErrors;
    }

    private static string Block(string key, SortedSet<string> names) => names.Count > 0
        ? key + " = @(\n" + string.Join("\n", names.Select(name => "        '" + name + "'")) + "\n    )"
        : key + " = @()";
}
