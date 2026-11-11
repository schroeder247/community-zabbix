// This Source Code Form is subject to the terms of the Mozilla Public
// License, v. 2.0. Copyright (c) 2026 schroeder247. SPDX-License-Identifier: MPL-2.0
extern alias ZabbixGenerator;

using System.Reflection;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.Diagnostics;
using Microsoft.CodeAnalysis.Text;
using ZabbixGenerator::Community.Zabbix.Generator;

namespace Community.Zabbix.Tests;

/// <summary>
/// The generators as a compiler host runs them: a malformed description is a located ZBX diagnostic and never an
/// exception out of the generator; an unchanged description is cached at the first step; the emitters stop when the
/// compiler cancels; and the generator assembly loads in the oldest host it claims (its System.Text.Json reference).
/// </summary>
public sealed class GeneratorDriverTests
{
    private const string Valid = """
        {
         "versions": ["7.0", "8.0"],
         "enums": [
          {"name": "ThingKind", "family": "THING_KIND", "members": [
            {"name": "Plain", "value": 0},
            {"name": "Fancy", "value": 1}
          ]}
         ],
         "types": [
          {"name": "Thing", "role": "read", "object": "thing", "fields": [
            {"name": "ThingId", "wire": "thingid", "kind": "id"},
            {"name": "Kind", "wire": "kind", "kind": "enum:ThingKind"}
          ]}
         ],
         "services": [
          {"object": "thing", "client": "ZabbixThingClient", "property": "Things", "model": "Thing", "methods": []}
         ]
        }
        """;

    private static GeneratorDriverRunResult Run(IIncrementalGenerator generator, string json, string part)
    {
        var compilation = CSharpCompilation.Create("probe", references: [MetadataReference.CreateFromFile(typeof(object).Assembly.Location)]);
        GeneratorDriver driver = CSharpGeneratorDriver.Create(
            generators: [generator.AsSourceGenerator()],
            additionalTexts: [new Text("/schema/zabbix-api.json", json)],
            optionsProvider: new Part(part));
        return driver.RunGenerators(compilation, TestContext.Current.CancellationToken).GetRunResult();
    }

    public static TheoryData<string, string, string, int> Malformed => new()
    {
        // what is broken, the replacement, the subject the diagnostic names, the zero-based line it points at
        { "a record declared twice", Valid.Replace("\"types\": [", "\"types\": [\n  {\"name\": \"Thing\", \"role\": \"read\", \"fields\": []},", StringComparison.Ordinal), "Thing", 9 },
        { "an enum member whose value is not an integer", Valid.Replace("\"value\": 1", "\"value\": \"one\"", StringComparison.Ordinal), "ThingKind", 3 },
        { "an enum member declared twice", Valid.Replace("\"name\": \"Fancy\"", "\"name\": \"Plain\"", StringComparison.Ordinal), "ThingKind.Plain", 4 },
        { "a record with no fields", Valid.Replace("\"role\": \"read\", \"object\": \"thing\", \"fields\": [", "\"role\": \"read\", \"object\": \"thing\", \"felds\": [", StringComparison.Ordinal), "Thing", 9 },
        { "a version that is not major.minor", Valid.Replace("\"kind\": \"id\"}", "\"kind\": \"id\", \"since\": \"seven\"}", StringComparison.Ordinal), "Thing.thingid", 10 },
        { "a field name that is not an identifier", Valid.Replace("\"name\": \"Kind\"", "\"name\": \"Kind Of\"", StringComparison.Ordinal), "Thing.kind", 11 },
        { "an enum name that takes the record's file", Valid.Replace("\"name\": \"ThingKind\"", "\"name\": \"Thing\"", StringComparison.Ordinal).Replace("enum:ThingKind", "enum:Thing", StringComparison.Ordinal), "Thing", 3 },
        { "a flags member that is neither a bit nor a union", Valid.Replace("\"family\": \"THING_KIND\",", "\"family\": \"THING_KIND\", \"flags\": true,", StringComparison.Ordinal).Replace("\"value\": 1", "\"value\": 6", StringComparison.Ordinal), "ThingKind.Fancy", 5 },
        { "a client declared twice", Valid.Replace("\"methods\": []}", "\"methods\": []},\n  {\"object\": \"other\", \"client\": \"ZabbixThingClient\", \"property\": \"Others\", \"methods\": []}", StringComparison.Ordinal), "ZabbixThingClient", 0 },
        { "a builder whose concept is not an identifier", Valid.Replace(" \"services\": [", " \"cmdlets\": {\"builders\": [\n  {\"concept\": \"Bad Concept\", \"record\": \"Thing\", \"noun\": \"n\", \"use\": \"u\", \"what\": \"w\"}]},\n \"services\": [", StringComparison.Ordinal), "Bad Concept", 15 },
        { "an int whose summary names the defines of a closed set", Valid.Replace("\"kind\": \"enum:ThingKind\"}", "\"kind\": \"int\", \"summary\": \"0 plain, 1 fancy (<c>THING_KIND_PLAIN</c>, <c>THING_KIND_FANCY</c>).\"}", StringComparison.Ordinal), "Thing.kind", 11 },
        { "a method declared twice", Valid.Replace("\"methods\": []", "\"methods\": [{\"api\": \"thing.get\", \"name\": \"GetAsync\", \"shape\": \"get-list\", \"parameters\": \"json\", \"result\": \"json\"}, {\"api\": \"thing.get\", \"name\": \"GetAsync\", \"shape\": \"get-list\", \"parameters\": \"json\", \"result\": \"json\"}]", StringComparison.Ordinal), "thing.thing.get", 15 },
    };

    [Theory]
    [MemberData(nameof(Malformed))]
    public void A_malformed_entry_is_a_located_diagnostic_and_the_rest_is_still_generated(string broken, string json, string subject, int line)
    {
        foreach (string part in new[] { "Models", "Clients" })
        {
            GeneratorDriverRunResult result = Run(new ZabbixApiGenerator(), json, part);

            Assert.Null(result.Results[0].Exception);
            Diagnostic diagnostic = Assert.Single(result.Diagnostics, d => d.Id == "ZBX0008");
            Assert.StartsWith(subject + " is malformed or declared twice: ", diagnostic.GetMessage(System.Globalization.CultureInfo.InvariantCulture), StringComparison.Ordinal);
            if (line > 0) Assert.Equal(line, diagnostic.Location.GetLineSpan().StartLinePosition.Line);
            Assert.NotEmpty(result.GeneratedTrees);
            _ = broken;
        }
        GeneratorDriverRunResult cmdlets = Run(new ZabbixCmdletGenerator(), json, "Cmdlets");
        Assert.Null(cmdlets.Results[0].Exception);
        Assert.Contains(cmdlets.Diagnostics, d => d.Id == "ZBX0008");
    }

    [Fact]
    public void A_json_syntax_error_is_located_at_its_line_and_both_generators_report_it_as_zbx0001()
    {
        string json = Valid.Replace("\"thingid\",", "\"thingid\"", StringComparison.Ordinal);

        Diagnostic models = Assert.Single(Run(new ZabbixApiGenerator(), json, "Models").Diagnostics);
        Diagnostic cmdlets = Assert.Single(Run(new ZabbixCmdletGenerator(), json, "Cmdlets").Diagnostics);

        Assert.Equal("ZBX0001", models.Id);
        Assert.Equal("ZBX0001", cmdlets.Id);
        Assert.Equal(10, models.Location.GetLineSpan().StartLinePosition.Line);
    }

    [Fact]
    public void Every_diagnostic_has_a_description_and_a_help_page_and_names_its_subject_without_a_dangling_dot()
    {
        DiagnosticDescriptor[] all = [.. typeof(ZabbixDiagnostics).GetFields(BindingFlags.Public | BindingFlags.Static).Select(field => (DiagnosticDescriptor)field.GetValue(null)!)];
        string releases = File.ReadAllText(Path.Combine(Repository.Root, "src", "Zabbix.Generator", "AnalyzerReleases.Unshipped.md"));
        string page = File.ReadAllText(Path.Combine(Repository.Root, "docs", "generator-diagnostics.md"));

        Assert.Equal(7, all.Length);
        Assert.All(all, descriptor =>
        {
            Assert.False(string.IsNullOrWhiteSpace(descriptor.Description.ToString(System.Globalization.CultureInfo.InvariantCulture)), descriptor.Id);
            Assert.EndsWith("generator-diagnostics.md#" + descriptor.Id.ToUpperInvariant().Replace("ZBX", "zbx", StringComparison.Ordinal), descriptor.HelpLinkUri, StringComparison.Ordinal);
            Assert.Contains(descriptor.Id + " |", releases, StringComparison.Ordinal);
            Assert.Contains("## " + descriptor.Id, page, StringComparison.Ordinal);
        });
        Assert.Equal("Thing", new EmitProblem(ZabbixDiagnostics.NotGenerated, "x", "Thing", null).Subject);
        Assert.Equal("Thing.kind", new EmitProblem(ZabbixDiagnostics.NotGenerated, "x", "Thing", "kind").Subject);
    }

    [Fact]
    public void A_quote_a_backslash_markup_and_a_keyword_in_the_description_still_make_valid_csharp()
    {
        string json = Valid
            .Replace("\"wire\": \"kind\"", "\"wire\": \"ki\\\"nd\\\\x\", \"since\": \"7.4\", \"summary\": \"a < b & c\"", StringComparison.Ordinal)
            .Replace("\"name\": \"Fancy\"", "\"name\": \"Fancy\", \"summary\": \"ends */ and <unclosed\"", StringComparison.Ordinal)
            .Replace("\"name\": \"ThingId\"", "\"name\": \"class\"", StringComparison.Ordinal);

        GeneratorDriverRunResult result = Run(new ZabbixApiGenerator(), json, "Models");

        Assert.Empty(result.Diagnostics);
        string thing = result.GeneratedTrees.Single(tree => tree.FilePath.EndsWith("ZabbixThing.g.cs", StringComparison.Ordinal)).GetText(TestContext.Current.CancellationToken).ToString();
        Assert.Contains("JsonPropertyName(\"ki\\\"nd\\\\x\")", thing, StringComparison.Ordinal);
        Assert.Contains("@class { get; init; }", thing, StringComparison.Ordinal);
        Assert.Contains("a &lt; b &amp; c", thing, StringComparison.Ordinal);
        Assert.All(result.GeneratedTrees, tree => Assert.Empty(tree.GetDiagnostics(TestContext.Current.CancellationToken)));
    }

    [Fact]
    public void An_unchanged_description_is_cached_at_the_first_step_and_nothing_after_it_reruns()
    {
        var compilation = CSharpCompilation.Create("probe", references: [MetadataReference.CreateFromFile(typeof(object).Assembly.Location)]);
        var first = new Text("/schema/zabbix-api.json", Valid);
        GeneratorDriver driver = CSharpGeneratorDriver.Create(
            [new ZabbixApiGenerator().AsSourceGenerator()],
            additionalTexts: [first],
            optionsProvider: new Part("Models"),
            driverOptions: new GeneratorDriverOptions(IncrementalGeneratorOutputKind.None, trackIncrementalGeneratorSteps: true));
        driver = driver.RunGenerators(compilation, TestContext.Current.CancellationToken);

        // the same text in a new AdditionalText (an editor's re-read): the read step reruns and finds its output equal
        driver = driver.ReplaceAdditionalText(first, new Text("/schema/zabbix-api.json", Valid));
        driver = driver.RunGenerators(compilation.AddSyntaxTrees(CSharpSyntaxTree.ParseText("class Edited;", cancellationToken: TestContext.Current.CancellationToken)), TestContext.Current.CancellationToken);
        GeneratorRunResult run = driver.GetRunResult().Results[0];

        IncrementalStepRunReason[] parse = [.. run.TrackedSteps[DescriptionPipeline.ParseStep].SelectMany(step => step.Outputs).Select(output => output.Reason)];
        IncrementalStepRunReason[] emit = [.. run.TrackedSteps[DescriptionPipeline.EmitStep].SelectMany(step => step.Outputs).Select(output => output.Reason)];
        Assert.All(parse, reason => Assert.Contains(reason, new[] { IncrementalStepRunReason.Cached, IncrementalStepRunReason.Unchanged }));
        Assert.All(emit, reason => Assert.Equal(IncrementalStepRunReason.Cached, reason));
        Assert.NotEmpty(emit);
    }

    [Theory]
    [InlineData("Models")]
    [InlineData("Cmdlets")]
    public void An_options_change_that_leaves_the_part_alone_reads_nothing_again(string part)
    {
        IIncrementalGenerator generator = part == "Cmdlets" ? new ZabbixCmdletGenerator() : new ZabbixApiGenerator();
        var compilation = CSharpCompilation.Create("probe", references: [MetadataReference.CreateFromFile(typeof(object).Assembly.Location)]);
        GeneratorDriver driver = CSharpGeneratorDriver.Create(
            [generator.AsSourceGenerator()],
            additionalTexts: [new Text(part == "Cmdlets" ? "/obj/zabbix-api.cmdlets.json" : "/schema/zabbix-api.json", Valid), new Text("/src/PublicAPI.Unshipped.txt", "#nullable enable")],
            optionsProvider: new Part(part),
            driverOptions: new GeneratorDriverOptions(IncrementalGeneratorOutputKind.None, trackIncrementalGeneratorSteps: true));
        driver = driver.RunGenerators(compilation, TestContext.Current.CancellationToken);

        // an .editorconfig or global-options edit: a new options provider whose answer for the description is the same
        driver = driver.WithUpdatedAnalyzerConfigOptions(new Part(part));
        driver = driver.RunGenerators(compilation, TestContext.Current.CancellationToken);
        GeneratorRunResult run = driver.GetRunResult().Results[0];

        IncrementalStepRunReason[] parse = [.. run.TrackedSteps[DescriptionPipeline.ParseStep].SelectMany(step => step.Outputs).Select(output => output.Reason)];
        IncrementalStepRunReason[] emit = [.. run.TrackedSteps[DescriptionPipeline.EmitStep].SelectMany(step => step.Outputs).Select(output => output.Reason)];
        Assert.Equal([IncrementalStepRunReason.Cached], parse);
        Assert.Equal([IncrementalStepRunReason.Cached], emit);
    }

    [Fact]
    public void The_cmdlet_generator_caches_an_unchanged_description_and_reads_only_the_description()
    {
        var compilation = CSharpCompilation.Create("probe", references: [MetadataReference.CreateFromFile(typeof(object).Assembly.Location)]);
        var first = new Text("/obj/zabbix-api.cmdlets.json", Valid);
        GeneratorDriver driver = CSharpGeneratorDriver.Create(
            [new ZabbixCmdletGenerator().AsSourceGenerator()],
            additionalTexts: [first, new Text("/src/PublicAPI.Unshipped.txt", "#nullable enable")],
            optionsProvider: new Part("Cmdlets"),
            driverOptions: new GeneratorDriverOptions(IncrementalGeneratorOutputKind.None, trackIncrementalGeneratorSteps: true));
        driver = driver.RunGenerators(compilation, TestContext.Current.CancellationToken);
        Assert.Single(driver.GetRunResult().Results[0].TrackedSteps[DescriptionPipeline.ParseStep].SelectMany(step => step.Outputs));

        driver = driver.ReplaceAdditionalText(first, new Text("/obj/zabbix-api.cmdlets.json", Valid));
        driver = driver.RunGenerators(compilation.AddSyntaxTrees(CSharpSyntaxTree.ParseText("class Edited;", cancellationToken: TestContext.Current.CancellationToken)), TestContext.Current.CancellationToken);
        GeneratorRunResult run = driver.GetRunResult().Results[0];

        IncrementalStepRunReason[] emit = [.. run.TrackedSteps[DescriptionPipeline.EmitStep].SelectMany(step => step.Outputs).Select(output => output.Reason)];
        Assert.Equal([IncrementalStepRunReason.Cached], emit);
    }

    [Theory]
    [InlineData("ZBX0004")]
    [InlineData("ZBX0005")]
    public void A_name_collision_in_the_real_description_names_an_overlay_entry_that_exists(string id)
    {
        // a collision made in the shipped description: the second field of the first record, or the second method of the
        // first client, takes the first one's C# name
        System.Text.Json.Nodes.JsonNode description = System.Text.Json.Nodes.JsonNode.Parse(File.ReadAllText(Path.Combine(Repository.Root, "schema", "zabbix-api.json")))!;
        System.Text.Json.Nodes.JsonArray entries = id == "ZBX0004" ? description["types"]![0]!["fields"]!.AsArray() : description["services"]![0]!["methods"]!.AsArray();
        entries[1]!["name"] = entries[0]!["name"]!.GetValue<string>();

        GeneratorDriverRunResult result = Run(new ZabbixApiGenerator(), description.ToJsonString(), id == "ZBX0004" ? "Models" : "Clients");

        Diagnostic diagnostic = Assert.Single(result.Diagnostics, d => d.Id == id);
        string message = diagnostic.GetMessage(System.Globalization.CultureInfo.InvariantCulture);
        string pointer = message[(message.IndexOf("schema/overlay.json#", StringComparison.Ordinal) + "schema/overlay.json#".Length)..];
        // the field's entry is in the overlay already; a method's may not be, and the extractor reads it where the pointer
        // says (tools/ZabbixApiExtract merge.py: overlay methods[api].name), so its container must be
        string[] tokens = [.. pointer.TrimStart('/').Split('/').Select(token => token.Replace("~1", "/", StringComparison.Ordinal).Replace("~0", "~", StringComparison.Ordinal))];
        Assert.Equal(id == "ZBX0004" ? ["types", entries.Parent!["name"]!.GetValue<string>(), "fields", entries[1]!["wire"]!.GetValue<string>(), "name"] : ["methods", entries[1]!["api"]!.GetValue<string>(), "name"], tokens);
        System.Text.Json.Nodes.JsonNode? at = System.Text.Json.Nodes.JsonNode.Parse(File.ReadAllText(Path.Combine(Repository.Root, "schema", "overlay.json")));
        foreach (string token in tokens[..(id == "ZBX0004" ? ^1 : ^2)]) at = at?[token];
        Assert.True(at is System.Text.Json.Nodes.JsonObject, $"{pointer} names no entry in schema/overlay.json");
        Assert.EndsWith("zabbix-api.json", diagnostic.Location.GetLineSpan().Path, StringComparison.Ordinal);
    }

    [Fact]
    public void Two_files_with_one_hint_name_keep_the_first_and_report_the_second_at_its_description_entry()
    {
        var parsed = new ParsedDescription("/schema/zabbix-api.json", "Models", Valid, ApiDescription.Read(Valid), []);

        GeneratorOutput output = DescriptionPipeline.Emit(parsed, static (_, _, _) => new EmitResult([new("ZabbixThing.g.cs", "// first"), new("ZabbixThing.g.cs", "// second")], []), TestContext.Current.CancellationToken);

        Assert.Equal("// first", Assert.Single(output.Files).Code);
        Diagnostic reported = Assert.Single(output.Diagnostics).ToDiagnostic();
        FileLinePositionSpan where = reported.Location.GetLineSpan();
        Assert.Equal(("ZBX0008", "/schema/zabbix-api.json", 9), (reported.Id, where.Path, where.StartLinePosition.Line));   // the entry named Thing
        Assert.Contains("ZabbixThing.g.cs", reported.GetMessage(System.Globalization.CultureInfo.InvariantCulture), StringComparison.Ordinal);
    }

    [Fact]
    public void An_emitter_stops_when_the_compiler_cancels()
    {
        ApiDescription api = ApiDescription.Read(Valid);
        using var cancelled = new CancellationTokenSource();
        cancelled.Cancel();

        Assert.Throws<OperationCanceledException>(() => new ZabbixApiEmitter(api).Emit(ZabbixApiPart.Models, cancelled.Token));
        Assert.Throws<OperationCanceledException>(() => new ZabbixApiEmitter(api).Emit(ZabbixApiPart.Clients, cancelled.Token));
        Assert.Throws<OperationCanceledException>(() => new ZabbixCmdletEmitter(api).Emit(cancelled.Token));
    }

    [Fact]
    public void The_generator_references_a_system_text_json_an_ide_host_on_net_8_has()
    {
        AssemblyName json = Assert.Single(typeof(ZabbixApiGenerator).Assembly.GetReferencedAssemblies(), name => name.Name == "System.Text.Json");
        AssemblyName roslyn = Assert.Single(typeof(ZabbixApiGenerator).Assembly.GetReferencedAssemblies(), name => name.Name == "Microsoft.CodeAnalysis");

        Assert.Equal(8, json.Version!.Major);
        Assert.Equal(new Version(4, 9, 0, 0), roslyn.Version);
    }

    private sealed class Text(string path, string text) : AdditionalText
    {
        public override string Path => path;

        public override SourceText GetText(CancellationToken cancellationToken = default) => SourceText.From(text);
    }

    private sealed class Part(string part) : AnalyzerConfigOptionsProvider
    {
        public override AnalyzerConfigOptions GlobalOptions { get; } = new Options(null);

        public override AnalyzerConfigOptions GetOptions(SyntaxTree tree) => new Options(null);

        public override AnalyzerConfigOptions GetOptions(AdditionalText textFile) => new Options(part);

        private sealed class Options(string? part) : AnalyzerConfigOptions
        {
            public override bool TryGetValue(string key, out string value)
            {
                value = part ?? string.Empty;
                return part is not null && key == "build_metadata.AdditionalFiles.ZabbixEmit";
            }
        }
    }
}
