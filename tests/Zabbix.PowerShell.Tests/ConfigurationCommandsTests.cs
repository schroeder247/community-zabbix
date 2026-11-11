// This Source Code Form is subject to the terms of the Mozilla Public
// License, v. 2.0. Copyright (c) 2026 schroeder247. SPDX-License-Identifier: MPL-2.0
using System.Management.Automation;
using System.Text.Json;
using Community.Zabbix.PowerShell.Commands;

namespace Community.Zabbix.PowerShell.Tests;

/// <summary>The configuration commands: export/import/compare/test/copy, and the secret-disclosure gate.</summary>
public sealed class ConfigurationCommandsTests
{
    private const string DocumentWithSecret = """{"zabbix_export":{"version":"7.0","host_groups":[{"name":"Web servers"}],"hosts":[{"host":"web-01","name":"web-01","interfaces":[{"type":1,"ip":"127.0.0.1"}],"password":"hunter2"}]}}""";

    [Fact]
    public async Task Export_ZbxConfiguration_redacts_plaintext_secrets_by_default()
    {
        await using FakeZabbix server = await FakeZabbix.StartAsync();
        server.On("hostgroup.get", """[{"groupid":"7","name":"Web servers"}]""").On("host.get", "[]");
        server.On("configuration.export", JsonSerializer.Serialize(DocumentWithSecret));
        using Shell shell = Shell.Open();
        shell.Connect(server);

        Run run = shell.Invoke("Export-ZbxConfiguration -HostGroup 'Web servers'");

        Assert.Empty(run.Errors);
        string text = Assert.IsType<string>(Assert.Single(run.Output).BaseObject);
        Assert.DoesNotContain("hunter2", text, StringComparison.Ordinal);
        Assert.Contains("REDACTED", text, StringComparison.Ordinal);
        Assert.Contains(run.Warnings, warning => warning.Contains("plaintext credential", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public async Task Export_ZbxConfiguration_IncludeSecrets_keeps_the_plaintext_secret()
    {
        await using FakeZabbix server = await FakeZabbix.StartAsync();
        server.On("hostgroup.get", """[{"groupid":"7","name":"Web servers"}]""").On("host.get", "[]");
        server.On("configuration.export", JsonSerializer.Serialize(DocumentWithSecret));
        using Shell shell = Shell.Open();
        shell.Connect(server);

        Run run = shell.Invoke("Export-ZbxConfiguration -HostGroup 'Web servers' -Unredacted");

        Assert.Empty(run.Errors);
        string text = Assert.IsType<string>(Assert.Single(run.Output).BaseObject);
        Assert.Contains("hunter2", text, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Export_ZbxConfiguration_without_secrets_carries_no_warning()
    {
        await using FakeZabbix server = await FakeZabbix.StartAsync();
        server.On("hostgroup.get", """[{"groupid":"7","name":"Web servers"}]""").On("host.get", "[]");
        server.On("configuration.export", JsonSerializer.Serialize("""{"zabbix_export":{"version":"7.0","hosts":[{"host":"web-01"}]}}"""));
        using Shell shell = Shell.Open();
        shell.Connect(server);

        Run run = shell.Invoke("Export-ZbxConfiguration -HostGroup 'Web servers'");

        Assert.Empty(run.Errors);
        Assert.Empty(run.Warnings);
    }

    [Fact]
    public void ZabbixConfigurationSecrets_counts_and_redacts_password_ipmi_password_and_client_secret()
    {
        const string json = """{"password":"p1","ipmi_password":"p2","client_secret":"p3","name":"kept","password":""}""";

        (string redacted, int count) = ZabbixConfigurationSecrets.Redact(json, "json");

        Assert.Equal(3, count);
        Assert.DoesNotContain("p1", redacted, StringComparison.Ordinal);
        Assert.DoesNotContain("p2", redacted, StringComparison.Ordinal);
        Assert.DoesNotContain("p3", redacted, StringComparison.Ordinal);
        Assert.Contains("kept", redacted, StringComparison.Ordinal);
    }

    [Fact]
    public void ZabbixConfigurationSecrets_treats_an_already_empty_secret_as_no_finding()
    {
        int count = ZabbixConfigurationSecrets.Count("""{"password":""}""", "json");
        Assert.Equal(0, count);
    }

    [Fact]
    public void ZabbixConfigurationSecrets_redacts_xml_and_yaml_too()
    {
        (string xml, int xmlCount) = ZabbixConfigurationSecrets.Redact("<host><password>hunter2</password></host>", "xml");
        Assert.Equal(1, xmlCount);
        Assert.DoesNotContain("hunter2", xml, StringComparison.Ordinal);

        (string yaml, int yamlCount) = ZabbixConfigurationSecrets.Redact("hosts:\n  - password: hunter2\n", "yaml");
        Assert.Equal(1, yamlCount);
        Assert.DoesNotContain("hunter2", yaml, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Import_ZbxConfiguration_requires_confirmation_and_sends_the_typed_rules()
    {
        await using FakeZabbix server = await FakeZabbix.StartAsync();
        server.On("configuration.import", "true");
        using Shell shell = Shell.Open();
        shell.Connect(server);

        Run run = shell.Invoke("""'{"zabbix_export":{}}' | Import-ZbxConfiguration -Rules ([Community.Zabbix.ZabbixConfigurationImportRuleRequest]@{ Hosts = [Community.Zabbix.ZabbixConfigurationImportRuleHostRequest]@{ CreateMissing = $true } }) -Force""");

        Assert.Empty(run.Errors);
        JsonElement request = Assert.Single(server.ParamsOf("configuration.import"));
        Assert.True(request.GetProperty("rules").GetProperty("hosts").GetProperty("createMissing").GetBoolean());
    }

    [Fact]
    public async Task Import_ZbxConfiguration_writes_nothing_and_asks_twice_only_when_a_rule_deletes()
    {
        // The import's bare `true` is written as a boolean, not as a JsonElement (ValueKind : True). An import asks one
        // question: -Confirm:$false answers the ShouldProcess prompt, and only deleteMissing
        // also asks ShouldContinue (which a host that cannot prompt refuses).
        await using FakeZabbix server = await FakeZabbix.StartAsync();
        server.On("configuration.import", "true");
        using Shell shell = Shell.Open();
        shell.Connect(server);

        Run imported = shell.Invoke("""'{"zabbix_export":{}}' | Import-ZbxConfiguration -Rules ([Community.Zabbix.ZabbixConfigurationImportRuleRequest]@{ Hosts = [Community.Zabbix.ZabbixConfigurationImportRuleHostRequest]@{ CreateMissing = $true } }) -Confirm:$false""");
        // $ConfirmPreference None: ShouldProcess passes unprompted, so what refuses is ShouldContinue's second question.
        Run deleting = shell.Invoke("""$ConfirmPreference = 'None'; '{"zabbix_export":{}}' | Import-ZbxConfiguration -Rules ([Community.Zabbix.ZabbixConfigurationImportRuleRequest]@{ Items = [Community.Zabbix.ZabbixConfigurationImportRuleItemRequest]@{ DeleteMissing = $true } })""");

        Assert.Empty(imported.Errors);
        Assert.Empty(imported.Output);
        Assert.StartsWith("ZabbixConfirmationUnavailable,", Assert.Single(deleting.Errors).FullyQualifiedErrorId, StringComparison.Ordinal);
        Assert.Single(server.ParamsOf("configuration.import"));
    }

    [Fact]
    public async Task Import_ZbxConfiguration_without_confirmation_imports_nothing()
    {
        await using FakeZabbix server = await FakeZabbix.StartAsync();
        server.On("configuration.import", "true");
        using Shell shell = Shell.Open();
        shell.Connect(server);

        Run run = shell.Invoke("""'{"zabbix_export":{}}' | Import-ZbxConfiguration -Rules ([Community.Zabbix.ZabbixConfigurationImportRuleRequest]@{})""");

        Assert.NotEmpty(run.Errors);
        Assert.Empty(server.ParamsOf("configuration.import"));
    }

    [Fact]
    public async Task Compare_ZbxConfiguration_redacts_the_diffs_before_side_by_default()
    {
        await using FakeZabbix server = await FakeZabbix.StartAsync();
        server.On("configuration.importcompare", """{"host":{"updated":[{"before":{"password":"hunter2"},"after":{"password":""}}]}}""");
        using Shell shell = Shell.Open();
        shell.Connect(server);

        Run run = shell.Invoke("""
            $diff = '{"zabbix_export":{}}' | Compare-ZbxConfiguration -Rules ([Community.Zabbix.ZabbixConfigurationImportRuleRequest]@{})
            @($diff).Count
            $diff.host.updated[0].before.password
            """);

        Assert.Empty(run.Errors);
        // The diff is a navigable shell object (ConvertFrom-Json's shape), not a raw JsonElement.
        Assert.Equal(2, run.Output.Count);
        Assert.Equal("1", run.Output[0].ToString());
        Assert.NotEqual("hunter2", run.Output[1].ToString());
    }

    [Theory]
    [InlineData("")]
    [InlineData(" -Unredacted")]
    public async Task Compare_ZbxConfiguration_writes_nothing_for_an_unchanged_document(string unredacted)
    {
        // configuration.importcompare answers [] when nothing would change; that is no diff, not one
        // JsonElement "[]" (which would make @($diff).Count 1).
        await using FakeZabbix server = await FakeZabbix.StartAsync();
        server.On("configuration.importcompare", "[]");
        using Shell shell = Shell.Open();
        shell.Connect(server);

        Run run = shell.Invoke("@('{\"zabbix_export\":{}}' | Compare-ZbxConfiguration -Rules ([Community.Zabbix.ZabbixConfigurationImportRuleRequest]@{})" + unredacted + ").Count");

        Assert.Empty(run.Errors);
        Assert.Equal("0", Assert.Single(run.Output).ToString());
    }

    [Fact]
    public async Task Compare_ZbxConfiguration_Unredacted_writes_a_navigable_diff()
    {
        await using FakeZabbix server = await FakeZabbix.StartAsync();
        server.On("configuration.importcompare", """{"templates":{"added":[{"after":{"template":"next-t"}}]}}""");
        using Shell shell = Shell.Open();
        shell.Connect(server);

        Run run = shell.Invoke("""('{"zabbix_export":{}}' | Compare-ZbxConfiguration -Rules ([Community.Zabbix.ZabbixConfigurationImportRuleRequest]@{}) -Unredacted).templates.added[0].after.template""");

        Assert.Empty(run.Errors);
        Assert.Equal("next-t", Assert.Single(run.Output).ToString());
    }

    [Fact]
    public void Test_ZbxConfiguration_reports_a_well_formed_document_as_valid()
    {
        using Shell shell = Shell.Open();

        Run run = shell.Invoke("""'{"zabbix_export":{"hosts":[{"host":"web-01"}]}}' | Test-ZbxConfiguration""");

        Assert.Empty(run.Errors);
        var result = Assert.IsType<ZabbixConfigurationTestResult>(Assert.Single(run.Output).BaseObject);
        Assert.True(result.IsValid);
        Assert.Equal("json", result.Format);
        Assert.Empty(result.Issues);
    }

    [Fact]
    public void Test_ZbxConfiguration_reports_malformed_json_as_invalid()
    {
        using Shell shell = Shell.Open();

        Run run = shell.Invoke("""'{"zabbix_export":' | Test-ZbxConfiguration -Quiet""");

        Assert.Empty(run.Errors);
        Assert.False((bool)Assert.Single(run.Output).BaseObject);
    }

    [Theory]
    [InlineData("not an export")]
    [InlineData("hosts:\n  - host: web-01")]
    [InlineData("{\"hosts\":[]}")]
    [InlineData("<hosts><host>web-01</host></hosts>")]
    [InlineData("[]")]
    public void Test_ZbxConfiguration_refuses_a_document_without_the_zabbix_export_root(string document)
    {
        // A non-empty text is not an export just because it parses as YAML. CXmlValidator.php:43 (7.0) makes zabbix_export XML_REQUIRED
        // for every format the importer reads, so a text with no such root is not an export.
        using Shell shell = Shell.Open();

        Run run = shell.Invoke("$document | Test-ZbxConfiguration", new Dictionary<string, object?> { ["document"] = document });

        Assert.Empty(run.Errors);
        var result = Assert.IsType<ZabbixConfigurationTestResult>(Assert.Single(run.Output).BaseObject);
        Assert.False(result.IsValid);
        Assert.Contains(result.Issues, issue => issue.Contains("zabbix_export", StringComparison.Ordinal));
    }

    [Theory]
    [InlineData("zabbix_export:\n  version: '7.0'", "yaml")]
    [InlineData("---\n# exported\nzabbix_export:\n  version: '7.0'", "yaml")]
    [InlineData("<?xml version=\"1.0\" encoding=\"UTF-8\"?>\n<zabbix_export><version>7.0</version></zabbix_export>", "xml")]
    [InlineData("{\"zabbix_export\":{\"version\":\"7.0\"}}", "json")]
    public void Test_ZbxConfiguration_accepts_the_zabbix_export_root_in_every_format(string document, string format)
    {
        using Shell shell = Shell.Open();

        Run run = shell.Invoke("$document | Test-ZbxConfiguration", new Dictionary<string, object?> { ["document"] = document });

        Assert.Empty(run.Errors);
        var result = Assert.IsType<ZabbixConfigurationTestResult>(Assert.Single(run.Output).BaseObject);
        Assert.True(result.IsValid, string.Join("; ", result.Issues));
        Assert.Equal(format, result.Format);
    }

    [Fact]
    public void Test_ZbxConfiguration_counts_plaintext_secrets()
    {
        using Shell shell = Shell.Open();

        Run run = shell.Invoke("""'{"password":"hunter2"}' | Test-ZbxConfiguration""");

        var result = Assert.IsType<ZabbixConfigurationTestResult>(Assert.Single(run.Output).BaseObject);
        Assert.Equal(1, result.PlaintextSecretCount);
    }

    [Fact]
    public async Task Copy_ZbxHost_exports_the_source_then_imports_it_renamed()
    {
        await using FakeZabbix server = await FakeZabbix.StartAsync();
        server.On("host.get", """[{"hostid":"10084","host":"web-01"}]""");
        server.On("configuration.export", JsonSerializer.Serialize("""{"zabbix_export":{"hosts":[{"host":"web-01","name":"web-01"}]}}"""));
        server.On("configuration.import", "true");
        using Shell shell = Shell.Open();
        shell.Connect(server);

        Run run = shell.Invoke("Copy-ZbxHost web-01 web-02 -Confirm:$false");

        Assert.Empty(run.Errors);
        JsonElement imported = Assert.Single(server.ParamsOf("configuration.import"));
        Assert.Contains("web-02", imported.GetProperty("source").GetString(), StringComparison.Ordinal);
        Assert.DoesNotContain("\"web-01\"", imported.GetProperty("source").GetString(), StringComparison.Ordinal);
    }

    [Fact]
    public async Task Copy_ZbxHost_takes_the_source_host_from_the_pipeline_as_its_InputObject()
    {
        // The source is a reference, as every generated Set- and Remove- takes its object: a piped host binds by value.
        await using FakeZabbix server = await FakeZabbix.StartAsync();
        server.On("host.get", """[{"hostid":"10084","host":"web-01"}]""");
        server.On("configuration.export", JsonSerializer.Serialize("""{"zabbix_export":{"hosts":[{"host":"web-01","name":"web-01"}]}}"""));
        server.On("configuration.import", "true");
        using Shell shell = Shell.Open();
        shell.Connect(server);

        Run run = shell.Invoke("Get-ZbxHost -Id 10084 | Copy-ZbxHost -NewName web-02 -Confirm:$false; (Get-Command Copy-ZbxHost).Parameters['InputObject'].Aliases");

        Assert.Empty(run.Errors);
        Assert.Equal("Id", Assert.Single(run.Output).BaseObject);
        JsonElement exported = Assert.Single(server.ParamsOf("configuration.export"));
        Assert.Equal("10084", exported.GetProperty("options").GetProperty("hosts")[0].GetString());
    }

    [Fact]
    public async Task Copy_ZbxTemplate_exports_the_source_then_imports_it_renamed()
    {
        await using FakeZabbix server = await FakeZabbix.StartAsync();
        server.On("template.get", """[{"templateid":"1","host":"tpl-app"}]""");
        server.On("configuration.export", JsonSerializer.Serialize("""{"zabbix_export":{"templates":[{"template":"tpl-app","name":"tpl-app"}]}}"""));
        server.On("configuration.import", "true");
        using Shell shell = Shell.Open();
        shell.Connect(server);

        Run run = shell.Invoke("Copy-ZbxTemplate tpl-app tpl-app-copy -Confirm:$false");

        Assert.Empty(run.Errors);
        JsonElement imported = Assert.Single(server.ParamsOf("configuration.import"));
        Assert.Contains("tpl-app-copy", imported.GetProperty("source").GetString(), StringComparison.Ordinal);
    }

    [Fact]
    public async Task Copy_ZbxHost_renames_through_the_documents_structure_not_its_text()
    {
        // A visible name that differs from the technical one, an item named like the host, a graph item and a trigger
        // that refer to the host: the copy's own names and its references move, nothing else does.
        await using FakeZabbix server = await FakeZabbix.StartAsync();
        server.On("host.get", """[{"hostid":"10084","host":"web-01","name":"Web server"}]""");
        server.On("configuration.export", JsonSerializer.Serialize("""
            {"zabbix_export":{"version":"7.0","hosts":[{"host":"web-01","name":"Web server","items":[{"name":"web-01","key":"k"}]}],
             "graphs":[{"name":"g","graph_items":[{"item":{"host":"web-01","key":"k"}}]}],
             "triggers":[{"name":"web-01 down","expression":"last(/web-01/k)=0"}]}}
            """));
        server.On("configuration.import", "true");
        using Shell shell = Shell.Open();
        shell.Connect(server);

        Run run = shell.Invoke("Copy-ZbxHost web-01 web-02 -Confirm:$false");

        Assert.Empty(run.Errors);
        using JsonDocument imported = JsonDocument.Parse(Assert.Single(server.ParamsOf("configuration.import")).GetProperty("source").GetString()!);
        JsonElement export = imported.RootElement.GetProperty("zabbix_export");
        JsonElement host = export.GetProperty("hosts")[0];
        Assert.Equal("web-02", host.GetProperty("host").GetString());
        Assert.Equal("web-02", host.GetProperty("name").GetString());
        Assert.Equal("web-01", host.GetProperty("items")[0].GetProperty("name").GetString());
        Assert.Equal("web-02", export.GetProperty("graphs")[0].GetProperty("graph_items")[0].GetProperty("item").GetProperty("host").GetString());
        Assert.Equal("last(/web-02/k)=0", export.GetProperty("triggers")[0].GetProperty("expression").GetString());
        Assert.Equal("web-01 down", export.GetProperty("triggers")[0].GetProperty("name").GetString());
    }

    [Fact]
    public async Task Copy_ZbxHost_NewName_with_a_quote_is_escaped_in_the_document()
    {
        await using FakeZabbix server = await FakeZabbix.StartAsync();
        server.On("host.get", """[{"hostid":"10084","host":"web-01","name":"web-01"}]""");
        server.On("configuration.export", JsonSerializer.Serialize("""{"zabbix_export":{"hosts":[{"host":"web-01","name":"web-01"}]}}"""));
        server.On("configuration.import", "true");
        using Shell shell = Shell.Open();
        shell.Connect(server);

        Run run = shell.Invoke("""Copy-ZbxHost web-01 'we"b\02' -Confirm:$false""");

        Assert.Empty(run.Errors);
        using JsonDocument imported = JsonDocument.Parse(Assert.Single(server.ParamsOf("configuration.import")).GetProperty("source").GetString()!);
        Assert.Equal("""we"b\02""", imported.RootElement.GetProperty("zabbix_export").GetProperty("hosts")[0].GetProperty("host").GetString());
    }

    [Fact]
    public async Task Copy_ZbxTemplate_gives_the_copy_new_uuids()
    {
        // configuration.import matches a template and its parts by UUID before name (on 7.0: a copy that kept
        // them imports nothing and reports no error).
        await using FakeZabbix server = await FakeZabbix.StartAsync();
        server.On("template.get", """[{"templateid":"1","host":"tpl-app","name":"Application"}]""");
        server.On("configuration.export", JsonSerializer.Serialize("""
            {"zabbix_export":{"template_groups":[{"uuid":"g0","name":"Templates"}],
             "templates":[{"uuid":"t0","template":"tpl-app","name":"Application","items":[{"uuid":"i0","key":"k"}]}],
             "graphs":[{"uuid":"gr0","name":"g","graph_items":[{"item":{"host":"tpl-app","key":"k"}}]}]}}
            """));
        server.On("configuration.import", "true");
        using Shell shell = Shell.Open();
        shell.Connect(server);

        Run run = shell.Invoke("Copy-ZbxTemplate tpl-app tpl-copy -Confirm:$false");

        Assert.Empty(run.Errors);
        using JsonDocument imported = JsonDocument.Parse(Assert.Single(server.ParamsOf("configuration.import")).GetProperty("source").GetString()!);
        JsonElement export = imported.RootElement.GetProperty("zabbix_export");
        JsonElement template = export.GetProperty("templates")[0];
        Assert.Equal("tpl-copy", template.GetProperty("template").GetString());
        Assert.Equal("tpl-copy", template.GetProperty("name").GetString());
        Assert.NotEqual("t0", template.GetProperty("uuid").GetString());
        Assert.NotEqual("i0", template.GetProperty("items")[0].GetProperty("uuid").GetString());
        Assert.NotEqual("gr0", export.GetProperty("graphs")[0].GetProperty("uuid").GetString());
        Assert.Equal("g0", export.GetProperty("template_groups")[0].GetProperty("uuid").GetString());
        Assert.Equal("tpl-copy", export.GetProperty("graphs")[0].GetProperty("graph_items")[0].GetProperty("item").GetProperty("host").GetString());
    }

    [Theory]
    [InlineData("Copy-ZbxHost no-such web-02 -Confirm:$false", "host.get")]
    [InlineData("Copy-ZbxTemplate no-such tpl-copy -Confirm:$false", "template.get")]
    [InlineData("Copy-ZbxItem no-such 10085 -Confirm:$false", "host.get")]
    public async Task A_copy_whose_source_matches_nothing_is_one_not_found_error_and_imports_nothing(string script, string get)
    {
        await using FakeZabbix server = await FakeZabbix.StartAsync();
        server.On(get, "[]");
        using Shell shell = Shell.Open();
        shell.Connect(server);

        Run run = shell.Invoke(script);

        ErrorRecord error = Assert.Single(run.Errors);
        Assert.Equal(ErrorCategory.ObjectNotFound, error.CategoryInfo.Category);
        Assert.Empty(server.ParamsOf("configuration.export"));
        Assert.Empty(server.ParamsOf("configuration.import"));
    }

    [Fact]
    public async Task Copy_ZbxHost_confirms_the_resolved_host_and_PassThru_writes_the_copy()
    {
        await using FakeZabbix server = await FakeZabbix.StartAsync();
        server.On("host.get", """[{"hostid":"10084","host":"web-01","name":"web-01"}]""");
        server.On("configuration.export", JsonSerializer.Serialize("""{"zabbix_export":{"hosts":[{"host":"web-01","name":"web-01"}]}}"""));
        server.On("configuration.import", "true");
        using Shell shell = Shell.Open();
        shell.Connect(server);

        Run run = shell.Invoke("Copy-ZbxHost web-01 web-02 -Confirm:$false -Verbose -PassThru");

        Assert.Empty(run.Errors);
        Assert.Contains(run.Verbose, line => line.Contains("\"Copy host\" on target \"host 'web-01' (10084) -> 'web-02'\"", StringComparison.Ordinal));
        Assert.IsType<ZabbixHost>(Assert.Single(run.Output).BaseObject);
    }

    [Fact]
    public async Task Piped_files_import_one_document_each_by_their_PSPath()
    {
        await using FakeZabbix server = await FakeZabbix.StartAsync();
        server.On("configuration.import", "true");
        using Shell shell = Shell.Open();
        shell.Connect(server);
        string folder = Directory.CreateTempSubdirectory("zbx-import-").FullName;
        try
        {
            await File.WriteAllTextAsync(Path.Combine(folder, "a.json"), """{"zabbix_export":{"version":"7.0","a":1}}""", TestContext.Current.CancellationToken);
            await File.WriteAllTextAsync(Path.Combine(folder, "b.json"), """{"zabbix_export":{"version":"7.0","b":2}}""", TestContext.Current.CancellationToken);

            Run run = shell.Invoke("Get-ChildItem -LiteralPath $folder -Filter *.json | Import-ZbxConfiguration -Rules @{ hosts = @{ createMissing = $true } } -Confirm:$false",
                new Dictionary<string, object?> { ["folder"] = folder });

            Assert.Empty(run.Errors);
            Assert.Equal(2, server.ParamsOf("configuration.import").Count());
        }
        finally
        {
            Directory.Delete(folder, recursive: true);
        }
    }

    [Theory]
    [InlineData("Import-ZbxConfiguration -LiteralPath $missing -Rules @{} -Confirm:$false")]
    [InlineData("Compare-ZbxConfiguration -LiteralPath $missing -Rules @{}")]
    [InlineData("Publish-ZbxDashboard -LiteralPath $missing -Confirm:$false")]
    public async Task A_document_file_that_does_not_exist_is_one_non_terminating_not_found_error(string command)
    {
        await using FakeZabbix server = await FakeZabbix.StartAsync();
        using Shell shell = Shell.Open();
        shell.Connect(server);

        Run run = shell.Invoke(command + "; 'after'", new Dictionary<string, object?> { ["missing"] = Path.Combine(Path.GetTempPath(), $"zbx-no-such-{Guid.NewGuid():N}.json") });

        ErrorRecord error = Assert.Single(run.Errors);
        Assert.Equal(ErrorCategory.ObjectNotFound, error.CategoryInfo.Category);
        Assert.StartsWith("ZabbixFileNotFound,", error.FullyQualifiedErrorId, StringComparison.Ordinal);
        Assert.Equal("after", Assert.Single(run.Output).ToString());
        Assert.DoesNotContain(server.Calls, call => call.Method.StartsWith("configuration.", StringComparison.Ordinal) || call.Method.StartsWith("dashboard.", StringComparison.Ordinal));
    }

    [Fact]
    public void Test_ZbxConfiguration_reports_a_file_it_cannot_read_as_its_finding_not_an_error()
    {
        using Shell shell = Shell.Open();

        Run run = shell.Invoke("Test-ZbxConfiguration -LiteralPath $missing", new Dictionary<string, object?> { ["missing"] = Path.Combine(Path.GetTempPath(), $"zbx-no-such-{Guid.NewGuid():N}.json") });

        Assert.Empty(run.Errors);
        var result = Assert.IsType<ZabbixConfigurationTestResult>(Assert.Single(run.Output).BaseObject);
        Assert.False(result.IsValid);
        Assert.StartsWith("Read failed: ", Assert.Single(result.Issues), StringComparison.Ordinal);
    }

    [Fact]
    public async Task Publish_ZbxDashboard_with_a_document_that_is_not_json_is_an_invalid_data_error_not_a_crash()
    {
        await using FakeZabbix server = await FakeZabbix.StartAsync();
        using Shell shell = Shell.Open();
        shell.Connect(server);

        Run run = shell.Invoke("'{not json' | Publish-ZbxDashboard -Confirm:$false; 'after'");

        Assert.Equal(ErrorCategory.InvalidData, Assert.Single(run.Errors).CategoryInfo.Category);
        Assert.Equal("after", Assert.Single(run.Output).ToString());
    }

    [Fact]
    public async Task Export_ZbxConfiguration_NoClobber_leaves_an_existing_file_and_is_a_resource_exists_error()
    {
        await using FakeZabbix server = await FakeZabbix.StartAsync();
        server.On("host.get", FakeZabbix.Hosts(("10084", "web-01")));
        server.On("configuration.export", JsonSerializer.Serialize("""{"zabbix_export":{"version":"7.0","hosts":[{"host":"web-01"}]}}"""));
        using Shell shell = Shell.Open();
        shell.Connect(server);
        string file = Path.Combine(Path.GetTempPath(), $"zbx-export-{Guid.NewGuid():N}.json");
        await File.WriteAllTextAsync(file, "keep", TestContext.Current.CancellationToken);
        try
        {
            Run run = shell.Invoke("Export-ZbxConfiguration -Host web-01 -Path $file -NoClobber", new Dictionary<string, object?> { ["file"] = file });

            Assert.Equal(ErrorCategory.ResourceExists, Assert.Single(run.Errors).CategoryInfo.Category);
            Assert.Equal("keep", await File.ReadAllTextAsync(file, TestContext.Current.CancellationToken));
        }
        finally
        {
            File.Delete(file);
        }
    }

    [Fact]
    public async Task Export_ZbxConfiguration_WhatIf_writes_no_file_and_exports_nothing()
    {
        await using FakeZabbix server = await FakeZabbix.StartAsync();
        server.On("host.get", FakeZabbix.Hosts(("10084", "web-01")));
        using Shell shell = Shell.Open();
        shell.Connect(server);
        string file = Path.Combine(Path.GetTempPath(), $"zbx-export-{Guid.NewGuid():N}.json");

        Run run = shell.Invoke("Export-ZbxConfiguration -Host web-01 -Path $file -WhatIf", new Dictionary<string, object?> { ["file"] = file });

        Assert.Empty(run.Errors);
        Assert.False(File.Exists(file));
        Assert.Empty(server.ParamsOf("configuration.export"));
    }

    [Fact]
    public async Task Export_ZbxConfiguration_Force_is_not_Unredacted_and_never_writes_secrets()
    {
        // -Force means "overwrite the file"; it is not an alias of -Unredacted and must not write plaintext secrets to it.
        await using FakeZabbix server = await FakeZabbix.StartAsync();
        server.On("host.get", FakeZabbix.Hosts(("10084", "web-01")));
        server.On("configuration.export", JsonSerializer.Serialize("""{"zabbix_export":{"version":"7.0","hosts":[{"host":"web-01","ipmi_password":"hunter2"}]}}"""));
        using Shell shell = Shell.Open();
        shell.Connect(server);
        string file = Path.Combine(Path.GetTempPath(), $"zbx-export-{Guid.NewGuid():N}.json");

        Run run = shell.Invoke("Export-ZbxConfiguration -Host web-01 -Path $file -Force", new Dictionary<string, object?> { ["file"] = file });

        Assert.Equal("NamedParameterNotFound,Community.Zabbix.PowerShell.Commands.ExportZbxConfigurationCommand", Assert.Single(run.Errors).FullyQualifiedErrorId);
        Assert.False(File.Exists(file));
    }

    [Fact]
    public async Task Export_ZbxConfiguration_HostGroup_exports_the_groups_hosts_with_the_group()
    {
        // configuration.export with options.host_groups exports the group objects only
        // (CConfigurationExport::gatherHostGroups); the cmdlet resolves the members into options.hosts.
        await using FakeZabbix server = await FakeZabbix.StartAsync();
        server.On("hostgroup.get", """[{"groupid":"7","name":"Web servers"}]""");
        server.On("host.get", FakeZabbix.Hosts(("10084", "web-01"), ("10085", "web-02")));
        server.On("configuration.export", JsonSerializer.Serialize("""{"zabbix_export":{}}"""));
        using Shell shell = Shell.Open();
        shell.Connect(server);

        Run run = shell.Invoke("Export-ZbxConfiguration -HostGroup 'Web servers'");

        Assert.Empty(run.Errors);
        JsonElement members = Assert.Single(server.ParamsOf("host.get"));
        Assert.Equal("7", Assert.Single(members.GetProperty("groupids").EnumerateArray()).GetString());
        JsonElement options = Assert.Single(server.ParamsOf("configuration.export")).GetProperty("options");
        Assert.Equal("7", Assert.Single(options.GetProperty("host_groups").EnumerateArray()).GetString());
        Assert.Equal(["10084", "10085"], options.GetProperty("hosts").EnumerateArray().Select(id => id.GetString()));
    }

    [Fact]
    public async Task Export_ZbxConfiguration_TemplateGroup_exports_the_groups_templates_with_the_group()
    {
        await using FakeZabbix server = await FakeZabbix.StartAsync();
        server.On("templategroup.get", """[{"groupid":"12","name":"Templates/Apps"}]""");
        server.On("template.get", """[{"templateid":"10300","host":"tpl-app"}]""");
        server.On("configuration.export", JsonSerializer.Serialize("""{"zabbix_export":{}}"""));
        using Shell shell = Shell.Open();
        shell.Connect(server);

        Run run = shell.Invoke("Export-ZbxConfiguration -TemplateGroup 'Templates/Apps'");

        Assert.Empty(run.Errors);
        JsonElement members = Assert.Single(server.ParamsOf("template.get"));
        Assert.Equal("12", Assert.Single(members.GetProperty("groupids").EnumerateArray()).GetString());
        JsonElement options = Assert.Single(server.ParamsOf("configuration.export")).GetProperty("options");
        Assert.Equal("12", Assert.Single(options.GetProperty("template_groups").EnumerateArray()).GetString());
        Assert.Equal("10300", Assert.Single(options.GetProperty("templates").EnumerateArray()).GetString());
    }

    [Fact]
    public async Task Export_ZbxConfiguration_HostGroup_and_HostName_name_a_shared_host_once()
    {
        await using FakeZabbix server = await FakeZabbix.StartAsync();
        server.On("hostgroup.get", """[{"groupid":"7","name":"Web servers"}]""");
        server.On("host.get", parameters => parameters.TryGetProperty("groupids", out _)
            ? FakeZabbix.Hosts(("10084", "web-01"))
            : FakeZabbix.Hosts(("10084", "web-01"), ("10090", "db-01")));
        server.On("configuration.export", JsonSerializer.Serialize("""{"zabbix_export":{}}"""));
        using Shell shell = Shell.Open();
        shell.Connect(server);

        Run run = shell.Invoke("Export-ZbxConfiguration -HostGroup 'Web servers' -Host web-01, db-01");

        Assert.Empty(run.Errors);
        JsonElement options = Assert.Single(server.ParamsOf("configuration.export")).GetProperty("options");
        Assert.Equal(["10084", "10090"], options.GetProperty("hosts").EnumerateArray().Select(id => id.GetString()).Order(StringComparer.Ordinal));
    }

    [Fact]
    public async Task Export_ZbxConfiguration_HostGroup_without_members_sends_no_hosts()
    {
        await using FakeZabbix server = await FakeZabbix.StartAsync();
        server.On("hostgroup.get", """[{"groupid":"7","name":"Web servers"}]""").On("host.get", "[]");
        server.On("configuration.export", JsonSerializer.Serialize("""{"zabbix_export":{}}"""));
        using Shell shell = Shell.Open();
        shell.Connect(server);

        Run run = shell.Invoke("Export-ZbxConfiguration -HostGroup 'Web servers'");

        Assert.Empty(run.Errors);
        JsonElement options = Assert.Single(server.ParamsOf("configuration.export")).GetProperty("options");
        Assert.False(options.TryGetProperty("hosts", out JsonElement hosts) && hosts.GetArrayLength() > 0);
        Assert.Equal("7", Assert.Single(options.GetProperty("host_groups").EnumerateArray()).GetString());
    }

    [Theory]
    [InlineData("json")]
    [InlineData("xml")]
    [InlineData("yaml")]
    public async Task Export_ZbxConfiguration_exports_a_large_group_in_batches_merged_into_one_document(string format)
    {
        // One configuration.export of the 357 (7.0.31) / 381 (8.0.0rc1) stock templates
        // exhausts the stock frontend's 128M PHP memory_limit (HTTP 500); 50 per call succeeds on both. The members go
        // in batches and the documents merge into one, the groups (exported by every batch) once.
        await using FakeZabbix server = await FakeZabbix.StartAsync();
        string[] ids = [.. Enumerable.Range(20001, 60).Select(id => id.ToString(System.Globalization.CultureInfo.InvariantCulture))];
        server.On("hostgroup.get", """[{"groupid":"7","name":"Web servers"}]""");
        server.On("host.get", FakeZabbix.Hosts([.. ids.Select(id => (id, "h" + id))]));
        server.On("configuration.export", parameters => JsonSerializer.Serialize(BatchDocument(parameters)));
        using Shell shell = Shell.Open();
        shell.Connect(server);

        Run run = shell.Invoke($"Export-ZbxConfiguration -HostGroup 'Web servers' -Format {format}");

        Assert.Empty(run.Errors);
        JsonElement[] calls = [.. server.ParamsOf("configuration.export")];
        Assert.True(calls.Length > 1);
        Assert.All(calls, call => Assert.True(call.GetProperty("options").GetProperty("hosts").GetArrayLength() <= ExportZbxConfigurationCommand.DefaultBatchSize));
        Assert.Equal(ids, calls.SelectMany(call => call.GetProperty("options").GetProperty("hosts").EnumerateArray()).Select(id => id.GetString()));
        Assert.True(calls[0].GetProperty("options").TryGetProperty("host_groups", out _));
        Assert.All(calls.Skip(1), call => Assert.False(call.GetProperty("options").TryGetProperty("host_groups", out JsonElement groups) && groups.ValueKind == JsonValueKind.Array));
        string text = Assert.IsType<string>(Assert.Single(run.Output).BaseObject);
        Assert.Equal(1, Occurrences(text, "Web servers"));
        Assert.Equal(1, Occurrences(text, "7.0"));
        Assert.All(ids, id => Assert.Equal(1, Occurrences(text, "h" + id)));
        if (format == "json")
        {
            JsonElement merged = JsonDocument.Parse(text).RootElement.GetProperty("zabbix_export");
            Assert.Equal(60, merged.GetProperty("hosts").GetArrayLength());
        }
        else if (format == "xml")
        {
            System.Xml.Linq.XElement merged = System.Xml.Linq.XDocument.Parse(text).Root!;
            Assert.Equal(60, merged.Element("hosts")!.Elements("host").Count());
        }
        else
        {
            Assert.StartsWith("zabbix_export:\n  version: '7.0'\n  host_groups:\n    - uuid: g7\n      name: 'Web servers'\n  hosts:\n    - host: h20001\n", text, StringComparison.Ordinal);
        }
    }

    [Fact]
    public async Task Export_ZbxConfiguration_BatchSize_sets_the_members_per_export_call()
    {
        await using FakeZabbix server = await FakeZabbix.StartAsync();
        server.On("hostgroup.get", """[{"groupid":"7","name":"Web servers"}]""");
        server.On("host.get", FakeZabbix.Hosts([.. Enumerable.Range(1, 25).Select(id => (id.ToString(System.Globalization.CultureInfo.InvariantCulture), "h" + id))]));
        server.On("configuration.export", parameters => JsonSerializer.Serialize(BatchDocument(parameters)));
        using Shell shell = Shell.Open();
        shell.Connect(server);

        Run run = shell.Invoke("Export-ZbxConfiguration -HostGroup 'Web servers' -BatchSize 10");

        Assert.Empty(run.Errors);
        Assert.Equal([10, 10, 5], server.ParamsOf("configuration.export").Select(call => call.GetProperty("options").GetProperty("hosts").GetArrayLength()));
        Assert.Equal(25, JsonDocument.Parse(Assert.IsType<string>(Assert.Single(run.Output).BaseObject)).RootElement.GetProperty("zabbix_export").GetProperty("hosts").GetArrayLength());
    }

    private static int Occurrences(string text, string value)
        => System.Text.RegularExpressions.Regex.Count(text, System.Text.RegularExpressions.Regex.Escape(value) + "(?![0-9])");

    /// <summary>The document configuration.export writes for one batch, in the format asked, as the 7.0 writers lay it out.</summary>
    private static string BatchDocument(JsonElement parameters)
    {
        string[] hosts = parameters.GetProperty("options").TryGetProperty("hosts", out JsonElement given) ? [.. given.EnumerateArray().Select(id => "h" + id.GetString())] : [];
        return parameters.GetProperty("format").GetString() switch
        {
            "xml" => "<?xml version=\"1.0\" encoding=\"UTF-8\"?>\n<zabbix_export><version>7.0</version><host_groups><host_group><uuid>g7</uuid><name>Web servers</name></host_group></host_groups><hosts>"
                + string.Concat(hosts.Select(host => $"<host><host>{host}</host></host>")) + "</hosts></zabbix_export>\n",
            "yaml" => "zabbix_export:\n  version: '7.0'\n  host_groups:\n    - uuid: g7\n      name: 'Web servers'\n  hosts:\n"
                + string.Concat(hosts.Select(host => $"    - host: {host}\n      description: |\n        line one\n        - not an element\n")),
            _ => """{"zabbix_export":{"version":"7.0","host_groups":[{"uuid":"g7","name":"Web servers"}],"hosts":["""
                + string.Join(',', hosts.Select(host => $$"""{"host":"{{host}}"}""")) + "]}}",
        };
    }

    [Theory]
    [InlineData("Json", "json")]
    [InlineData("YAML", "yaml")]
    [InlineData("xml", "xml")]
    public async Task Export_ZbxConfiguration_Format_sends_the_lowercase_wire_value_whatever_the_case_typed(string typed, string wire)
    {
        // [ValidateSet] is case-insensitive, so the typed value must not go to the wire verbatim:
        // configuration.export refuses "Json" ('value must be one of "yaml", "xml", "json", "raw"').
        await using FakeZabbix server = await FakeZabbix.StartAsync();
        server.On("hostgroup.get", """[{"groupid":"7","name":"Web servers"}]""").On("host.get", "[]");
        server.On("configuration.export", JsonSerializer.Serialize("{}"));
        using Shell shell = Shell.Open();
        shell.Connect(server);

        Run run = shell.Invoke($"Export-ZbxConfiguration -HostGroup 'Web servers' -Format {typed}");

        Assert.Empty(run.Errors);
        Assert.Equal(wire, Assert.Single(server.ParamsOf("configuration.export")).GetProperty("format").GetString());
    }

    [Theory]
    [InlineData("Import-ZbxConfiguration -Force", "configuration.import", "true")]
    [InlineData("Compare-ZbxConfiguration", "configuration.importcompare", "[]")]
    public async Task Import_and_Compare_Format_send_the_lowercase_wire_value(string command, string method, string reply)
    {
        await using FakeZabbix server = await FakeZabbix.StartAsync();
        server.On(method, reply);
        using Shell shell = Shell.Open();
        shell.Connect(server);

        Run run = shell.Invoke($$$"""'{"zabbix_export":{}}' | {{{command}}} -Format Json -Rules ([Community.Zabbix.ZabbixConfigurationImportRuleRequest]@{})""");

        Assert.Empty(run.Errors);
        Assert.Equal("json", Assert.Single(server.ParamsOf(method)).GetProperty("format").GetString());
    }

    [Fact]
    public async Task Copy_ZbxItem_Key_imports_only_the_named_items_onto_the_target_host()
    {
        // -Key selects what is imported, not only whether to copy: importing the whole host export would copy every item.
        const string export = """
            {"zabbix_export":{"version":"7.0","hosts":[{"host":"web-01","name":"web-01","items":[
              {"name":"ping","key":"agent.ping"},{"name":"cpu","key":"system.cpu.load"},{"name":"mem","key":"vm.memory.size"}]}]}}
            """;
        await using FakeZabbix server = await FakeZabbix.StartAsync();
        server.On("host.get", parameters => parameters.GetRawText().Contains("10085", StringComparison.Ordinal)
                ? """[{"hostid":"10085","host":"web-02","name":"Web two"}]""" : FakeZabbix.Hosts(("10084", "web-01")))
              .On("item.get", """[{"itemid":"1","key_":"agent.ping"},{"itemid":"2","key_":"vm.memory.size"}]""")
              .On("configuration.export", System.Text.Json.JsonSerializer.Serialize(export))
              .On("configuration.import", "true");
        using Shell shell = Shell.Open();
        shell.Connect(server);

        Run run = shell.Invoke("Copy-ZbxItem web-01 10085 -Key agent.ping, vm.memory.size -Confirm:$false");

        Assert.Empty(run.Errors);
        JsonElement import = Assert.Single(server.ParamsOf("configuration.import"));
        JsonElement host = JsonDocument.Parse(import.GetProperty("source").GetString()!).RootElement.GetProperty("zabbix_export").GetProperty("hosts")[0];
        Assert.Equal("web-02", host.GetProperty("host").GetString());
        Assert.Equal("Web two", host.GetProperty("name").GetString());
        Assert.Equal(["agent.ping", "vm.memory.size"], host.GetProperty("items").EnumerateArray().Select(item => item.GetProperty("key").GetString()));
    }

    [Fact]
    public async Task Copy_ZbxItem_Key_the_source_does_not_have_is_an_error()
    {
        await using FakeZabbix server = await FakeZabbix.StartAsync();
        server.On("host.get", FakeZabbix.Hosts(("10084", "web-01"))).On("item.get", "[]");
        using Shell shell = Shell.Open();
        shell.Connect(server);

        Run run = shell.Invoke("Copy-ZbxItem web-01 10085 -Key no.such.key -Confirm:$false");

        Assert.StartsWith("ZabbixItemNotFound,", Assert.Single(run.Errors).FullyQualifiedErrorId, StringComparison.Ordinal);
        Assert.Empty(server.ParamsOf("configuration.import"));
    }

    [Fact]
    public async Task Path_takes_wildcards_and_LiteralPath_takes_the_name_as_written()
    {
        // -Path expands wildcards, one document per file; -LiteralPath does not; the first positional is a path,
        // never the document text (Import-ZbxConfiguration ./x.yaml must not import the string "./x.yaml").
        await using FakeZabbix server = await FakeZabbix.StartAsync();
        server.On("configuration.import", "true");
        string folder = Directory.CreateTempSubdirectory("zbx-path-").FullName;
        try
        {
            await File.WriteAllTextAsync(System.IO.Path.Combine(folder, "a.json"), """{"zabbix_export":{"version":"7.0"}}""", TestContext.Current.CancellationToken);
            await File.WriteAllTextAsync(System.IO.Path.Combine(folder, "b.json"), """{"zabbix_export":{"version":"7.0"}}""", TestContext.Current.CancellationToken);
            await File.WriteAllTextAsync(System.IO.Path.Combine(folder, "[c].json"), """{"zabbix_export":{"version":"7.0"}}""", TestContext.Current.CancellationToken);
            using Shell shell = Shell.Open();
            shell.Connect(server);

            Run wildcard = shell.Invoke($"Import-ZbxConfiguration '{folder}/*.json' -Rules @{{}} -Confirm:$false");
            Run literal = shell.Invoke($"Import-ZbxConfiguration -LiteralPath '{folder}/[c].json' -Rules @{{}} -Confirm:$false");
            Run tested = shell.Invoke($"Test-ZbxConfiguration '{folder}/?.json' -Quiet");

            Assert.Empty(wildcard.Errors);
            Assert.Empty(literal.Errors);
            Assert.Equal(4, server.ParamsOf("configuration.import").Count());
            Assert.Equal([true, true], tested.Output.Select(o => (bool)o.BaseObject));
        }
        finally
        {
            Directory.Delete(folder, recursive: true);
        }
    }
}
