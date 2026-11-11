// This Source Code Form is subject to the terms of the Mozilla Public
// License, v. 2.0. Copyright (c) 2026 schroeder247. SPDX-License-Identifier: MPL-2.0
using System.Management.Automation;
using System.Reflection;
using System.Text.Json;
using System.Xml.Linq;

namespace Community.Zabbix.PowerShell.Tests;

/// <summary>
/// The module surface under hostile input: error kinds, format views, casts with the wrong types, <c>-AdditionalProperty</c>
/// shapes, and the frontend page command fed every pipeline form.
/// </summary>
public sealed class AdversarialSurfaceTests
{
    public static TheoryData<ZabbixApiErrorKind> Kinds => [.. Enum.GetValues<ZabbixApiErrorKind>()];

    [Theory]
    [MemberData(nameof(Kinds))]
    public void Every_api_error_kind_has_a_stable_id_and_a_category_of_its_own(ZabbixApiErrorKind kind)
    {
        // The id is the kind's name (never the server's prose), and only the kind the SDK could not classify is
        // NotSpecified: a script's catch on a category must see every classified failure.
        var failure = new ZabbixApiException(-32500, "Application error.", "detail", "host.get");
        // The kind is the SDK's classification of code and text; every value is reached here by setting it directly.
        typeof(ZabbixApiException).GetField("<Kind>k__BackingField", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance)!.SetValue(failure, kind);
        ErrorRecord record = ZabbixErrors.From(failure);

        Assert.Equal("ZabbixApi." + kind, record.FullyQualifiedErrorId);
        Assert.Equal(kind == ZabbixApiErrorKind.Unknown, record.CategoryInfo.Category == ErrorCategory.NotSpecified);
    }

    private static string[] Views => [.. ModuleUnderTest.All
        .SelectMany(module => Directory.EnumerateFiles(module.BuildDirectory, "*.format.ps1xml"))
        .SelectMany(file => XDocument.Load(file).Descendants("ViewSelectedBy").Elements("TypeName").Select(type => type.Value))
        .Distinct(StringComparer.Ordinal)
        .Order(StringComparer.Ordinal)];

    [Fact]
    public void Every_format_view_names_a_type_the_modules_load()
    {
        // A view left on a name that matches no type formats nothing and fails nowhere, so each view names a type the
        // modules export.
        using Shell shell = Shell.Open();

        // A name that is no CLR type must be an ETS type name a command declares it writes ([OutputType("Community.Zabbix.PowerShell.ServerInfo")]).
        string[] declared = [.. ModuleUnderTest.AllCmdlets.SelectMany(type => type.GetCustomAttributes<OutputTypeAttribute>(true))
            .SelectMany(output => output.Type).Where(name => name.Type is null).Select(name => name.Name)];
        Run run = shell.Invoke("$types | Where-Object { -not ($_ -as [type]) -and $_ -notin $declared }", new Dictionary<string, object?> { ["types"] = Views, ["declared"] = declared });

        Assert.Empty(run.Errors);
        Assert.Empty(run.Output);
        Assert.True(Views.Length > 100, $"{Views.Length} views");
    }

    [Theory]
    [InlineData("[Community.Zabbix.ZabbixHostCreateRequest]@{ Host = 'x'; Groups = 'not a list of groups' }", "$.groups")]
    [InlineData("[Community.Zabbix.ZabbixHostCreateRequest]@{ Host = @{ nested = 1 } }", "$.host")]
    [InlineData("[Community.Zabbix.ZabbixHostCreateRequest]@{ Host = 'x'; Groups = @(@{ groupid = 1 }); Status = 'sideways' }", "'sideways' is not a ZabbixHostStatus")]
    [InlineData("[Community.Zabbix.ZabbixHostGetParameters]@{ Limit = 'many' }", "$.limit")]
    public void A_record_cast_with_a_wrong_type_is_a_conversion_error_naming_the_field(string cast, string named)
    {
        using Shell shell = Shell.Open();

        Run run = shell.Invoke($"try {{ {cast} | Out-Null; 'converted' }} catch {{ $_.Exception.Message }}");

        Assert.Empty(run.Errors);
        string message = Assert.Single(run.Output).ToString();
        Assert.StartsWith("Cannot convert value", message, StringComparison.Ordinal);
        Assert.Contains(named, message, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("[ordered]@{ description = 'ordered'; proxy_hostid = '0' }", "description", "\"ordered\"")]
    [InlineData("@{ macros = @(@{ macro = '{$A}'; value = '1' }, @{ macro = '{$B}'; value = '2' }) }", "macros", "[{\"macro\":\"{$A}\",\"value\":\"1\"},{\"macro\":\"{$B}\",\"value\":\"2\"}]")]
    [InlineData("@{ inventory = @{ os = 'Linux' } }", "inventory", "{\"os\":\"Linux\"}")]
    [InlineData("@{ no_such_field = 'kept' }", "no_such_field", "\"kept\"")]
    public async Task Properties_shapes_reach_the_wire_as_given(string properties, string field, string json)
    {
        await using FakeZabbix server = await FakeZabbix.StartAsync();
        server.On("host.create", """{"hostids":["5"]}""").On("host.get", FakeZabbix.Hosts(("5", "x"))).On("hostgroup.get", """[{"groupid":"2","name":"g"}]""");
        using Shell shell = Shell.Open();
        shell.Connect(server);

        Run run = shell.Invoke($"New-ZbxHost -Name x -Group g -AdditionalProperty ({properties}) -Confirm:$false");

        Assert.Empty(run.Errors);
        JsonElement create = Assert.Single(server.ParamsOf("host.create"));
        Assert.Equal(json, create.GetProperty(field).GetRawText());
    }

    [Fact]
    public async Task Properties_with_a_null_value_takes_the_field_out_of_the_request()
    {
        await using FakeZabbix server = await FakeZabbix.StartAsync();
        server.On("host.create", """{"hostids":["5"]}""").On("host.get", FakeZabbix.Hosts(("5", "x"))).On("hostgroup.get", """[{"groupid":"2","name":"g"}]""");
        using Shell shell = Shell.Open();
        shell.Connect(server);

        Run run = shell.Invoke("New-ZbxHost -Name x -Group g -Description 'named' -AdditionalProperty @{ description = $null } -Confirm:$false");

        // -AdditionalProperty wins over the named parameter, and $null is the absence of the field (about_Community.Zabbix_Properties):
        // the request carries no description, never the API's null, which a string field refuses.
        Assert.Empty(run.Errors);
        JsonElement create = Assert.Single(server.ParamsOf("host.create"));
        Assert.False(create.TryGetProperty("description", out _), create.GetRawText());
    }

    [Theory]
    [InlineData("$piped = [Community.Zabbix.ZabbixHost]@{ HostId = '10084'; HostName = 'web-01' }", "hostid=10084")]
    [InlineData("$piped = '10084'; $kind = @{ Kind = 'Host' }", "hostid=10084")]
    [InlineData("$piped = 10084; $kind = @{ Kind = 'Host' }", "hostid=10084")]
    public async Task Show_ZbxPage_takes_a_record_or_an_id_with_Kind_and_writes_its_address(string arrange, string query)
    {
        await using FakeZabbix server = await FakeZabbix.StartAsync();
        server.On("apiinfo.version", "\"7.0.31\"").On("host.get", """[{"hostid":"10084","host":"web-01","status":"0"}]""");
        using Shell shell = Shell.Open();
        shell.Connect(server);

        Run run = shell.Invoke($"$kind = @{{}}; {arrange}; $piped | Show-ZbxPage @kind -PassThru");

        Assert.Empty(run.Errors);
        Assert.Contains(query, Assert.IsType<Uri>(Assert.Single(run.Output).BaseObject).Query, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("'10084' | Show-ZbxPage -PassThru", "ZabbixPageUnknown")]
    [InlineData("@{ HostId = '10084' } | Show-ZbxPage -PassThru", "ZabbixPageUnknown")]
    [InlineData("'web-01' | Show-ZbxPage -Kind Host -PassThru", "ZabbixPageNotAnId")]
    [InlineData("$null | Show-ZbxPage -PassThru", "ParameterArgumentValidationError")]
    public async Task Show_ZbxPage_refuses_what_has_no_page_with_one_error_naming_why(string script, string error)
    {
        await using FakeZabbix server = await FakeZabbix.StartAsync();
        server.On("apiinfo.version", "\"7.0.31\"");
        using Shell shell = Shell.Open();
        shell.Connect(server);

        Run run = shell.Invoke(script);

        Assert.StartsWith(error + ",", Assert.Single(run.Errors).FullyQualifiedErrorId, StringComparison.Ordinal);
        Assert.Empty(run.Output);
    }

    [Fact]
    public async Task Show_ZbxPage_with_an_empty_pipeline_does_nothing()
    {
        await using FakeZabbix server = await FakeZabbix.StartAsync();
        using Shell shell = Shell.Open();
        shell.Connect(server);

        Run run = shell.Invoke("@() | Show-ZbxPage -PassThru");

        Assert.Empty(run.Errors);
        Assert.Empty(run.Output);
    }
}
