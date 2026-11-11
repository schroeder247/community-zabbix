// This Source Code Form is subject to the terms of the Mozilla Public
// License, v. 2.0. Copyright (c) 2026 schroeder247. SPDX-License-Identifier: MPL-2.0
using System.Globalization;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;

namespace Community.Zabbix.Tests;

/// <summary>
/// What the compiler makes of a write as a caller types it (ADR 0013, section 4): an initializer that leaves out a required
/// field doesn't compile, a constructor stands in for the required fields, a node's lambda can't change the id, and each
/// create and update binds the overload a reader expects, with a statement lambda, an expression lambda, a target-typed
/// request and a list of them.
/// </summary>
/// <remarks>Compiling allocates, so these run in the probes' collection, as the documents' samples do.</remarks>
[Collection(nameof(ChannelsProbeTests))]
public sealed class RequestCompileTests
{
    private const string Usings = """
        using System;
        using System.Collections.Generic;
        using System.Net;
        using System.Threading;
        using System.Threading.Tasks;
        using Community.Zabbix;

        """;

    private const string Declarations = """
        ZabbixClient zabbix = null!;
        ulong linux = 42;
        ulong itemId = 7;
        CancellationToken cancellationToken = default;

        """;

    private static readonly SymbolDisplayFormat Signature = new(
        typeQualificationStyle: SymbolDisplayTypeQualificationStyle.NameOnly,
        genericsOptions: SymbolDisplayGenericsOptions.IncludeTypeParameters,
        memberOptions: SymbolDisplayMemberOptions.IncludeContainingType | SymbolDisplayMemberOptions.IncludeParameters,
        parameterOptions: SymbolDisplayParameterOptions.IncludeType,
        miscellaneousOptions: SymbolDisplayMiscellaneousOptions.UseSpecialTypes);

    private static readonly Lazy<IReadOnlyList<MetadataReference>> s_references = new(() =>
        [.. ((string)AppContext.GetData("TRUSTED_PLATFORM_ASSEMBLIES")!).Split(Path.PathSeparator)
            .Where(path => !Path.GetFileName(path).StartsWith("xunit", StringComparison.OrdinalIgnoreCase)
                && !Path.GetFileName(path).StartsWith("Microsoft.Testing", StringComparison.OrdinalIgnoreCase)
                && !Path.GetFileName(path).Contains("Zabbix.Tests", StringComparison.Ordinal))
            .Select(path => MetadataReference.CreateFromFile(path))]);

    [Fact]
    public void A_request_initializer_that_leaves_out_a_required_field_does_not_compile()
    {
        Diagnostic missing = Assert.Single(Errors("""var request = new ZabbixHostCreateRequest { TechnicalName = "web-02" };"""));

        Assert.Equal("CS9035", missing.Id);
        Assert.Contains("ZabbixHostCreateRequest.Groups", missing.GetMessage(CultureInfo.InvariantCulture), StringComparison.Ordinal);
    }

    [Fact]
    public void The_constructor_sets_the_required_fields_and_an_initializer_or_a_setter_the_others()
        => Assert.Empty(Errors("""
            var request = new ZabbixHostCreateRequest("web-02", [1UL]) { Tags = [("env", "prod")] };
            request.Status = ZabbixHostStatus.Disabled;
            ZabbixTagRequest tag = ("env", "prod");
            ZabbixHostMacroRequest macro = ("{$SNMP_COMMUNITY}", "public");
            """));

    [Fact]
    public void A_node_s_lambda_cannot_change_the_id_it_updates()
    {
        Diagnostic refused = Assert.Single(Errors("""await zabbix.Hosts[1UL].UpdateAsync(h => h.HostId = 2);"""));

        Assert.Equal("CS8852", refused.Id);
    }

    [Fact]
    public void Typed_fields_values_and_a_field_the_sdk_does_not_know_compile_where_a_caller_writes_them()
        => Assert.Empty(Errors("""
            var hosts = new ZabbixHostGetParameters
            {
                Output = [ZabbixHostFields.HostId, ZabbixHostFields.VisibleName, new ZabbixField<ZabbixHost>("a_new_field")],
                SelectInterfaces = ZabbixOutput.Extend,
                SelectTags = [ZabbixTagFields.Tag, ZabbixTagFields.Value],
                SelectItems = ZabbixOutput.Count,
                Sort = [ZabbixHostFields.VisibleName.Descending(), ZabbixHostFields.HostId],
            };
            NewestFirst(hosts, ZabbixHostFields.HostId);
            await zabbix.Items.CreateAsync(1UL, "Requests", ZabbixItemType.Trapper, "app.requests", ZabbixItemValueType.Unsigned, i =>
            {
                i.TrapperHosts = [IPNetwork.Parse("10.0.0.0/8"), IPAddress.IPv6Loopback, ZabbixPeer.Parse("{HOST.CONN}")];
                i.History = TimeSpan.FromDays(90);
                i.Trends = TimeSpan.Zero;
            });
            await zabbix.Items[itemId].UpdateAsync(i =>
            {
                i.Delay = ZabbixInterval.Parse("1m;10s/1-5,09:00-18:00");
                i.Timeout = ZabbixDuration.Parse("{$AGENT.TIMEOUT}");
            });
            await zabbix.HostInterfaces.CreateAsync(10084, ZabbixInterface.Agent(IPAddress.Parse("10.0.0.2")) with { Port = ZabbixPort.Parse("{$AGENT_PORT}") });
            ZabbixHostInterfaceRequest snmp = ZabbixInterface.Snmp(IPAddress.Parse("10.0.0.2")) with { Port = 1161 };

            static void NewestFirst<TRecord>(IZabbixGetParameters<TRecord> parameters, ZabbixField<TRecord> field) => parameters.Sort = [field.Descending()];
            """));

    // Each refusal is the compiler's "cannot convert": a field converts only to its own record's output and sort, and text
    // only through Parse.
    [Theory]
    [InlineData("""var q = new ZabbixItemGetParameters { Output = [ZabbixHostFields.HostId] };""")]
    [InlineData("""var q = new ZabbixItemGetParameters { Sort = [ZabbixHostFields.HostId] };""")]
    [InlineData("""var q = new ZabbixItemGetParameters { Sort = [ZabbixHostFields.HostId.Descending()] };""")]
    [InlineData("""var q = new ZabbixHostGetParameters { SelectInterfaces = [ZabbixHostFields.HostId] };""")]
    [InlineData("""var q = new ZabbixHostGetParameters { Output = ["hostid"] };""")]
    [InlineData("""await zabbix.Items[itemId].UpdateAsync(i => i.Delay = "5m");""")]
    [InlineData("""await zabbix.Items[itemId].UpdateAsync(i => i.TrapperHosts = "10.0.0.0/8");""")]
    [InlineData("""ZabbixHostInterfaceRequest agent = ZabbixInterface.Agent(IPAddress.Loopback) with { Port = "10050" };""")]
    public void A_field_of_another_record_or_text_where_a_typed_value_goes_does_not_compile(string statement)
        => Assert.Equal("CS0029", Assert.Single(Errors(statement)).Id);

    public static TheoryData<string, string> Writes => new()
    {
        {
            """
            ulong id = await zabbix.Hosts.CreateAsync("web-02", [linux], h =>
            {
                h.Interfaces = [ZabbixInterface.Agent(IPAddress.Parse("10.0.0.2"))];
                h.Tags = [("env", "prod")];
            });
            """,
            "ZabbixHostClient.CreateAsync(string, IReadOnlyList<ZabbixHostGroupRequest>, Action<ZabbixHostCreateRequest>)"
        },
        { """await zabbix.Hosts.CreateAsync("web-02", [linux], h => h.Status = ZabbixHostStatus.Disabled);""", "ZabbixHostClient.CreateAsync(string, IReadOnlyList<ZabbixHostGroupRequest>, Action<ZabbixHostCreateRequest>)" },
        { """await zabbix.Hosts.CreateAsync("web-02", [linux], h => h.Status = ZabbixHostStatus.Disabled, cancellationToken);""", "ZabbixHostClient.CreateAsync(string, IReadOnlyList<ZabbixHostGroupRequest>, Action<ZabbixHostCreateRequest>, CancellationToken)" },
        { """await zabbix.Hosts.CreateAsync("web-02", [linux]);""", "ZabbixHostClient.CreateAsync(string, IReadOnlyList<ZabbixHostGroupRequest>)" },
        { """await zabbix.Hosts.CreateAsync("web-02", [linux], cancellationToken);""", "ZabbixHostClient.CreateAsync(string, IReadOnlyList<ZabbixHostGroupRequest>, CancellationToken)" },
        { """await zabbix.Hosts.CreateAsync(new("web-02", [linux]) { Tags = [("env", "prod")] });""", "ZabbixHostClient.CreateAsync(ZabbixHostCreateRequest)" },
        { """await zabbix.Hosts.CreateAsync(new("web-02", [linux]) { Tags = [("env", "prod")] }, cancellationToken);""", "ZabbixHostClient.CreateAsync(ZabbixHostCreateRequest, CancellationToken)" },
        { """await zabbix.Hosts.CreateAsync([new("web-02", [linux]), new("web-03", [linux])]);""", "ZabbixHostClient.CreateAsync(IEnumerable<ZabbixHostCreateRequest>)" },
        { """await zabbix.HostGroups.CreateAsync(new("Linux servers") { Uuid = "6f1ed29f0b1a4b4e8c3a8c4b2e5d7a90" });""", "ZabbixHostGroupClient.CreateAsync(ZabbixHostGroupCreateRequest)" },
        { """await zabbix.HostGroups.CreateAsync("Linux servers", g => g.Uuid = "6f1ed29f0b1a4b4e8c3a8c4b2e5d7a90");""", "ZabbixHostGroupClient.CreateAsync(string, Action<ZabbixHostGroupCreateRequest>)" },
        { """await zabbix.Items.CreateAsync(1UL, "Requests", ZabbixItemType.Trapper, "app.requests", ZabbixItemValueType.Unsigned, i => { i.TrapperHosts = [IPNetwork.Parse("10.0.0.0/8"), IPAddress.IPv6Loopback]; i.Delay = TimeSpan.Zero; });""", "ZabbixItemClient.CreateAsync(ulong, string, ZabbixItemType, string, ZabbixItemValueType, Action<ZabbixItemCreateRequest>)" },
        { """await zabbix.Hosts.UpdateAsync(new(10084) { Status = ZabbixHostStatus.Disabled });""", "ZabbixHostClient.UpdateAsync(ZabbixHostUpdateRequest)" },
        { """await zabbix.Hosts["web-02"].UpdateAsync(h => h.Status = ZabbixHostStatus.Disabled);""", "ZabbixHostNode.UpdateAsync(Action<ZabbixHostUpdateRequest>, CancellationToken)" },
        { """await zabbix.Hosts[10084UL].UpdateAsync(h => { h.Status = ZabbixHostStatus.Disabled; h.Tags = [("env", "prod")]; });""", "ZabbixUpdatableObjectNode<ZabbixHost, ZabbixHostUpdateRequest>.UpdateAsync(Action<ZabbixHostUpdateRequest>, CancellationToken)" },
        { """await zabbix.Items[itemId].UpdateAsync(i => i.Delay = TimeSpan.FromMinutes(5));""", "ZabbixUpdatableObjectNode<ZabbixItem, ZabbixItemUpdateRequest>.UpdateAsync(Action<ZabbixItemUpdateRequest>, CancellationToken)" },
        { """await zabbix.HostGroups["Linux"].UpdateAsync(g => g.Name = "Linux servers");""", "ZabbixHostGroupNode.UpdateAsync(Action<ZabbixHostGroupUpdateRequest>, CancellationToken)" },
        { """await zabbix.Hosts["web-01"].Item("agent.ping").UpdateAsync(i => i.Status = ZabbixItemStatus.Disabled);""", "ZabbixItemNode.UpdateAsync(Action<ZabbixItemUpdateRequest>, CancellationToken)" },
        { """await zabbix.HostInterfaces.CreateAsync(10084, ZabbixInterface.Snmp(IPAddress.Parse("10.0.0.2")));""", "ZabbixHostInterfaceClient.CreateAsync(ulong, ZabbixHostInterfaceRequest)" },
    };

    [Theory]
    [MemberData(nameof(Writes))]
    public void A_write_binds_the_overload_a_reader_expects(string write, string overload)
    {
        (CSharpCompilation compilation, SyntaxTree tree) = Compile(Declarations + write);

        Assert.Empty(Errors(compilation));
        InvocationExpressionSyntax call = tree.GetRoot(TestContext.Current.CancellationToken).DescendantNodes().OfType<InvocationExpressionSyntax>()
            .First(invocation => invocation.Expression is MemberAccessExpressionSyntax { Name.Identifier.ValueText: "CreateAsync" or "UpdateAsync" });
        var bound = (IMethodSymbol)compilation.GetSemanticModel(tree).GetSymbolInfo(call, TestContext.Current.CancellationToken).Symbol!;
        Assert.Equal(overload, bound.ToDisplayString(Signature));
    }

    private static List<Diagnostic> Errors(string statements) => Errors(Compile(Declarations + statements).Compilation);

    private static List<Diagnostic> Errors(CSharpCompilation compilation)
        => [.. compilation.GetDiagnostics(TestContext.Current.CancellationToken).Where(diagnostic => diagnostic.Severity == DiagnosticSeverity.Error)];

    private static (CSharpCompilation Compilation, SyntaxTree Tree) Compile(string statements)
    {
        SyntaxTree tree = CSharpSyntaxTree.ParseText(Usings + statements, new CSharpParseOptions(LanguageVersion.CSharp14), cancellationToken: TestContext.Current.CancellationToken);
        CSharpCompilation compilation = CSharpCompilation.Create(
            "RequestProbe",
            [tree],
            s_references.Value,
            new CSharpCompilationOptions(OutputKind.ConsoleApplication, nullableContextOptions: NullableContextOptions.Enable));
        return (compilation, tree);
    }
}
