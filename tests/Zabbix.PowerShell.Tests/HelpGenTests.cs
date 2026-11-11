// This Source Code Form is subject to the terms of the Mozilla Public
// License, v. 2.0. Copyright (c) 2026 schroeder247. SPDX-License-Identifier: MPL-2.0
using System.Text.Json;
using Community.Zabbix.PowerShell.HelpGen;

namespace Community.Zabbix.PowerShell.Tests;

/// <summary>
/// The help generator's source reader (<see cref="CommandSourceParser"/>) and MAML writer (<see cref="MamlWriter"/>)
/// on fixed input, without MSBuild: a golden MAML file for a checked-in sample, and the reader's boundaries — where a
/// class, an attribute and a doc comment end — each pinned by the smallest source that moves one.
/// </summary>
public sealed class HelpGenTests
{
    private static readonly string SampleDirectory = Path.Combine(AppContext.BaseDirectory, "HelpGenSample");

    [Fact]
    public void The_MAML_of_the_sample_commands_is_the_checked_in_golden_file()
    {
        // On a deliberate change, the run leaves the MAML it built beside the golden file in the test output
        // (SampleCommands.maml.actual.xml); copy it over HelpGenSample/SampleCommands.maml.xml.
        IReadOnlyList<CmdletDoc> cmdlets = SampleWithHelpScript();

        string actual = MamlWriter.Build(cmdlets, "https://example.test/reference/Sample/", new Dictionary<string, string>()).ToString() + "\n";

        File.WriteAllText(Path.Combine(SampleDirectory, "SampleCommands.maml.actual.xml"), actual);
        Assert.Equal(File.ReadAllText(Path.Combine(SampleDirectory, "SampleCommands.maml.xml")).ReplaceLineEndings("\n"), actual.ReplaceLineEndings("\n"));
    }

    [Theory]
    [InlineData("100", "100")]
    [InlineData("\"0.0.0.0\"", "0.0.0.0")]
    [InlineData("TimeSpan.FromSeconds(60)", "60 seconds")]
    [InlineData("TimeSpan.FromMinutes(2)", "2 minutes")]
    [InlineData("ZabbixConfigurationFormat.Json", "Json")]
    [InlineData("Environment.MachineName", "[Environment]::MachineName")]
    [InlineData("[\"127.0.0.1\", \"::1\"]", "127.0.0.1, ::1")]
    [InlineData("new global::System.Management.Automation.SwitchParameter(true)", "True")]
    [InlineData("null!", null)]
    [InlineData("string.Empty", null)]
    [InlineData("[]", null)]
    [InlineData("ZabbixServerNode.DefaultPort", null)]
    public void A_parameter_s_initializer_is_its_help_default_value_where_it_says_one(string initializer, string? expected)
    {
        // dev:defaultValue: what the parameter is when it is not given, as a reader reads it; a constant declared outside
        // the module's sources has no value here, and its summary states it instead.
        Microsoft.CodeAnalysis.CSharp.Syntax.ExpressionSyntax expression = Microsoft.CodeAnalysis.CSharp.SyntaxFactory.ParseExpression(initializer);

        Assert.Equal(expected, CommandSourceParser.DefaultValue(expression));
    }

    [Fact]
    public void The_MAML_writes_a_default_value_only_where_the_initializer_says_one()
    {
        IReadOnlyList<CmdletDoc> cmdlets = SampleWithHelpScript();

        System.Xml.Linq.XDocument maml = MamlWriter.Build(cmdlets, "https://example.test/reference/Sample/", new Dictionary<string, string>());

        System.Xml.Linq.XNamespace dev = "http://schemas.microsoft.com/maml/dev/2004/10";
        Assert.Equal(["100"], maml.Descendants(dev + "defaultValue").Select(value => value.Value));
    }

    [Fact]
    public void An_undecorated_base_between_two_cmdlets_lends_its_parameters_only_to_the_cmdlet_that_derives_it()
    {
        // A cmdlet is bounded by its class, not by the next [Cmdlet] attribute: otherwise New-ZbxAgentHandler's
        // help would carry the Name/Binding/PassThru of the abstract base declared after it.
        IReadOnlyList<CmdletDoc> cmdlets = Read("""
            [Cmdlet(VerbsCommon.New, "Handler")]
            public sealed class NewHandlerCommand : PSCmdlet
            {
                [Parameter] public string? Template { get; set; }
            }

            public abstract class HandlerCommand : PSCmdlet
            {
                [Parameter] public string? Name { get; set; }
            }

            [Cmdlet(VerbsLifecycle.Register, "Handler")]
            public sealed class RegisterHandlerCommand : HandlerCommand
            {
                [Parameter] public SwitchParameter PassThru { get; set; }
            }
            """);

        Assert.Equal(["New-Handler: Template", "Register-Handler: PassThru, Name"], cmdlets.Select(c => $"{c.Name}: {string.Join(", ", c.Parameters.Select(p => p.Name))}"));
    }

    [Fact]
    public void A_hand_written_part_of_a_partial_cmdlet_adds_its_parameters_to_the_help()
    {
        // New-ZbxToken -Generate is declared in a hand-written part of the generated class; the help includes it.
        IReadOnlyList<CmdletDoc> cmdlets = CommandSourceParser.Cmdlets(
        [
            CommandSourceParser.Parse("""
                using System.Management.Automation;
                [Cmdlet(VerbsCommon.New, "Token")]
                public sealed partial class NewTokenCommand : PSCmdlet
                {
                    [Parameter] public string? Name { get; set; }
                }
                """),
            CommandSourceParser.Parse("""
                using System.Management.Automation;
                public sealed partial class NewTokenCommand
                {
                    /// <summary>Also generates the secret.</summary>
                    [Parameter] public SwitchParameter Generate { get; set; }
                }
                """),
        ], []);

        CmdletDoc cmdlet = Assert.Single(cmdlets);
        Assert.Equal(["Name", "Generate"], cmdlet.Parameters.Select(p => p.Name));
        Assert.Equal("Also generates the secret.", cmdlet.Parameters[1].Description);
    }

    [Fact]
    public void An_OutputType_that_names_an_ETS_type_is_the_commands_output()
    {
        CmdletDoc cmdlet = Assert.Single(Read("""
            [Cmdlet(VerbsCommon.Get, "Info")]
            [OutputType("Community.Zabbix.PowerShell.ServerInfo", ParameterSetName = new[] { "Default" })]
            public sealed class GetInfoCommand : PSCmdlet
            {
            }
            """));

        Assert.Equal(["Community.Zabbix.PowerShell.ServerInfo"], cmdlet.OutputTypes);
    }

    [Fact]
    public void An_array_initializer_in_an_OutputType_attribute_does_not_hide_the_cmdlets_own_parameters()
    {
        // The class body starts at the class's own '{', not the first '{' after [Cmdlet]: otherwise Get-ZbxQueue's -Server,
        // -ByProxy, -Details and -Limit go undocumented behind OutputType's "new[] { ... }".
        CmdletDoc cmdlet = Assert.Single(Read("""
            [Cmdlet(VerbsCommon.Get, "Queue", DefaultParameterSetName = "Overview")]
            [OutputType(typeof(QueueOverview), ParameterSetName = new[] { "Overview" })]
            [OutputType(typeof(Global.QueueItem), ParameterSetName = new[] { "Details" })]
            public sealed class GetQueueCommand : PSCmdlet
            {
                [Parameter] public string? Server { get; set; }
                [Parameter(ParameterSetName = "Details")] public int Limit { get; set; }
            }
            """));

        Assert.Equal(["Server", "Limit"], cmdlet.Parameters.Select(p => p.Name));
        Assert.Equal(["QueueOverview", "QueueItem"], cmdlet.OutputTypes);
    }

    [Fact]
    public void An_attribute_ahead_of_Parameter_still_documents_the_property_it_decorates()
    {
        // The doc comment and the [Alias] are the property's wherever its [Parameter] sits in the attribute lists.
        ParameterDoc parameter = Assert.Single(Assert.Single(Read("""
            [Cmdlet(VerbsCommon.Get, "Thing")]
            public sealed class GetThingCommand : PSCmdlet
            {
                /// <summary>The thing's name.</summary>
                [Alias("ThingName"), SupportsWildcards]
                [Parameter(Mandatory = true, Position = 0)]
                public virtual string Name { get; set; } = "";
            }
            """)).Parameters);

        Assert.Equal(("The thing's name.", "string", "ThingName", true, true, 0), (parameter.Description, parameter.TypeText, string.Join(", ", parameter.Aliases), parameter.Wildcards, parameter.Mandatory, parameter.Uses[0].Position));
    }

    [Fact]
    public void An_attribute_quoted_in_a_comment_or_a_string_is_not_read()
    {
        IReadOnlyList<CmdletDoc> cmdlets = Read("""
            /// <summary>Declare it with <c>[Cmdlet(VerbsCommon.Get, "Ghost")]</c>.</summary>
            public abstract class GhostBase : PSCmdlet
            {
                // [Parameter] public string? Commented { get; set; }
                public string Text { get; set; } = "[Parameter] public string? Quoted { get; set; }";
            }

            [Cmdlet("Get", "Real")]
            public sealed class GetRealCommand : GhostBase
            {
                /// <summary>Quoted as <c>[Parameter(Mandatory = true)]</c> in prose.</summary>
                [Parameter] public string? Name { get; set; }
            }
            """);

        CmdletDoc cmdlet = Assert.Single(cmdlets);
        Assert.Equal(("Get-Real", "Name", false), (cmdlet.Name, Assert.Single(cmdlet.Parameters).Name, cmdlet.Parameters[0].Mandatory));
    }

    [Fact]
    public void A_commands_own_alias_is_the_commands_and_a_parameters_alias_is_the_parameters()
    {
        // The manifest's AliasesToExport is every command's own [Alias]; a parameter's never joins it.
        CmdletDoc cmdlet = Assert.Single(Read("""
            [global::System.Management.Automation.Cmdlet(global::System.Management.Automation.VerbsCommon.Get, "Item")]
            [global::System.Management.Automation.Alias("gi2", "getitem")]
            public sealed class GetItemCommand : PSCmdlet
            {
                [global::System.Management.Automation.Parameter, global::System.Management.Automation.Alias("Key")]
                public string? Name { get; set; }
            }
            """));

        Assert.Equal(("gi2, getitem", "Key"), (string.Join(", ", cmdlet.Aliases), string.Join(", ", cmdlet.Parameters[0].Aliases)));
    }

    [Theory]
    [InlineData("global::System.Security.SecureString?", "SecureString")]
    [InlineData("string[]?", "string[]")]
    [InlineData("global::Community.Zabbix.PowerShell.ZabbixReference<global::Community.Zabbix.Api.ZabbixHost>[]", "ZabbixReference<ZabbixHost>[]")]
    [InlineData("System.Collections.Generic.Dictionary<string, int?>", "Dictionary<string, int>")]
    public void A_type_is_displayed_without_namespaces_or_nullable_markers(string written, string displayed)
        => Assert.Equal(displayed, ParameterDoc.DisplayTypeOf(written));

    [Fact]
    public void A_generated_example_keeps_its_code_lines_and_its_remark_paragraphs()
    {
        // The generator writes a generated command's examples as <example> doc comments: the code keeps its lines (an
        // indented continuation too) and its entities decoded, and each <para> is one remark paragraph.
        CmdletDoc get = SampleWithHelpScript().Single(c => c.Name == "Get-SmpWidget");

        Assert.Equal(
            [
                new ExampleDoc("Get a widget by name", "Get-SmpWidget -Name 'gear & sprocket'", "Gets the widget named gear & sprocket."),
                new ExampleDoc("Count the widgets of a pipeline", "Get-SmpWidget |\n    Measure-Object", "Gets every widget and counts them.\nA second paragraph is a second remark."),
            ],
            get.Examples);
    }

    [Fact]
    public void A_help_script_replaces_the_synopsis_and_the_description_and_gives_the_examples()
    {
        // Examples/<Verb-Noun>.ps1's comment-based help block: .SYNOPSIS and .DESCRIPTION replace the class's paragraphs
        // (a wrapped section is one paragraph); each region is an example, its wrapped comment lines one remark
        // paragraph and a bare '#' line the start of the next.
        CmdletDoc remove = SampleWithHelpScript().Single(c => c.Name == "Remove-SmpWidget");

        Assert.Equal("Removes widgets from the store.", remove.Synopsis);
        Assert.Equal("The Remove-SmpWidget cmdlet deletes the widget given to -InputObject. It asks first unless -Force is given.", remove.Description);
        ExampleDoc example = Assert.Single(remove.Examples);
        Assert.Equal(new ExampleDoc("Remove a widget", "$widget | Remove-SmpWidget -Force", "The widget comes from the pipeline; -Force skips the prompt.\nA bare comment line starts a second paragraph."), example);
    }

    [Fact]
    public void The_generated_examples_are_written_for_the_live_run_each_marked_shown_or_replaced_by_a_help_script()
    {
        // tools/Test-ZbxHelpExamples.ps1 runs every shown generated example and uses a replaced one only to set up the
        // object a shown one works on.
        IReadOnlyList<CmdletDoc> generated = CommandSourceParser.Cmdlets([CommandSourceParser.Parse(File.ReadAllText(Path.Combine(SampleDirectory, "SampleCommands.cs.txt")))], []);

        using JsonDocument written = JsonDocument.Parse(GeneratedExamples.Build(generated, new HashSet<string>(["Get-SmpWidget"], StringComparer.OrdinalIgnoreCase)));

        JsonElement[] entries = [.. written.RootElement.EnumerateArray()];
        Assert.Equal(2, entries.Length);
        Assert.All(entries, entry => Assert.Equal("Get-SmpWidget", entry.GetProperty("Cmdlet").GetString()));
        Assert.Equal("Get-SmpWidget -Name 'gear & sprocket'", entries[0].GetProperty("Code").GetString());
        Assert.Equal("Count the widgets of a pipeline", entries[1].GetProperty("Title").GetString());
        Assert.All(entries, entry => Assert.False(entry.GetProperty("Shown").GetBoolean()));
        Assert.True(JsonDocument.Parse(GeneratedExamples.Build(generated, new HashSet<string>())).RootElement[0].GetProperty("Shown").GetBoolean());
    }

    [Theory]
    [InlineData("<#\n.EXAMPLE\nGet-Thing\n#>\n#region A\nGet-Thing\n#endregion", ".EXAMPLE is not read")]
    [InlineData("<#\n.SYNOPSIS\nOne.\n.SYNOPSIS\nTwo.\n#>\n#region A\nGet-Thing\n#endregion", ".SYNOPSIS is given twice")]
    [InlineData("<#\n.DESCRIPTION\n#>\n#region A\nGet-Thing\n#endregion", ".DESCRIPTION is empty")]
    [InlineData("<#\n.SYNOPSIS\nOne.\n#region A\nGet-Thing\n#endregion", "the help block is never closed")]
    [InlineData("<#\nloose text\n.SYNOPSIS\nOne.\n#>\n#region A\nGet-Thing\n#endregion", "text outside a .SYNOPSIS or .DESCRIPTION section")]
    [InlineData("#region A\n# only a remark\n#endregion", "the region 'A' has no code")]
    public void A_malformed_help_script_is_refused_with_the_reason(string script, string reason)
    {
        List<string> errors = [];

        HelpScriptParser.Parse(script, errors);

        Assert.Contains(errors, error => error.Contains(reason, StringComparison.Ordinal));
    }

    // The sample commands, Remove-SmpWidget with its help script applied as the help build applies it.
    private static IReadOnlyList<CmdletDoc> SampleWithHelpScript()
    {
        List<string> errors = [];
        HelpScript script = HelpScriptParser.Parse(File.ReadAllText(Path.Combine(SampleDirectory, "Remove-SmpWidget.ps1")), errors);
        Assert.Empty(errors);
        return [.. CommandSourceParser.Cmdlets([CommandSourceParser.Parse(File.ReadAllText(Path.Combine(SampleDirectory, "SampleCommands.cs.txt")))], [])
            .Select(cmdlet => cmdlet.Name == "Remove-SmpWidget" ? cmdlet.With(script) : cmdlet)];
    }

    private static IReadOnlyList<CmdletDoc> Read(string source)
        => CommandSourceParser.Cmdlets([CommandSourceParser.Parse("using System.Management.Automation;\n" + source)], []);
}
