// This Source Code Form is subject to the terms of the Mozilla Public
// License, v. 2.0. Copyright (c) 2026 schroeder247. SPDX-License-Identifier: MPL-2.0
using System.Management.Automation;
using System.Text.RegularExpressions;
using System.Xml.Linq;

namespace Community.Zabbix.PowerShell.Tests;

/// <summary>
/// What a user reading <c>Get-Help</c> sees: no maintainers' notes in the text, one syntax entry per parameter set with
/// each parameter's type, the pipeline input each parameter takes, and the hand-written examples of
/// <c>Examples/&lt;Verb-Noun&gt;.ps1</c> in the help, not the templated ones.
/// </summary>
public sealed partial class HelpReadabilityTests
{
    private static readonly XNamespace Command = "http://schemas.microsoft.com/maml/dev/command/2004/10";
    private static readonly XNamespace Maml = "http://schemas.microsoft.com/maml/2004/10";

    private static string HelpFile { get; } = ModuleUnderTest.Zabbix.HelpFile;

    // The words that only mean something to the module's maintainers: the internal documents, planning and finding
    // ids, the generator's and base classes' names, a doc comment's file references, and the "()" / "see )" a dropped
    // <see cref> leaves behind (a method call such as $Error.Clear() is not one).
    [GeneratedRegex(@"PARITY|\bFinding \d|pwfix|COMPLIANCE-PLAN|LEAD-NOTES|\bshipped\b|\bWave \d|\w\.cs\b|\w\.md\b|Emitter\b|Hand-written|\bPP\d\d\b|conformance S\d|(?<![\w.])\(\)|see \)")]
    private static partial Regex MaintainersNote();

    [Theory]
    [MemberData(nameof(ModuleUnderTest.Names), MemberType = typeof(ModuleUnderTest))]
    public void No_help_text_carries_a_maintainers_note(string module)
    {
        XDocument document = XDocument.Load(ModuleUnderTest.Named(module).HelpFile);
        var violations = new List<string>();
        foreach (XElement entry in document.Descendants(Command + "command"))
        {
            string name = entry.Element(Command + "details")!.Element(Command + "name")!.Value;
            foreach (XElement para in entry.Descendants(Maml + "para"))
            {
                if (MaintainersNote().Match(para.Value) is { Success: true } match)
                    violations.Add($"{name}: '{match.Value}' in \"{para.Value[..Math.Min(para.Value.Length, 100)]}\"");
            }
        }

        Assert.Empty(violations);
    }

    [Fact]
    public void Every_parameter_set_is_its_own_syntax_entry_and_lists_a_parameter_once()
    {
        XDocument document = XDocument.Load(HelpFile);
        XElement getHost = document.Descendants(Command + "command")
            .Single(c => c.Element(Command + "details")!.Element(Command + "name")!.Value == "Get-ZbxHost");

        XElement[] syntax = [.. getHost.Element(Command + "syntax")!.Elements(Command + "syntaxItem")];
        Assert.Equal(4, syntax.Length);                                                  // All, ByName, ById, ByFilter
        var repeated = new List<string>();
        foreach (XElement entry in document.Descendants(Command + "syntaxItem"))
        {
            string cmdlet = entry.Element(Maml + "name")!.Value;
            repeated.AddRange(entry.Elements(Command + "parameter")
                .GroupBy(p => p.Element(Maml + "name")!.Value, StringComparer.Ordinal)
                .Where(g => g.Count() > 1)
                .Select(g => $"{cmdlet} -{g.Key}"));
        }
        Assert.Empty(repeated);
    }

    [Fact]
    public void Get_Help_shows_the_parameter_type_position_and_pipeline_input()
    {
        using Shell shell = Shell.Open();

        Run run = shell.Invoke("""
            $h = Get-Help Get-ZbxHost -Full
            $byName = $h.syntax.syntaxItem | Where-Object { $_.parameter.name -contains 'Name' }
            [pscustomobject]@{
                SyntaxEntries = @($h.syntax.syntaxItem).Count
                NameType      = [string](($byName.parameter | Where-Object name -EQ 'Name').parameterValue)
                NamePosition  = [string](($byName.parameter | Where-Object name -EQ 'Name').position)
                IdPipeline    = [string](($h.parameters.parameter | Where-Object name -EQ 'Id').pipelineInput)
            }
            """);

        Assert.Empty(run.Errors);
        PSObject help = Assert.Single(run.Output);
        Assert.Equal(4, (int)help.Properties["SyntaxEntries"].Value);
        Assert.Equal("string[]", (string)help.Properties["NameType"].Value);
        Assert.Equal("0", (string)help.Properties["NamePosition"].Value);
        // A piped id reaches -Id through the hidden InputObject (ZabbixGetCmdlet), so -Id itself binds by property name only.
        Assert.Equal("True (ByPropertyName)", (string)help.Properties["IdPipeline"].Value);
    }

    [Fact]
    public void A_parameter_shows_its_short_type_name_and_every_alias_including_a_generated_one()
    {
        XDocument document = XDocument.Load(HelpFile);
        string[] qualified = [.. document.Descendants(Command + "parameterValue").Select(v => v.Value)
            .Where(v => v.Contains("global::", StringComparison.Ordinal) || v.Contains("System.", StringComparison.Ordinal)).Distinct()];
        XElement inputObject = document.Descendants(Command + "command")
            .Single(c => c.Element(Command + "details")!.Element(Command + "name")!.Value == "Set-ZbxHostGroup")
            .Element(Command + "parameters")!.Elements(Command + "parameter")
            .Single(p => p.Element(Maml + "name")!.Value == "InputObject");

        Assert.Empty(qualified);
        Assert.Equal("Id", inputObject.Attribute("aliases")!.Value);                      // [global::...Alias("Id")]
        Assert.Equal("ZabbixReference<ZabbixHostGroup>[]", inputObject.Element(Command + "parameterValue")!.Value);
    }

    [Theory]
    [MemberData(nameof(ModuleUnderTest.Names), MemberType = typeof(ModuleUnderTest))]
    public void Every_hand_written_example_file_reaches_Get_Help_as_that_cmdlets_examples(string module)
    {
        XDocument document = XDocument.Load(ModuleUnderTest.Named(module).HelpFile);
        var examplesByCmdlet = document.Descendants(Command + "command").ToDictionary(
            c => c.Element(Command + "details")!.Element(Command + "name")!.Value,
            c => c.Element(Command + "examples")!.Elements(Command + "example").Select(e => e.Element(Maml + "title")!.Value).ToArray(),
            StringComparer.OrdinalIgnoreCase);

        string[] files = Directory.GetFiles(Path.Combine(ModuleUnderTest.Named(module).ProjectDirectory, "Examples"), "*.ps1");
        Assert.NotEmpty(files);
        var violations = new List<string>();
        foreach (string file in files)
        {
            string cmdlet = Path.GetFileNameWithoutExtension(file);
            string[] regions = [.. RegionTitle().Matches(File.ReadAllText(file)).Select(m => m.Groups[1].Value.Trim())];
            if (!examplesByCmdlet.TryGetValue(cmdlet, out string[]? titles)) { violations.Add($"{cmdlet}: not a cmdlet"); continue; }
            if (titles.Length != regions.Length) violations.Add($"{cmdlet}: {regions.Length} regions, {titles.Length} examples in help");
            for (int i = 0; i < Math.Min(titles.Length, regions.Length); i++)
                if (!titles[i].Contains(regions[i], StringComparison.Ordinal)) violations.Add($"{cmdlet}: example {i + 1} is '{titles[i]}', not '{regions[i]}'");
        }

        Assert.Empty(violations);
    }

    [Theory]
    [InlineData("about_Community.Zabbix")]
    [InlineData("about_Community.Zabbix_Sessions")]
    [InlineData("about_Community.Zabbix_Properties")]
    [InlineData("about_Community.Zabbix_Pipeline")]
    [InlineData("about_Community.Zabbix.Agent")]
    public void The_about_topic_is_found_by_Get_Help(string topic)
    {
        using Shell shell = Shell.Open();

        Run run = shell.Invoke("Get-Help $topic | Out-String", new Dictionary<string, object?> { ["topic"] = topic });

        Assert.Empty(run.Errors);
        string text = string.Concat(run.Output.Select(o => o.BaseObject as string));
        Assert.Contains("SHORT DESCRIPTION", text, StringComparison.Ordinal);
        Assert.Contains(topic, text, StringComparison.Ordinal);
    }

    [GeneratedRegex(@"(?m)^\s*#region\s+(.+)$")]
    private static partial Regex RegionTitle();
}
