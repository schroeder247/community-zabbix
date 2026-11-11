// This Source Code Form is subject to the terms of the Mozilla Public
// License, v. 2.0. Copyright (c) 2026 schroeder247. SPDX-License-Identifier: MPL-2.0
using System.Reflection;
using System.Text.RegularExpressions;
using System.Xml.Linq;
using Community.Zabbix.Tests.Conformance;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Microsoft.Extensions.Compliance.Classification;

namespace Community.Zabbix.Tests;

/// <summary>
/// The style adversary. <see cref="DesignConformanceTests"/> turns the design standard's rules into tests; this class tries
/// to EXPRESS what those rules say must be inexpressible, and adds rules that
/// the other suite does not carry. Each rule is also proven on a probe, so it cannot pass by being blind.
/// </summary>
public sealed partial class AdversarialDesignConformanceTests
{
    // ---- the build's own gaps: the ban list ----

    /// <summary>Every <c>// gap: ID</c> or, once closed, <c>// expect: ID</c> mark of <c>tests/probes/Zabbix.Analyzers.Probe/Adversarial.cs</c>.</summary>
    public static TheoryData<string, int, string> GapMarks
        => [.. GapMarkLines().Select(mark => new TheoryDataRow<string, int, string>(mark.File, mark.Line, mark.Id))];

    /// <summary>
    /// Each line of the probe that blocks, reads or schedules on the wall clock, queues raw thread-pool work or writes to the
    /// console must fail the library build with the id its mark names: the ban list, not review, keeps these out of the
    /// libraries.
    /// </summary>
    [Theory]
    [MemberData(nameof(GapMarks))]
    public async Task Every_ban_list_gap_is_a_build_error(string file, int line, string id)
    {
        IReadOnlyList<AnalyzerProbe.ProbeError> errors = await AnalyzerProbe.ErrorsAsync();

        Assert.Contains(new AnalyzerProbe.ProbeError(file, line, id), errors);
    }

    [Fact]
    public void The_probe_carries_the_ban_list_gaps()
        => Assert.True(GapMarkLines().Count() >= 12, "the gap probe lost its marks");

    // ---- generated code ----

    /// <summary>
    /// Generated code is held to the library rules, the output of ALL our generators, not only the API generator's.
    /// <c>Generated_code_is_held_to_the_library_rules</c> scans <c>Community.Zabbix.Generator.ZabbixApiGenerator/</c> only;
    /// <c>ZabbixCmdletEmitter</c> (the module's 51 files, ~17k lines of cmdlets) writes <c>// &lt;auto-generated/&gt;</c> and a
    /// blanket <c>#pragma warning disable</c>, so no analyzer (CA2000, CA1062, CA1305, RS0030 …) ever reads them.
    /// </summary>
    [Fact]
    public void Every_generator_s_output_is_held_to_the_library_rules()
        => DesignConformanceTests.AssertNone(GeneratedOffenders());

    [Fact]
    public void The_generated_code_rule_reads_every_generator_s_output()
        => Assert.Contains(GeneratedFiles(), file => file.Contains("ZabbixCmdletGenerator", StringComparison.Ordinal));

    // ---- which libraries are under conformance at all ----

    /// <summary>
    /// Every project carries a conformance suite. <see cref="Libraries"/> is computed from THIS
    /// test project's references, which do not include <c>Zabbix.PowerShell</c> (it needs the PowerShell SDK), so no rule of
    /// <see cref="DesignConformanceTests"/> ever read the module. Every library project of the solution must be read by a
    /// conformance suite: this one's, or one in a test project that references it directly
    /// (<c>Zabbix.PowerShell.Tests/AdversarialPowerShellConformanceTests.cs</c> is the module's).
    /// </summary>
    [Fact]
    public void Every_library_project_is_under_a_design_conformance_suite()
    {
        HashSet<string> here = [.. Libraries.All.Select(Libraries.Folder)];
        List<string> suites = [.. Directory.EnumerateDirectories(Path.Combine(Repository.Root, "tests"))
            .Where(directory => Directory.EnumerateFiles(directory, "*ConformanceTests*.cs").Any())];

        IEnumerable<string> uncovered = LibraryProjects()
            .Where(project => !here.Contains(project))
            .Where(project => !suites.Any(suite => Directory.EnumerateFiles(suite, "*.csproj")
                .Any(csproj => File.ReadAllText(csproj).Contains($"/src/{project}/{project}.csproj", StringComparison.Ordinal))));

        DesignConformanceTests.AssertNone(uncovered);
    }

    // ---- Roslyn rules over every library's sources (the module's included) ----

    /// <summary><c>async void</c> exists only for event handlers, and the libraries declare none.</summary>
    [Fact]
    public void No_library_method_is_async_void()
        => DesignConformanceTests.AssertNone(AsyncVoids(AllLibraryTrees.Value));

    [Fact]
    public void The_async_void_rule_finds_an_async_void_method_and_local_function()
        => Assert.Equal(["probe.cs:1", "probe.cs:1"], AsyncVoids([Sources.ParseText("class P { async void M() { async void L() { } } }", "probe.cs")]));

    /// <summary>A library logs through <c>ILogger</c>, never <c>Console</c>: console output bypasses the host's logging and redaction.</summary>
    [Fact]
    public void No_library_code_writes_to_the_console()
        => DesignConformanceTests.AssertNone(ConsoleUses(AllLibraryTrees.Value));

    [Fact]
    public void The_console_rule_finds_a_console_write()
        => Assert.Equal(["probe.cs:1"], ConsoleUses([Sources.ParseText("class P { void M() => System.Console.WriteLine(1); }", "probe.cs")]));

    /// <summary>
    /// No catch-all drops the exception, over EVERY library: <c>No_filtered_catch_all_drops_the_exception</c> reads
    /// <c>Sources.Library</c>, which is <see cref="Libraries"/>' sources, and the module's are not among them.
    /// The same Roslyn rule over all of them, one blind spot closed: a filter that passes the caught exception to a call
    /// (<c>when (outcome.Capture(failure))</c>) has handed it on. A swallowed failure vanishes without a log or a span: a
    /// session's dispose failure at runspace close, or a <c>PipelineStoppedException</c> from Ctrl+C at a prompt read as "no".
    /// </summary>
    [Fact]
    public void No_filtered_catch_all_drops_the_exception_in_any_library()
        => DesignConformanceTests.AssertNone(SwallowingCatchAllsExceptHandedOnInTheFilter(AllLibraryTrees.Value));

    [Fact]
    public void The_all_libraries_catch_all_rule_spares_a_filter_that_hands_the_exception_on()
        => Assert.Equal(["probe.cs:1"], SwallowingCatchAllsExceptHandedOnInTheFilter([Sources.ParseText(
            "class P { void M(System.Action a, System.Func<System.Exception, bool> keep) { try { a(); } catch (System.Exception e) when (keep(e)) { } try { a(); } catch (System.Exception e) when (e is not System.OutOfMemoryException) { } } }", "probe.cs")]));

    /// <summary>
    /// Time is injected: <see cref="TimeProvider.System"/> appears only where a composition default is
    /// chosen: the right side of <c>??</c>/<c>??=</c>, a property or field initializer (the injectable default), or an
    /// argument to a <c>TryAdd*</c>/<c>Add*</c> registration. Anywhere else it is a wall-clock read a test clock cannot reach.
    /// A direct read means, for example, that certificate validity is judged on the machine clock even when the caller
    /// injected one.
    /// </summary>
    [Fact]
    public void The_sdk_reads_the_clock_only_through_an_injected_time_provider()
        => DesignConformanceTests.AssertNone(SystemClockReads(AllLibraryTrees.Value).Where(offender => !offender.StartsWith("Zabbix.PowerShell/", StringComparison.Ordinal)));

    /// <summary>
    /// As above, in the module: a cmdlet reads time through <c>ZabbixContext.Time</c>, the runspace's injectable clock;
    /// one that bypasses it cannot be driven by a test clock.
    /// </summary>
    [Fact]
    public void The_module_reads_the_clock_only_through_its_context_s_time_provider()
        => DesignConformanceTests.AssertNone(SystemClockReads(AllLibraryTrees.Value).Where(offender => offender.StartsWith("Zabbix.PowerShell/", StringComparison.Ordinal)));

    [Fact]
    public void The_clock_rule_spares_a_constructor_s_default_but_not_a_read_inside_a_constructor()
        => Assert.Equal(["X/probe.cs:2"], SystemClockReads([Sources.ParseText(
            "class P { System.TimeProvider T { get; } long N; protected P() { T = System.TimeProvider.System;\nN = System.TimeProvider.System.GetTimestamp(); } }",
            Path.Combine(Repository.Root, "src", "X", "probe.cs"))]));

    [Fact]
    public void The_clock_rule_finds_a_direct_read_and_spares_a_composition_default()
        => Assert.Equal(["X/probe.cs:1"], SystemClockReads([Sources.ParseText(
            "class P { System.TimeProvider T { get; } = System.TimeProvider.System; long M(System.TimeProvider? t) => (t ?? System.TimeProvider.System).GetTimestamp() + System.TimeProvider.System.GetTimestamp(); }",
            Path.Combine(Repository.Root, "src", "X", "probe.cs"))]));

    /// <summary>
    /// A duration, a timeout or a deadline is measured on the monotonic clock
    /// (<c>GetTimestamp</c>/<c>GetElapsedTime</c>), never as the difference of, or a comparison between, two wall-clock readings
    /// (<c>GetUtcNow</c>), which move when the machine clock is set: a run's duration, a wait deadline or a cached topology's
    /// expiry would jump with the clock.
    /// </summary>
    [Fact]
    public void No_duration_or_deadline_is_measured_on_the_wall_clock()
        => DesignConformanceTests.AssertNone(WallClockDurations(AllLibraryTrees.Value));

    [Fact]
    public void The_wall_clock_duration_rule_finds_a_difference_and_a_deadline_but_not_a_stored_instant_s_age()
        => Assert.Equal(["X/probe.cs:3", "X/probe.cs:4"], WallClockDurations([Sources.ParseText(
            "class P { System.TimeProvider t = System.TimeProvider.System; void M(System.DateTimeOffset stored) {\nvar started = t.GetUtcNow(); var deadline = t.GetUtcNow() + System.TimeSpan.FromSeconds(1);\nvar took = t.GetUtcNow() - started;\nbool late = t.GetUtcNow() >= deadline;\nvar age = t.GetUtcNow() - stored; } }",
            Path.Combine(Repository.Root, "src", "X", "probe.cs"))]));

    // ---- reflection rules ----

    /// <summary>
    /// A public class is sealed (a leaf) or
    /// abstract (a designed base). An unsealed concrete class is an extension point nobody designed. Exempt, computed: a
    /// mockable client — every public instance method virtual (the generated <c>Zabbix*Client</c>s, the Azure SDK client
    /// pattern); a concrete base that sealed public types of the same libraries extend, a designed base that is
    /// also used as it is, the <c>ConsoleFormatterOptions</c> → <c>SimpleConsoleFormatterOptions</c>/<c>JsonConsoleFormatterOptions</c>
    /// shape of <c>Microsoft.Extensions.Logging.Console</c>. <c>Zabbix.ZabbixListenerOptions</c> is that shape
    /// (a bare listener's, a push agent's trapper's and a proxy's trapper's options; <c>ZabbixPassiveAgentOptions</c>, sealed,
    /// extends it). Sealing it does not compile; making it abstract would need a role-less concrete twin for the bare listener.
    /// So the exemption is computed, not a named hole.
    /// </summary>
    [Fact]
    public void Public_classes_are_sealed_or_abstract()
        => DesignConformanceTests.AssertNone(UnsealedClasses(Libraries.PublicTypes));

    [Fact]
    public void The_sealed_rule_finds_an_open_concrete_class()
        => Assert.Equal([typeof(OpenProbe).FullName!], UnsealedClasses([typeof(OpenProbe), typeof(SealedProbe), typeof(AbstractProbe), typeof(MockableProbe)]));

    [Fact]
    public void The_sealed_rule_spares_a_mockable_client_s_dispose_pattern_but_not_a_plain_dispose()
        => Assert.Equal([typeof(DisposableUnpatternedProbe).FullName!], UnsealedClasses([typeof(DisposableMockableProbe), typeof(DisposableUnpatternedProbe)]));

    [Fact]
    public void The_sealed_rule_spares_an_aggregate_whose_properties_are_virtual()
        => Assert.Equal([typeof(OpenAggregateProbe).FullName!], UnsealedClasses([typeof(MockableAggregateProbe), typeof(OpenAggregateProbe)]));

    [Fact]
    public void The_sealed_rule_spares_a_concrete_base_only_while_a_sealed_type_extends_it()
    {
        Assert.Empty(UnsealedClasses([typeof(BaseProbe), typeof(LeafProbe)]));
        Assert.Equal([typeof(BaseProbe).FullName!], UnsealedClasses([typeof(BaseProbe)]));
    }

    /// <summary>
    /// No public interface is empty (docs/adr/0013-builders-verbs-and-typed-values.md, section 7): a builder capability
    /// carries <c>Name</c> and <c>Services</c>, as <c>IHttpClientBuilder</c> does, so an extension written against it can
    /// register what it adds. An interface counts the members it inherits; its internal members don't count, since no
    /// extension outside the library can call them.
    /// </summary>
    [Fact]
    public void No_public_interface_is_empty()
        => DesignConformanceTests.AssertNone(EmptyInterfaces(Libraries.PublicTypes));

    [Fact]
    public void The_interface_rule_finds_an_empty_one_and_spares_one_that_inherits_its_members()
        => Assert.Equal([typeof(IEmptyProbe).FullName!, typeof(IStillEmptyProbe).FullName!],
            EmptyInterfaces([typeof(IEmptyProbe), typeof(IStillEmptyProbe), typeof(ICapabilityProbe), typeof(IDerivedCapabilityProbe)]));

    internal static IEnumerable<string> EmptyInterfaces(IEnumerable<Type> types)
        => types.Where(type => type.IsInterface && !type.GetInterfaces().Prepend(type).Any(contract =>
                contract.GetMembers(BindingFlags.Public | BindingFlags.Instance | BindingFlags.Static | BindingFlags.DeclaredOnly).Length > 0))
            .Select(type => type.FullName!)
            .Order(StringComparer.Ordinal);

    /// <summary>
    /// Every public KEYED indexer throws for a missing key and has a <c>TryGet*</c> sibling. A
    /// positional one (<see cref="int"/>/<c>Index</c>) is the list idiom: bounds-checked, its count beside it. One that
    /// hands out a node looks nothing up: <c>zabbix.Hosts["web-01"]</c> makes no call until it's asked.
    /// </summary>
    [Fact]
    public void Every_public_indexer_has_a_try_get_sibling()
        => DesignConformanceTests.AssertNone(IndexersWithoutTryGet(Libraries.PublicTypes));

    [Fact]
    public void The_indexer_rule_finds_an_indexer_without_a_try_get()
        => Assert.Equal([$"{typeof(IndexerProbe).FullName}.Item"], IndexersWithoutTryGet([typeof(IndexerProbe)]));

    /// <summary>
    /// A public disposable type that
    /// holds a field whose own type is <see cref="IAsyncDisposable"/> (a stream, a timer, a service provider, a channel
    /// owner) implements <see cref="IAsyncDisposable"/> too, so a caller can release it without blocking. For example, an owner
    /// of a <c>ServiceProvider</c> that is only <see cref="IDisposable"/> fails: <c>ServiceProvider.Dispose()</c>
    /// throws when a resolved singleton is <see cref="IAsyncDisposable"/>-only, and <c>await using</c> is not available.
    /// </summary>
    [Fact]
    public void Disposable_types_holding_async_resources_are_async_disposable()
        => DesignConformanceTests.AssertNone(SyncOnlyDisposables(Libraries.PublicTypes));

    [Fact]
    public void The_async_disposal_rule_finds_a_sync_only_owner_of_a_stream()
        => Assert.Equal([$"{typeof(SyncOwnerProbe).FullName}._stream"], SyncOnlyDisposables([typeof(SyncOwnerProbe)]));

    /// <summary>
    /// Every parameter typed as a secret carries a classification: a member or parameter NAMED as a
    /// secret (a password, a PSK, an API or session token, a client secret) carries a <see cref="DataClassificationAttribute"/>,
    /// so the redactor sees it wherever it is logged. A record's positional parameter is covered by its property's attribute.
    /// This covers every secret field of the generated API records (<c>TlsPsk</c>,
    /// <c>IpmiPassword</c>, <c>Passwd</c>, <c>SslKeyPassword</c>, <c>ClientSecret</c>, <c>AccessToken</c>, SNMPv3 passphrases …),
    /// <c>ZabbixProxyTask.Password</c> and <c>ZabbixCredential.Invalidate(token)</c>. The runtime half (their <c>ToString()</c>
    /// prints them) is <c>AdversarialSecretTests.A_record_s_text_form_never_carries_a_secret_member</c>.
    /// </summary>
    [Fact]
    public void Every_secret_named_member_carries_a_data_classification()
        => DesignConformanceTests.AssertNone(UnclassifiedSecrets(Libraries.Types));

    [Fact]
    public void The_secret_rule_finds_an_unclassified_password()
        => Assert.Equal([$"{typeof(SecretProbe).FullName}.Password"], UnclassifiedSecrets([typeof(SecretProbe)]));

    /// <summary>
    /// An item key's parameters can carry a credential (<c>web.page.get[host,,user:password]</c>), so every
    /// <c>[LoggerMessage]</c> parameter typed <see cref="ZabbixItemKey"/> or named <c>key</c> carries a
    /// <see cref="DataClassificationAttribute"/> (<see cref="ZabbixItemKeyDataAttribute"/>). A key's bare name
    /// (<c>keyName</c>) is not a key and is not held to it.
    /// </summary>
    [Fact]
    public void Every_logged_item_key_carries_a_data_classification()
        => DesignConformanceTests.AssertNone(UnclassifiedLoggedKeys(DesignConformanceTests.LoggerMessages().Select(entry => entry.Method)));

    [Fact]
    public void The_item_key_rule_finds_an_unclassified_key()
        => Assert.Equal([$"{typeof(ItemKeyLogProbe).FullName}.Unclassified(key)", $"{typeof(ItemKeyLogProbe).FullName}.Unclassified(value)"],
            UnclassifiedLoggedKeys(typeof(ItemKeyLogProbe).GetMethods(BindingFlags.Public | BindingFlags.Static | BindingFlags.DeclaredOnly)));

    internal static IEnumerable<string> UnclassifiedLoggedKeys(IEnumerable<MethodInfo> methods)
        => methods
            .SelectMany(method => method.GetParameters()
                .Where(parameter => parameter.ParameterType == typeof(ZabbixItemKey) || string.Equals(parameter.Name, "key", StringComparison.OrdinalIgnoreCase))
                .Where(parameter => !Classified(parameter.GetCustomAttributesData()))
                .Select(parameter => $"{method.DeclaringType!.FullName}.{method.Name}({parameter.Name})"))
            .Order(StringComparer.Ordinal);

    // ================================================================================================================
    // the rules' machinery

    private static readonly Lazy<IReadOnlyList<SyntaxTree>> AllLibraryTrees = new(() => [.. LibraryProjects()
        .SelectMany(project => Directory.EnumerateFiles(Path.Combine(Repository.Root, "src", project), "*.cs", SearchOption.AllDirectories)
            .Where(file => !IsBuildOutput(file))
            .Select(file => Sources.ParseText(File.ReadAllText(file), file)))]);

    private static bool IsBuildOutput(string file)
    {
        string normalized = file.Replace('\\', '/');
        return normalized.Contains("/obj/", StringComparison.Ordinal) || normalized.Contains("/bin/", StringComparison.Ordinal);
    }

    /// <summary>The solution's library projects: every <c>src/</c> project of <c>Zabbix.slnx</c> that is neither an executable nor a Roslyn component.</summary>
    internal static IEnumerable<string> LibraryProjects()
        => XDocument.Load(Path.Combine(Repository.Root, "Zabbix.slnx")).Descendants("Project")
            .Select(project => (string)project.Attribute("Path")!)
            .Where(path => path.StartsWith("src/", StringComparison.Ordinal))
            .Where(path =>
            {
                string csproj = File.ReadAllText(Path.Combine(Repository.Root, path));
                return !csproj.Contains("<OutputType>Exe</OutputType>", StringComparison.Ordinal)
                    && !csproj.Contains("<IsRoslynComponent>true</IsRoslynComponent>", StringComparison.Ordinal);
            })
            .Select(path => Path.GetFileNameWithoutExtension(path));

    private static IEnumerable<AnalyzerProbe.ProbeError> GapMarkLines()
        => File.ReadLines(Path.Combine(AnalyzerProbe.Directory, "Adversarial.cs"))
            .Select((text, index) => (Text: text, Line: index + 1))
            .Select(line => (line.Line, Mark: GapMark().Match(line.Text)))
            .Where(line => line.Mark.Success)
            .Select(line => new AnalyzerProbe.ProbeError("Adversarial.cs", line.Line, line.Mark.Groups["id"].Value));

    // A gap closed by the ban list is promoted from "gap:" to "expect:" and stays one of these rows.
    [GeneratedRegex(@"//\s*(?:gap|expect):\s*(?<id>[A-Za-z]+[0-9]+)\s*$")]
    private static partial Regex GapMark();

    private static IEnumerable<string> GeneratedFiles()
        => Directory.EnumerateFiles(Path.Combine(Repository.Root, "src"), "*.g.cs", SearchOption.AllDirectories)
            .Where(file => file.Replace('\\', '/').Contains("/generated/Zabbix.Generator/", StringComparison.Ordinal));

    private static IEnumerable<string> GeneratedOffenders()
    {
        foreach (string file in GeneratedFiles())
        {
            string text = File.ReadAllText(file);
            string generator = Path.GetFileName(Path.GetDirectoryName(file)!);
            if (text.Contains("<auto-generated", StringComparison.Ordinal)) yield return $"{generator}/{Path.GetFileName(file)}: <auto-generated> marker";
            if (text.Split('\n').Any(line => line.Trim() == "#pragma warning disable")) yield return $"{generator}/{Path.GetFileName(file)}: blanket #pragma warning disable";
        }
    }

    private static string Where(SyntaxNode node)
        => $"{Path.GetFileName(node.SyntaxTree.FilePath)}:{node.GetLocation().GetLineSpan().StartLinePosition.Line + 1}";

    /// <summary>The location as <c>{project}/{file}:{line}</c>, the project being the directory under <c>src/</c>.</summary>
    private static string ProjectWhere(SyntaxNode node)
    {
        string relative = Path.GetRelativePath(Path.Combine(Repository.Root, "src"), node.SyntaxTree.FilePath).Replace('\\', '/');
        string project = relative.Split('/')[0];
        return $"{project}/{Where(node)}";
    }

    internal static IEnumerable<string> SwallowingCatchAllsExceptHandedOnInTheFilter(IReadOnlyList<SyntaxTree> trees)
    {
        HashSet<string> offenders = [.. DesignConformanceTests.SwallowingCatchAlls(trees)];
        foreach (SyntaxTree tree in trees)
        {
            foreach (CatchClauseSyntax clause in tree.GetRoot().DescendantNodes().OfType<CatchClauseSyntax>())
            {
                string at = Where(clause);
                if (!offenders.Contains(at)) continue;
                string? variable = clause.Declaration?.Identifier.ValueText;
                bool handedOn = variable is { Length: > 0 } && clause.Filter is { } filter && filter.FilterExpression.DescendantNodes().OfType<ArgumentSyntax>()
                    .Any(argument => argument.Expression is IdentifierNameSyntax name && name.Identifier.ValueText == variable);
                if (!handedOn) yield return at;
            }
        }
    }

    internal static IEnumerable<string> AsyncVoids(IEnumerable<SyntaxTree> trees)
        => trees.SelectMany(tree => tree.GetRoot().DescendantNodes())
            .Where(node => node switch
            {
                MethodDeclarationSyntax method => method.Modifiers.Any(SyntaxKind.AsyncKeyword) && method.ReturnType is PredefinedTypeSyntax { Keyword.RawKind: (int)SyntaxKind.VoidKeyword },
                LocalFunctionStatementSyntax local => local.Modifiers.Any(SyntaxKind.AsyncKeyword) && local.ReturnType is PredefinedTypeSyntax { Keyword.RawKind: (int)SyntaxKind.VoidKeyword },
                _ => false,
            })
            .Select(Where);

    internal static IEnumerable<string> ConsoleUses(IEnumerable<SyntaxTree> trees)
        => trees.SelectMany(tree => tree.GetRoot().DescendantNodes().OfType<MemberAccessExpressionSyntax>())
            .Where(access => access.Expression.ToString() is "Console" or "System.Console" or "global::System.Console")
            .Select(Where);

    internal static IEnumerable<string> SystemClockReads(IEnumerable<SyntaxTree> trees)
        => trees.SelectMany(tree => tree.GetRoot().DescendantNodes().OfType<MemberAccessExpressionSyntax>())
            .Where(access => access.Name.Identifier.ValueText == "System" && access.Expression.ToString() is "TimeProvider" or "System.TimeProvider" or "global::System.TimeProvider")
            .Where(access => !IsCompositionDefault(access))
            .Select(ProjectWhere);

    private static bool IsCompositionDefault(ExpressionSyntax read)
    {
        SyntaxNode? parent = read.Parent;
        while (parent is ParenthesizedExpressionSyntax) parent = parent.Parent;
        return parent switch
        {
            BinaryExpressionSyntax { RawKind: (int)SyntaxKind.CoalesceExpression } coalesce => coalesce.Right == read,
            AssignmentExpressionSyntax { RawKind: (int)SyntaxKind.CoalesceAssignmentExpression } => true,
            AssignmentExpressionSyntax { RawKind: (int)SyntaxKind.SimpleAssignmentExpression } assignment => assignment.Right == read && assignment.FirstAncestorOrSelf<ConstructorDeclarationSyntax>() is not null,
            EqualsValueClauseSyntax { Parent: PropertyDeclarationSyntax or VariableDeclaratorSyntax { Parent.Parent: FieldDeclarationSyntax } } => true,
            ArgumentSyntax { Parent.Parent: InvocationExpressionSyntax invocation } => InvokedName(invocation) is { } name && (name.StartsWith("TryAdd", StringComparison.Ordinal) || name.StartsWith("Add", StringComparison.Ordinal)),
            _ => false,
        };
    }

    private static string? InvokedName(InvocationExpressionSyntax invocation) => invocation.Expression switch
    {
        MemberAccessExpressionSyntax member => member.Name.Identifier.ValueText,
        IdentifierNameSyntax name => name.Identifier.ValueText,
        GenericNameSyntax generic => generic.Identifier.ValueText,
        _ => null,
    };

    internal static IEnumerable<string> WallClockDurations(IEnumerable<SyntaxTree> trees)
    {
        foreach (SyntaxTree tree in trees)
        {
            foreach (BinaryExpressionSyntax binary in tree.GetRoot().DescendantNodes().OfType<BinaryExpressionSyntax>())
            {
                bool subtract = binary.IsKind(SyntaxKind.SubtractExpression);
                bool compare = binary.Kind() is SyntaxKind.LessThanExpression or SyntaxKind.LessThanOrEqualExpression or SyntaxKind.GreaterThanExpression or SyntaxKind.GreaterThanOrEqualExpression;
                if (!subtract && !compare) continue;
                ExpressionSyntax? other = IsWallRead(binary.Left) ? binary.Right : IsWallRead(binary.Right) ? binary.Left : null;
                if (other is not IdentifierNameSyntax name) continue;
                if (WallInstantLocal(binary, name.Identifier.ValueText) || (compare && DeadlineName().IsMatch(name.Identifier.ValueText)))
                    yield return ProjectWhere(binary);
            }
        }
    }

    private static bool IsWallRead(ExpressionSyntax expression)
        => expression is InvocationExpressionSyntax { Expression: MemberAccessExpressionSyntax { Name.Identifier.ValueText: "GetUtcNow" } };

    /// <summary>Whether <paramref name="name"/> is a local of the enclosing member initialised from a wall-clock reading.</summary>
    private static bool WallInstantLocal(SyntaxNode at, string name)
        => at.Ancestors().FirstOrDefault(node => node is BaseMethodDeclarationSyntax or LocalFunctionStatementSyntax or AccessorDeclarationSyntax) is { } member
           && member.DescendantNodes().OfType<VariableDeclaratorSyntax>()
               .Any(local => local.Identifier.ValueText == name && local.Initializer?.Value.DescendantNodesAndSelf().OfType<ExpressionSyntax>().Any(IsWallRead) == true);

    [GeneratedRegex("^_?(deadline|expires|expiry|expiration)$", RegexOptions.IgnoreCase)]
    private static partial Regex DeadlineName();

    internal static IEnumerable<string> UnsealedClasses(IEnumerable<Type> types)
    {
        List<Type> all = [.. types];
        return all.Where(type => type is { IsClass: true, IsSealed: false, IsAbstract: false } && !typeof(Delegate).IsAssignableFrom(type))
            .Where(type => !DesignConformanceTests.IsCompilerGenerated(type) && !IsMockableClient(type) && !IsAgentOrProxy(type))
            .Where(type => !all.Any(other => other.IsSealed && other.BaseType == type))
            .Select(type => type.FullName!);
    }

    // An agent or a proxy is a type an application derives from to add to it, as the Framework Design Guidelines' sealing
    // page asks for ("DO NOT seal classes without having a good reason"); its members stay non-virtual and its phases internal,
    // so a derived class cannot break it (docs/adr/0010-agent-proxy-client-classes.md §2).
    private static bool IsAgentOrProxy(Type type) => typeof(ZabbixComponent).IsAssignableFrom(type);

    // The dispose pattern's public Dispose() and DisposeAsync() stay non-virtual by design: a derived type overrides the
    // protected virtual Dispose(bool) or DisposeAsyncCore() the type declares beside them.
    // A property's accessors count as its members: an aggregate of clients (ZabbixApi) is mockable through its virtual
    // client properties.
    private static bool IsMockableClient(Type type)
        => type.GetMethods(BindingFlags.Public | BindingFlags.Instance | BindingFlags.DeclaredOnly).Where(method => (!method.IsSpecialName || IsAccessor(method)) && !IsDisposePatternEntry(type, method)).ToList() is { Count: > 0 } methods
           && methods.All(method => (method.IsVirtual && !method.IsFinal) || methods.Any(other => other.Name == method.Name && other.IsVirtual && !other.IsFinal));   // a shorter overload calls through the virtual one

    private static bool IsAccessor(MethodInfo method)
        => method.IsSpecialName && (method.Name.StartsWith("get_", StringComparison.Ordinal) || method.Name.StartsWith("set_", StringComparison.Ordinal));

    private static bool IsDisposePatternEntry(Type type, MethodInfo method)
        => method.GetParameters().Length == 0 && method.Name switch
        {
            "Dispose" => type.GetMethod("Dispose", BindingFlags.NonPublic | BindingFlags.Instance, [typeof(bool)]) is { IsVirtual: true, IsFamily: true },
            "DisposeAsync" => type.GetMethod("DisposeAsyncCore", BindingFlags.NonPublic | BindingFlags.Instance, Type.EmptyTypes) is { IsVirtual: true, IsFamily: true },
            _ => false,
        };

    internal static IEnumerable<string> IndexersWithoutTryGet(IEnumerable<Type> types)
        => types.SelectMany(type => type.GetProperties(BindingFlags.Public | BindingFlags.Instance | BindingFlags.DeclaredOnly)
                .Where(property => property.GetIndexParameters().Length > 0)
                .Where(property => !type.GetMethods(BindingFlags.Public | BindingFlags.Instance).Any(method => method.Name.StartsWith("TryGet", StringComparison.Ordinal)))
                .Where(property => !(property.GetIndexParameters() is [{ ParameterType: var index }] && (index == typeof(int) || index == typeof(Index))))
                .Where(property => !IsLazyNode(property.PropertyType))
                .Select(property => $"{type.FullName}.{property.Name}"));

    // An indexer that hands out a node reads nothing and throws for nothing: the node's first call answers.
    private static bool IsLazyNode(Type type)
    {
        if (typeof(IZabbixNode).IsAssignableFrom(type)) return true;
        for (Type? current = type; current is not null; current = current.BaseType)
            if (current.IsGenericType && current.GetGenericTypeDefinition() == typeof(ZabbixObjectNode<>)) return true;
        return false;
    }

    internal static IEnumerable<string> SyncOnlyDisposables(IEnumerable<Type> types)
        => types.Where(type => typeof(IDisposable).IsAssignableFrom(type) && !typeof(IAsyncDisposable).IsAssignableFrom(type) && !type.IsInterface)
            .SelectMany(type => type.GetFields(BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.Public | BindingFlags.DeclaredOnly)
                .Where(field => typeof(IAsyncDisposable).IsAssignableFrom(field.FieldType))
                .Select(field => $"{type.FullName}.{field.Name}"));

    [GeneratedRegex("(password|passwd|passphrase|psk|presharedkeys|secret|token|sessionid)$|^_?sid$", RegexOptions.IgnoreCase)]
    private static partial Regex SecretName();

    internal static IEnumerable<string> UnclassifiedSecrets(IEnumerable<Type> types)
    {
        const BindingFlags all = BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance | BindingFlags.Static | BindingFlags.DeclaredOnly;
        foreach (Type type in types.Where(type => !DesignConformanceTests.IsCompilerGenerated(type)))
        {
            foreach (PropertyInfo property in type.GetProperties(all).Where(property => SecretName().IsMatch(property.Name) && CarriesText(property.PropertyType)))
                if (!Classified(property.GetCustomAttributesData())) yield return $"{type.FullName}.{property.Name}";
            foreach (MethodBase method in type.GetMethods(all).Cast<MethodBase>().Concat(type.GetConstructors(all)))
            {
                if (method.IsPrivate || DesignConformanceTests.IsCompilerGenerated(method)) continue;
                foreach (ParameterInfo parameter in method.GetParameters().Where(parameter => SecretName().IsMatch(parameter.Name ?? "") && CarriesText(parameter.ParameterType)))
                {
                    if (Classified(parameter.GetCustomAttributesData())) continue;
                    PropertyInfo? twin = type.GetProperty(parameter.Name!, all | BindingFlags.IgnoreCase);
                    if (twin is not null && Classified(twin.GetCustomAttributesData())) continue;           // a record's positional parameter
                    yield return $"{type.FullName}.{(method is ConstructorInfo ? ".ctor" : method.Name)}({parameter.Name})";
                }
            }
        }
    }

    private static bool CarriesText(Type type)
        => type == typeof(string) || type == typeof(byte[]) || type == typeof(ReadOnlyMemory<byte>) || type == typeof(ReadOnlyMemory<char>)
           || (type.IsGenericType && type.GetGenericArguments().Contains(typeof(string)) && typeof(System.Collections.IEnumerable).IsAssignableFrom(type));

    private static bool Classified(IEnumerable<CustomAttributeData> attributes)
        => attributes.Any(attribute => typeof(DataClassificationAttribute).IsAssignableFrom(attribute.AttributeType));
}

// ---- probes the rules must find ----

#pragma warning disable CA1852, CA1812, CA1822, CA1515 // the probes are deliberately shaped wrong
public class OpenProbe;

public sealed class SealedProbe;

/// <summary>A concrete base a sealed leaf extends (the sealed rule spares it).</summary>
public class BaseProbe;

/// <summary>The sealed leaf of <see cref="BaseProbe"/>.</summary>
public sealed class LeafProbe : BaseProbe;

public abstract class AbstractProbe;

public sealed class IndexerProbe
{
    public string this[string key] => key;
}

public class MockableProbe
{
    public virtual int Read() => 1;
}

public class MockableAggregateProbe
{
    public virtual MockableProbe Reader { get; } = new();
}

public class OpenAggregateProbe
{
    public MockableProbe Reader { get; } = new();
}

public class DisposableMockableProbe : IDisposable
{
    public virtual int Read() => 1;

    public void Dispose()
    {
        Dispose(disposing: true);
        GC.SuppressFinalize(this);
    }

    protected virtual void Dispose(bool disposing)
    {
    }
}

public class DisposableUnpatternedProbe
{
    public virtual int Read() => 1;

    public void Dispose() => _ = Read();
}

public sealed class SyncOwnerProbe : IDisposable
{
    private readonly MemoryStream _stream = new();

    public void Dispose() => _stream.Dispose();
}

public sealed class SecretProbe
{
    public string? Password { get; set; }

    [Community.Zabbix.ZabbixSecret]
    public string? Psk { get; set; }
}
#pragma warning restore CA1852, CA1812, CA1822, CA1515

#pragma warning disable CA1040 // the probes are deliberately empty
/// <summary>An interface the interface rule must find: no member.</summary>
public interface IEmptyProbe
{
}

/// <summary>An interface the interface rule must find: it inherits no member either.</summary>
public interface IStillEmptyProbe : IEmptyProbe
{
}
#pragma warning restore CA1040

/// <summary>A builder capability the interface rule spares.</summary>
public interface ICapabilityProbe
{
    /// <summary>Gets the name.</summary>
    string Name { get; }
}

/// <summary>A capability whose members are inherited, which the interface rule spares.</summary>
public interface IDerivedCapabilityProbe : ICapabilityProbe
{
}

/// <summary>Violations the logged-item-key rule must find: a parameter named <c>key</c> and one typed <see cref="ZabbixItemKey"/>, each unclassified.</summary>
public static class ItemKeyLogProbe
{
    public static void Unclassified(string key, ZabbixItemKey value, string keyName) => _ = (key, value, keyName);

    public static void Classified([ZabbixItemKeyData] string key, [ZabbixItemKeyData] ZabbixItemKey value) => _ = (key, value);
}
