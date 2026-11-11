// This Source Code Form is subject to the terms of the Mozilla Public
// License, v. 2.0. Copyright (c) 2026 schroeder247. SPDX-License-Identifier: MPL-2.0
extern alias ZabbixGenerator;

using ZabbixGenerator::Community.Zabbix.Generator;

namespace Community.Zabbix.PowerShell.Tests;

/// <summary>
/// The generated <c>Community.Zabbix.types.ps1xml</c> (same status as <c>Community.Zabbix.psd1</c>/MAML help): every
/// <c>role: request/nested-request/get</c> type gets a <c>&lt;TypeConverter&gt;</c> entry (<see cref="ZabbixRecordConverter"/>),
/// every <c>role: read</c> type gets a <c>&lt;TypeAdapter&gt;</c> entry (<see cref="ZabbixAdditionalFieldsAdapter"/>)
/// — coverage checked by regenerating the file from the SAME description (<see cref="ZabbixTypesEmitter.Generate"/>,
/// the pure function the build-time writer calls) and diffing against the copy this project's own build
/// output carries beside <c>Community.Zabbix.psd1</c>, plus a live check that a fresh <c>Import-Module</c> actually loaded it
/// (<c>Get-TypeData</c>).
/// </summary>
public sealed class ZabbixTypesPs1XmlTests
{
    private static readonly string SchemaPath = Path.Combine(AppContext.BaseDirectory, "schema", "zabbix-api.json");
    private static readonly string GeneratedTypesPath = Path.Combine(AppContext.BaseDirectory, "Community.Zabbix.types.ps1xml");

    [Fact]
    public void The_built_types_ps1xml_matches_a_fresh_regeneration_from_the_current_schema()
    {
        ApiDescription description = ApiDescription.Read(File.ReadAllText(SchemaPath));
        string expected = ZabbixTypesEmitter.Generate(description);
        string actual = File.ReadAllText(GeneratedTypesPath).Replace("\r\n", "\n", StringComparison.Ordinal);

        Assert.Equal(expected, actual);
    }

    [Fact]
    public void Every_request_parameters_or_nested_record_type_has_a_TypeConverter_entry()
    {
        ApiDescription description = ApiDescription.Read(File.ReadAllText(SchemaPath));
        string[] expectedNames = [.. description.Types
            .Where(t => t.Role is "request" or "nested-request" or "get")
            .Select(t => "Community.Zabbix." + (t.Name.StartsWith("Zabbix", StringComparison.Ordinal) ? t.Name : "Zabbix" + t.Name))
            .Distinct()
            .Order(StringComparer.Ordinal)];
        Assert.NotEmpty(expectedNames);

        string text = File.ReadAllText(GeneratedTypesPath);
        foreach (string name in expectedNames)
            Assert.Contains($"<Name>{name}</Name>\n    <TypeConverter>", text, StringComparison.Ordinal);
    }

    [Fact]
    public void Every_read_type_has_a_TypeAdapter_entry()
    {
        ApiDescription description = ApiDescription.Read(File.ReadAllText(SchemaPath));
        string[] expectedNames = [.. description.Types
            .Where(t => t.Role == "read" && t.ExtensionData)
            .Select(t => "Community.Zabbix." + (t.Name.StartsWith("Zabbix", StringComparison.Ordinal) ? t.Name : "Zabbix" + t.Name))
            .Distinct()
            .Order(StringComparer.Ordinal)];
        Assert.NotEmpty(expectedNames);

        string text = File.ReadAllText(GeneratedTypesPath);
        foreach (string name in expectedNames)
            Assert.Contains($"<Name>{name}</Name>\n    <TypeAdapter>", text, StringComparison.Ordinal);
    }

    [Fact]
    public void Import_Module_loads_Zabbix_types_ps1xml_Get_TypeData_shows_the_converter_and_the_adapter()
    {
        using Shell shell = Shell.Open();

        Run converter = shell.Invoke("(Get-TypeData -TypeName 'Community.Zabbix.ZabbixHostCreateRequest').TypeConverter.FullName");
        Run adapter = shell.Invoke("(Get-TypeData -TypeName 'Community.Zabbix.ZabbixHost').TypeAdapter.FullName");

        Assert.Empty(converter.Errors);
        Assert.Equal("Community.Zabbix.PowerShell.ZabbixRecordConverter", Assert.Single(converter.Output).BaseObject);
        Assert.Empty(adapter.Errors);
        Assert.Equal("Community.Zabbix.PowerShell.ZabbixAdditionalFieldsAdapter", Assert.Single(adapter.Output).BaseObject);
    }

    [Fact]
    public async Task A_read_records_tags_answer_to_Name_and_a_host_to_DisplayName()
    {
        // Scripts read $h.Tags[0].Name and $h.DisplayName; the records say Tag and VisibleName.
        // ETS aliases, not model changes.
        await using FakeZabbix server = await FakeZabbix.StartAsync();
        server.On("host.get", """[{"hostid":"10084","host":"web-01","name":"Web One","tags":[{"tag":"env","value":"prod"}]}]""");
        using Shell shell = Shell.Open();
        shell.Connect(server);

        Run run = shell.Invoke("$h = Get-ZbxHost -Id 10084; $h.Tags[0].Name; $h.DisplayName");

        Assert.Empty(run.Errors);
        Assert.Equal("env", run.Output[0].BaseObject);
        Assert.Equal("Web One", run.Output[1].BaseObject);
    }

    [Fact]
    public async Task A_user_macro_answers_to_Name_with_its_bare_name()
    {
        await using FakeZabbix server = await FakeZabbix.StartAsync();
        server.On("usermacro.get", """[{"hostmacroid":"6","hostid":"10084","macro":"{$NP8MAC}","value":"v"}]""");
        using Shell shell = Shell.Open();
        shell.Connect(server);

        Run run = shell.Invoke("(Get-ZbxUserMacro -Id 6).Name");

        Assert.Empty(run.Errors);
        Assert.Equal("NP8MAC", Assert.Single(run.Output).BaseObject);
    }

    [Fact]
    public async Task A_template_record_has_no_HostId_so_its_TemplateId_is_what_binds()
    {
        // template.get answers templateid and takes hostid out of every template (CTemplate.php 8.0:252), so the record
        // carries no HostId that could bind a null ahead of New-ZbxItem -HostId's TemplateId alias.
        await using FakeZabbix server = await FakeZabbix.StartAsync();
        server.On("template.get", """[{"templateid":"10001","host":"next-t","name":"next-t"}]""");
        using Shell shell = Shell.Open();
        shell.Connect(server);

        Run run = shell.Invoke("$t = Get-ZbxTemplate -Id 10001; $t.PSObject.Properties.Name -contains 'HostId'; $t.TemplateId");

        Assert.Empty(run.Errors);
        Assert.Equal(false, run.Output[0].BaseObject);
        Assert.Equal("10001", run.Output[1].ToString());
    }
}
