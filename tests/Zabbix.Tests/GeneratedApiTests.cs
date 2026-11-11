// This Source Code Form is subject to the terms of the Mozilla Public
// License, v. 2.0. Copyright (c) 2026 schroeder247. SPDX-License-Identifier: MPL-2.0
extern alias ZabbixGenerator;

using System.Globalization;
using System.Reflection;
using System.Text.Json;
using System.Text.Json.Serialization.Metadata;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.Diagnostics;
using Microsoft.CodeAnalysis.Text;
using Microsoft.Extensions.DependencyInjection;
using ZabbixGenerator::Community.Zabbix.Generator;

namespace Community.Zabbix.Tests;

/// <summary>
/// The Zabbix source is the model's authority. The generator is driven in-process for its two parts and its
/// diagnostics; the generated clients speak the wire, gate on the server's version and compose what 8.0 removed; and
/// the checked-in description is held to facts a model reflected from the public documentation gets wrong (golden checks).
/// </summary>
public sealed class GeneratedApiTests
{
    private static readonly string SchemaPath = Path.Combine(AppContext.BaseDirectory, "schema", "zabbix-api.json");

    private static readonly Lazy<JsonDocument> Description = new(() => JsonDocument.Parse(File.ReadAllText(SchemaPath)));

    private static (ZabbixClient Server, ScriptedApi Api) Build(ScriptedApi api)
    {
        var services = new ServiceCollection();
        services.AddZabbix().Configure(options => { options.Endpoint = new Uri("https://zbx.example/api_jsonrpc.php"); options.ApiToken = "tok"; });
        services.AddHttpClient(ZabbixHttp.GetClientName()).ConfigurePrimaryHttpMessageHandler(() => api);
        ServiceProvider provider = services.BuildValidated();
        return (provider.GetRequiredService<ZabbixClient>(), api);
    }

    // ----- the generator ---------------------------------------------------------------------------------------

    private static GeneratorDriverRunResult Run(string json, string part)
    {
        var compilation = CSharpCompilation.Create("probe", references: [MetadataReference.CreateFromFile(typeof(object).Assembly.Location)]);
        GeneratorDriver driver = CSharpGeneratorDriver.Create(
            generators: [new ZabbixApiGenerator().AsSourceGenerator()],
            additionalTexts: [new InMemoryText("/schema/zabbix-api.json", json)],
            optionsProvider: new PartOptions(part));
        return driver.RunGenerators(compilation).GetRunResult();
    }

    private const string Tiny = """
        {
         "versions": ["7.0", "8.0"],
         "enums": [],
         "types": [
          {"name": "Thing", "role": "read", "fields": [
            {"name": "ThingId", "wire": "thingid", "kind": "id"},
            {"name": "Odd", "wire": "odd", "kind": "quaternion"}
          ]}
         ],
         "services": []
        }
        """;

    /// <summary>
    /// A list read has no paged form, and each one says why where a caller looks for it: the API's <c>.get</c> has a
    /// limit but no offset or cursor, so the reply is read whole within the response bound.
    /// </summary>
    [Fact]
    public void Every_list_read_says_it_is_one_reply_and_why()
    {
        System.Xml.Linq.XDocument xml = System.Xml.Linq.XDocument.Load(Path.ChangeExtension(typeof(ZabbixHostClient).Assembly.Location, ".xml"));
        List<System.Xml.Linq.XElement> reads = [.. xml.Descendants("member")
            .Where(member => ((string?)member.Attribute("name"))?.Contains("Client.GetAsync(", StringComparison.Ordinal) == true)
            .Where(member => member.Element("summary")?.Value.Contains("as one reply", StringComparison.Ordinal) == true)];

        Assert.True(reads.Count > 50, $"{reads.Count} list reads");
        Assert.All(reads, read => Assert.Contains("there is no paged form", read.Element("remarks")?.Value ?? "", StringComparison.Ordinal));
        Assert.Contains(reads, read => read.Element("remarks")!.Descendants("see").Any(see => (string?)see.Attribute("cref") == "P:Community.Zabbix.ZabbixOptions.MaxResponseContentBytes"));
    }

    [Fact]
    public void The_models_part_emits_records_enums_and_the_capability_table_and_the_clients_part_emits_clients_and_the_aggregate()
    {
        string json = File.ReadAllText(SchemaPath);

        GeneratorDriverRunResult models = Run(json, "Models");
        GeneratorDriverRunResult clients = Run(json, "Clients");

        Assert.Empty(models.Diagnostics);
        Assert.Empty(clients.Diagnostics);
        string[] modelFiles = [.. models.GeneratedTrees.Select(tree => Path.GetFileName(tree.FilePath))];
        string[] clientFiles = [.. clients.GeneratedTrees.Select(tree => Path.GetFileName(tree.FilePath))];
        Assert.Contains("ZabbixHost.g.cs", modelFiles);
        Assert.Contains("ZabbixItemValueType.g.cs", modelFiles);
        Assert.Contains("ZabbixApiMethods.g.cs", modelFiles);
        Assert.DoesNotContain("ZabbixHostClient.g.cs", modelFiles);
        Assert.Contains("ZabbixHostClient.g.cs", clientFiles);
        Assert.Contains("ZabbixApi.g.cs", clientFiles);
        // No <auto-generated/> marker and no blanket #pragma warning disable (Generated_code_is_held_to_the_library_rules):
        // either one would turn every analyzer off for the file, exempting our own output from the rules it emits code under.
        Assert.All(models.GeneratedTrees.Concat(clients.GeneratedTrees), tree => Assert.StartsWith("// Emitted by Zabbix.Generator", tree.GetText(TestContext.Current.CancellationToken).ToString(), StringComparison.Ordinal));
    }

    [Fact]
    public void An_unreadable_description_is_one_diagnostic_not_a_crash()
    {
        GeneratorDriverRunResult result = Run("{ \"versions\": [\"7.4\"] }", "Models");

        Diagnostic diagnostic = Assert.Single(result.Diagnostics);
        Assert.Equal("ZBX0001", diagnostic.Id);
        Assert.Equal(DiagnosticSeverity.Error, diagnostic.Severity);
        Assert.Empty(result.GeneratedTrees);
    }

    [Fact]
    public void A_kind_the_generator_does_not_know_is_an_error_at_the_description_s_line_not_a_silent_json_element()
    {
        GeneratorDriverRunResult result = Run(Tiny, "Models");

        Diagnostic diagnostic = Assert.Single(result.Diagnostics);
        Assert.Equal("ZBX0002", diagnostic.Id);
        Assert.Equal(DiagnosticSeverity.Error, diagnostic.Severity);
        Assert.Equal(6, diagnostic.Location.GetLineSpan().StartLinePosition.Line);                 // the "odd" field's line (zero-based)
        string thing = result.GeneratedTrees.Single(t => t.FilePath.EndsWith("ZabbixThing.g.cs", StringComparison.Ordinal)).GetText(TestContext.Current.CancellationToken).ToString();
        Assert.Contains("ThingId", thing, StringComparison.Ordinal);
        Assert.DoesNotContain("Odd", thing, StringComparison.Ordinal);
    }

    [Fact]
    public void A_kind_that_names_an_undeclared_record_is_an_error()
    {
        GeneratorDriverRunResult result = Run(Tiny.Replace("quaternion", "object:Nowhere", StringComparison.Ordinal), "Models");

        Assert.Equal("ZBX0003", Assert.Single(result.Diagnostics).Id);
    }

    [Fact]
    public void Two_fields_that_would_share_a_csharp_name_are_an_error_not_a_skipped_member()
    {
        GeneratorDriverRunResult result = Run(Tiny.Replace("\"name\": \"Odd\", \"wire\": \"odd\", \"kind\": \"quaternion\"", "\"name\": \"ThingId\", \"wire\": \"thing_id\", \"kind\": \"id\"", StringComparison.Ordinal), "Models");

        Diagnostic diagnostic = Assert.Single(result.Diagnostics);
        Assert.Equal("ZBX0004", diagnostic.Id);
        // the message is a sentence that names the overlay entry a human edits; the location stays the description's line
        string message = diagnostic.GetMessage(CultureInfo.InvariantCulture);
        Assert.StartsWith("Thing.thing_id is left out because its C# name is taken: ", message, StringComparison.Ordinal);
        Assert.EndsWith("name it at schema/overlay.json#/types/Thing/fields/thing_id/name", message, StringComparison.Ordinal);
        Assert.EndsWith("zabbix-api.json", diagnostic.Location.GetLineSpan().Path, StringComparison.Ordinal);
    }

    [Fact]
    public void Every_generated_record_is_registered_in_the_context_the_build_writes()
    {
        HashSet<string> described = [.. ApiDescription.Read(File.ReadAllText(SchemaPath)).Types.Select(t => "Zabbix" + t.Name)];
        List<Type> records = [.. typeof(ZabbixHost).Assembly.GetExportedTypes().Where(type => described.Contains(type.Name))];

        List<string> missing = [.. records.Where(type => ZabbixApiJson.Default.GetTypeInfo(type) is null).Select(type => type.Name)];

        Assert.Equal(described.Count, records.Count);
        Assert.Empty(missing);
    }

    [Fact]
    public void Every_client_hangs_off_the_aggregate_and_shares_the_transport()
    {
        using var scripted = new ScriptedApi();
        (ZabbixClient client, _) = Build(scripted);

        IEnumerable<Type> clients = typeof(ZabbixApi).Assembly.GetExportedTypes().Where(type => type.Name.EndsWith("Client", StringComparison.Ordinal) && type.Name.StartsWith("Zabbix", StringComparison.Ordinal) && type.GetConstructors(BindingFlags.Instance | BindingFlags.NonPublic).Any(c => c.GetParameters().FirstOrDefault()?.ParameterType == typeof(IZabbixTransport)));
        List<Type> hung = [.. typeof(ZabbixApi).GetProperties().Select(property => property.PropertyType)];

        Assert.Equal(Description.Value.RootElement.GetProperty("services").GetArrayLength(), clients.Count());
        Assert.All(clients, client => Assert.Contains(client, hung));
        Assert.Same(client.Transport, client.Transport);
    }

    // ----- the clients on the wire -----------------------------------------------------------------------------

    [Fact]
    public async Task A_get_is_one_reply_as_a_list_typed_in_and_out_with_the_api_s_string_numbers_and_enums()
    {
        using ScriptedApi api = new ScriptedApi().On("host.get", """[{"hostid":"10","host":"web-01","name":"Web 01","status":"1","tls_connect":"2","flags":"4"}]""");
        (ZabbixClient client, _) = Build(api);

        IReadOnlyList<ZabbixHost> hosts = await client.Hosts.GetAsync(new ZabbixHostGetParameters
        {
            Output = ZabbixOutput.Extend,
            Filter = new() { ["host"] = new[] { "web-01", "web-02" } },
            SelectInterfaces = ZabbixOutput.Of("ip", "port"),
            MonitoredHosts = true,
            Limit = 5,
        }, TestContext.Current.CancellationToken);

        ZabbixHost host = Assert.Single(hosts);
        Assert.Equal(10UL, host.HostId);
        Assert.Equal(ZabbixHostStatus.Disabled, host.Status);                                // HOST_STATUS_NOT_MONITORED = 1
        Assert.Equal(ZabbixTlsConnectMode.Psk, host.TlsConnect);
        Assert.Equal(ZabbixDiscoveryKinds.Discovered, host.Flags);
        JsonElement sent = api.ParamsOf("host.get").Single();
        Assert.Equal("extend", sent.GetProperty("output").GetString());
        Assert.Equal(["ip", "port"], sent.GetProperty("selectInterfaces").EnumerateArray().Select(e => e.GetString()));
        Assert.True(sent.GetProperty("monitored_hosts").GetBoolean());
        Assert.Equal(5, sent.GetProperty("limit").GetInt32());
        Assert.False(sent.TryGetProperty("hostids", out _));                                     // null is not sent
        Assert.Equal(typeof(Task<IReadOnlyList<ZabbixHost>>), typeof(ZabbixHostClient).GetMethod("GetAsync", [typeof(ZabbixHostGetParameters), typeof(CancellationToken)])!.ReturnType);
    }

    [Fact]
    public async Task Count_asks_for_countOutput_and_reads_the_api_s_string_count_and_refuses_what_is_not_a_count()
    {
        using ScriptedApi api = new ScriptedApi().On("item.get", "\"1234\"").On("host.get", "null");
        (ZabbixClient client, _) = Build(api);

        long count = await client.Items.CountAsync(new ZabbixItemGetParameters { Host = "web-01" }, TestContext.Current.CancellationToken);

        Assert.Equal(1234, count);
        Assert.True(api.ParamsOf("item.get").Single().GetProperty("countOutput").GetBoolean());
        await Assert.ThrowsAsync<InvalidDataException>(() => client.Hosts.CountAsync(cancellationToken: TestContext.Current.CancellationToken));          // never read as 0
    }

    [Fact]
    public async Task Create_update_and_delete_return_the_ids_the_server_names()
    {
        using ScriptedApi api = new ScriptedApi()
            .On("host.create", """{"hostids":["107"]}""")
            .On("host.update", """{"hostids":["107"]}""")
            .On("host.delete", """{"hostids":["107","108"]}""");
        (ZabbixClient client, _) = Build(api);

        ulong created = await client.Hosts.CreateAsync(new ZabbixHostCreateRequest
        {
            TechnicalName = "web-03",
            Groups = [new ZabbixHostGroupRequest { GroupId = 2 }],
            Status = ZabbixHostStatus.Enabled,
        }, TestContext.Current.CancellationToken);
        ulong updated = await client.Hosts.UpdateAsync(new ZabbixHostUpdateRequest { HostId = 107, VisibleName = "Web 03" }, TestContext.Current.CancellationToken);
        IReadOnlyList<ulong> deleted = await client.Hosts.DeleteAsync([107, 108], TestContext.Current.CancellationToken);

        Assert.Equal(107UL, created);
        Assert.Equal(107UL, updated);
        Assert.Equal<ulong>([107, 108], deleted);
        JsonElement create = api.ParamsOf("host.create").Single();
        Assert.Equal(0, create.GetProperty("status").GetInt32());                                // enums go as numbers
        Assert.Equal("2", create.GetProperty("groups")[0].GetProperty("groupid").GetString());
    }

    [Fact]
    public async Task A_write_of_one_object_answers_its_one_id_and_refuses_a_reply_of_another_count()
    {
        using ScriptedApi api = new ScriptedApi()
            .On("hostgroup.create", """{"groupids":["5","6"]}""")
            .On("hostgroup.delete", """{"groupids":["5"]}""");
        (ZabbixClient client, _) = Build(api);

        await Assert.ThrowsAsync<InvalidDataException>(() => client.HostGroups.CreateAsync(new ZabbixHostGroupCreateRequest { Name = "A" }, TestContext.Current.CancellationToken));
        await client.HostGroups.DeleteAsync(5UL, TestContext.Current.CancellationToken);

        Assert.Equal("""["5"]""", api.ParamsOf("hostgroup.delete").Single().GetRawText());
    }

    [Fact]
    public async Task FindAsync_answers_the_object_with_the_id_or_null_through_its_own_ids_option()
    {
        using ScriptedApi api = new ScriptedApi()
            .On("template.get", parameters => parameters.GetProperty("templateids")[0].GetString() == "10001" ? """[{"templateid":"10001","host":"Linux"}]""" : "[]");
        (ZabbixClient client, _) = Build(api);

        ZabbixTemplate? found = await client.Templates.FindAsync(10001UL, TestContext.Current.CancellationToken);
        ZabbixTemplate? missing = await client.Templates.FindAsync(9UL, TestContext.Current.CancellationToken);

        Assert.Equal("Linux", found?.TechnicalName);
        Assert.Null(missing);
        Assert.All(api.ParamsOf("template.get"), call => Assert.False(call.TryGetProperty("filter", out _)));
        Assert.Equal("""["10001"]""", api.ParamsOf("template.get").First().GetProperty("templateids").GetRawText());
    }

    [Fact]
    public async Task A_batch_create_sends_an_array()
    {
        using ScriptedApi api = new ScriptedApi().On("hostgroup.create", """{"groupids":["5","6"]}""");
        (ZabbixClient client, _) = Build(api);

        IReadOnlyList<ulong> ids = await client.HostGroups.CreateAsync([new ZabbixHostGroupCreateRequest { Name = "A" }, new ZabbixHostGroupCreateRequest { Name = "B" }], TestContext.Current.CancellationToken);

        Assert.Equal<ulong>([5, 6], ids);
        Assert.Equal(2, api.ParamsOf("hostgroup.create").Single().GetArrayLength());
    }

    [Fact]
    public async Task Singletons_and_other_methods_are_typed_from_their_rules()
    {
        using ScriptedApi api = new ScriptedApi()
            .On("settings.get", """{"default_theme":"blue-theme","max_in_table":"50","ha_failover_delay":"1m"}""")
            .On("event.acknowledge", """{"eventids":["77"]}""")
            .On("user.unblock", """{"userids":["3"]}""");
        (ZabbixClient client, _) = Build(api);

        ZabbixSettingsRecord settings = await client.Settings.GetAsync(cancellationToken: TestContext.Current.CancellationToken);
        IReadOnlyList<ulong> acknowledged = await client.Events.AcknowledgeAsync(new ZabbixEventAcknowledgeRequest { EventIds = [77], Action = ZabbixEventActions.Close | ZabbixEventActions.Message, Message = "done" }, TestContext.Current.CancellationToken);
        await client.Users.UnblockAsync([3], TestContext.Current.CancellationToken);

        Assert.Equal("blue-theme", settings.DefaultTheme);
        Assert.Equal("1m", settings.HaFailoverDelay);
        Assert.Equal<ulong>([77], acknowledged);
        Assert.Equal(5, api.ParamsOf("event.acknowledge").Single().GetProperty("action").GetInt32());
        Assert.Equal("3", api.ParamsOf("user.unblock").Single()[0].GetString());
    }

    [Fact]
    public async Task Apiinfo_version_sends_an_empty_object_and_returns_the_text()
    {
        using ScriptedApi api = new ScriptedApi().On("apiinfo.version", "\"7.4.15\"");
        (ZabbixClient client, _) = Build(api);

        string version = await client.ApiInfo.GetVersionAsync(cancellationToken: TestContext.Current.CancellationToken);

        Assert.Equal("7.4.15", version);
        Assert.Equal(JsonValueKind.Object, api.ParamsOf("apiinfo.version").Single().ValueKind);
    }

    [Fact]
    public void The_converters_speak_the_api_s_dialect()
    {
        JsonTypeInfo<ZabbixItem> item = ZabbixApiJson.Default.ZabbixItem;
        JsonTypeInfo<ZabbixDashboard> dashboard = ZabbixApiJson.Default.ZabbixDashboard;

        ZabbixItem read = JsonSerializer.Deserialize("""{"itemid":"1","value_type":"3","status":"0","lastclock":"1700000000","lastns":"12","hosts":[{"hostid":"1","host":"h"}]}""", item)!;
        string written = JsonSerializer.Serialize(new ZabbixItem { ItemId = 1, ValueType = ZabbixItemValueType.Text, Status = ZabbixItemStatus.Disabled }, item);
        string flag = JsonSerializer.Serialize(new ZabbixDashboard { DashboardId = 1, AutoStart = true }, dashboard);

        Assert.Equal(ZabbixItemValueType.Unsigned, read.ValueType);
        Assert.Equal(ZabbixItemStatus.Active, read.Status);                                      // ITEM_STATUS_ACTIVE = 0
        Assert.Equal(new ZabbixClock(1_700_000_000, 12), read.LastClockExact);
        Assert.Equal("h", read.HostName);
        Assert.Null(new ZabbixItem { HostId = 10084 }.HostName);                               // an id is not a name
        Assert.Equal("""{"itemid":"1","status":1,"value_type":4}""", written);
        Assert.Equal("""{"dashboardid":"1","auto_start":"1"}""", flag);                          // an integer flag goes as "1", never as true
    }

    [Fact]
    public void A_field_the_description_does_not_model_is_kept_not_dropped()
    {
        ZabbixHost host = JsonSerializer.Deserialize("""{"hostid":"1","wizard_ready":"1","brand_new_in_9_0":{"x":1}}""", ZabbixApiJson.Default.ZabbixHost)!;

        Assert.NotNull(((IZabbixAdditionalFields)host).AdditionalFields);
        Assert.Equal(1, ((IZabbixAdditionalFields)host).AdditionalFields!["brand_new_in_9_0"].GetProperty("x").GetInt32());
    }

    // ----- request/parameters extension data: a caller (the PowerShell -Properties hashtable, or an SDK user on a
    // newer Zabbix) can send a field a *CreateRequest/*UpdateRequest/*MassUpdateRequest/*GetParameters record does
    // not model -----------------------------------------------------------------------------------------------

    private const string TinyRequest = """
        {
         "versions": ["7.0", "8.0"],
         "enums": [],
         "types": [
          {"name": "ThingCreateRequest", "role": "request", "fields": [
            {"name": "Name", "wire": "name", "kind": "string"}
          ]},
          {"name": "ThingGetParameters", "role": "get", "getParameters": true, "fields": [
            {"name": "Limit", "wire": "limit", "kind": "int"}
          ]},
          {"name": "ThingFilter", "role": "nested-request", "fields": [
            {"name": "Tag", "wire": "tag", "kind": "string"}
          ]}
         ],
         "services": []
        }
        """;

    [Fact]
    public void Every_top_level_request_and_getparameters_record_gets_a_settable_extension_data_bag_but_a_nested_one_does_not()
    {
        GeneratorDriverRunResult result = Run(TinyRequest, "Models");

        Assert.Empty(result.Diagnostics);
        string create = result.GeneratedTrees.Single(t => t.FilePath.EndsWith("ZabbixThingCreateRequest.g.cs", StringComparison.Ordinal)).GetText(TestContext.Current.CancellationToken).ToString();
        string get = result.GeneratedTrees.Single(t => t.FilePath.EndsWith("ZabbixThingGetParameters.g.cs", StringComparison.Ordinal)).GetText(TestContext.Current.CancellationToken).ToString();
        string filter = result.GeneratedTrees.Single(t => t.FilePath.EndsWith("ZabbixThingFilter.g.cs", StringComparison.Ordinal)).GetText(TestContext.Current.CancellationToken).ToString();

        Assert.Contains("[global::System.Text.Json.Serialization.JsonExtensionData]", create, StringComparison.Ordinal);
        Assert.Contains("IDictionary<string, global::System.Text.Json.JsonElement>? AdditionalFields { get; set; }", create, StringComparison.Ordinal);
        Assert.Contains("IJsonOnSerializing", create, StringComparison.Ordinal);
        Assert.Contains("ZabbixExtensionDataGuard.Refuse(AdditionalFields, \"ZabbixThingCreateRequest\"", create, StringComparison.Ordinal);
        Assert.Contains("[global::System.Text.Json.Serialization.JsonExtensionData]", get, StringComparison.Ordinal);
        Assert.Contains("IJsonOnSerializing", get, StringComparison.Ordinal);
        Assert.DoesNotContain("AdditionalFields", filter, StringComparison.Ordinal);
        Assert.DoesNotContain("IJsonOnSerializing", filter, StringComparison.Ordinal);
    }

    [Fact]
    public void A_field_a_request_record_does_not_model_serializes_beside_the_modelled_fields()
    {
        var request = new ZabbixHostCreateRequest
        {
            TechnicalName = "h",
            Groups = [new ZabbixHostGroupRequest { GroupId = 1 }],
            AdditionalFields = new Dictionary<string, JsonElement> { ["newfield"] = JsonSerializer.SerializeToElement("x") },
        };

        string written = JsonSerializer.Serialize(request, ZabbixApiJson.Default.ZabbixHostCreateRequest);

        Assert.Contains("\"host\":\"h\"", written, StringComparison.Ordinal);
        Assert.Contains("\"newfield\":\"x\"", written, StringComparison.Ordinal);
    }

    [Fact]
    public void A_field_a_request_record_already_models_cannot_be_sent_twice_through_the_bag()
    {
        var request = new ZabbixHostGroupCreateRequest
        {
            Name = "g",
            AdditionalFields = new Dictionary<string, JsonElement> { ["name"] = JsonSerializer.SerializeToElement("dup") },
        };

        ArgumentException error = Assert.Throws<ArgumentException>(() => JsonSerializer.Serialize(request, ZabbixApiJson.Default.ZabbixHostGroupCreateRequest));

        Assert.Contains("name", error.Message, StringComparison.Ordinal);
        Assert.Contains("ZabbixHostGroupCreateRequest", error.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void A_field_a_getparameters_record_does_not_model_serializes_beside_the_modelled_fields()
    {
        var parameters = new ZabbixHostGetParameters
        {
            Limit = 5,
            AdditionalFields = new Dictionary<string, JsonElement> { ["unheardof"] = JsonSerializer.SerializeToElement(1) },
        };

        string written = JsonSerializer.Serialize(parameters, ZabbixApiJson.Default.ZabbixHostGetParameters);

        Assert.Contains("\"limit\":5", written, StringComparison.Ordinal);
        Assert.Contains("\"unheardof\":1", written, StringComparison.Ordinal);
    }

    [Fact]
    public void An_output_is_a_keyword_or_a_field_list_and_a_field_value_is_a_scalar_or_a_list()
    {
        JsonTypeInfo<ZabbixHostGetParameters> info = ZabbixApiJson.Default.ZabbixHostGetParameters;

        Assert.Equal("""{"output":"count"}""", JsonSerializer.Serialize(new ZabbixHostGetParameters { Output = "count" }, info));
        Assert.Equal("""{"output":["hostid","host"]}""", JsonSerializer.Serialize(new ZabbixHostGetParameters { Output = new[] { "hostid", "host" } }, info));
        Assert.Equal("""{"filter":{"status":1,"host":"a","flags":[0,4]}}""", JsonSerializer.Serialize(new ZabbixHostGetParameters { Filter = new() { ["status"] = 1L, ["host"] = "a", ["flags"] = new long[] { 0, 4 } } }, info));
    }

    [Fact]
    public void A_typed_filter_and_search_send_the_wire_names_and_values_the_dictionary_would()
    {
        JsonTypeInfo<ZabbixHostGetParameters> info = ZabbixApiJson.Default.ZabbixHostGetParameters;
        var parameters = new ZabbixHostGetParameters
        {
            Output = ZabbixOutput.Of(ZabbixHostFields.HostId, ZabbixHostFields.VisibleName),
            Filter = new ZabbixHostGetFilter { TechnicalName = ["web-01", "web-02"], Status = [ZabbixHostStatus.Disabled], Ip = ["10.0.0.1"] },
            Search = new ZabbixHostGetSearch { VisibleName = "web" },
            SortField = [ZabbixHostFields.VisibleName],
        };

        Assert.Equal(
            """{"filter":{"host":["web-01","web-02"],"ip":["10.0.0.1"],"status":[1]},"search":{"name":"web"},"output":["hostid","name"],"sortfield":["name"]}""",
            JsonSerializer.Serialize(parameters, info));
        Assert.Equal(parameters, parameters with
        {
            Filter = new() { ["host"] = new[] { "web-01", "web-02" }, ["ip"] = new[] { "10.0.0.1" }, ["status"] = new long[] { 1 } },
        });
        Assert.Equal("host", ZabbixHostFields.TechnicalName);
    }

    [Fact]
    public void A_typed_filter_is_a_read_only_dictionary_of_the_fields_it_sets()
    {
        var filter = new ZabbixItemGetFilter { Type = [ZabbixItemType.HttpAgent, ZabbixItemType.Trapper] };

        Assert.Equal(["type"], filter.Keys);
        Assert.True(filter.ContainsKey("type"));
        Assert.False(filter.TryGetValue("key_", out _));
        Assert.Equal(new ZabbixFieldValue(JsonSerializer.SerializeToElement(new long[] { 19, 2 })), filter["type"]);
        Assert.Throws<KeyNotFoundException>(() => filter["key_"]);
        Assert.Equal(new ZabbixItemGetFilter { Type = [ZabbixItemType.HttpAgent, ZabbixItemType.Trapper] }, filter);
        Assert.NotEqual(new ZabbixItemGetFilter { Type = [ZabbixItemType.Trapper, ZabbixItemType.HttpAgent] }, filter);
        Assert.Equal("ZabbixItemGetFilter { type = [19,2] }", filter.ToString());
        Assert.Empty(new ZabbixItemGetFilter());
    }

    [Fact]
    public void Every_get_with_a_read_record_has_its_field_names_and_typed_filter()
    {
        ApiDescription description = ApiDescription.Read(File.ReadAllText(SchemaPath));
        Assembly api = typeof(ZabbixHost).Assembly;
        string[] gets = [.. description.Types
            .Where(t => t.Role == "get" && t.GetParameters && t.Name.EndsWith("GetParameters", StringComparison.Ordinal))
            .Where(t => description.Services.Any(s => s.Object == t.Object && s.Model is not null))
            .Select(t => t.Name)];

        Assert.NotEmpty(gets);
        Assert.All(gets, get => Assert.NotNull(api.GetType("Community.Zabbix.Zabbix" + get[..^"Parameters".Length] + "Filter")));
        Assert.All(description.Services.Where(s => s.Model is not null && gets.Any(g => description.Types.Any(t => t.Name == g && t.Object == s.Object))),
            service => Assert.NotNull(api.GetType("Community.Zabbix.Zabbix" + service.Model + "Fields")));
    }

    [Fact]
    public void A_code_shaped_field_is_an_enum_or_a_flag_unless_reviewed()
    {
        // A field whose name says it holds a code (a type, a mode, a status, a state, a show_ flag) is an enum or a bool.
        // Reviewed, each an integer for a reason: a CEP rule operation's type is a class constant whose set depends on
        // the execution mode; a connector's item_value_type and a media type's tokens_status are bit sets no define names
        // bit by bit; authtype is two enums by item type (HTTP_AUTH_* for HTTP agent items, ITEM_AUTHTYPE_* for SSH);
        // a map element's viewtype has one define (SYSMAP_ELEMENT_AREA_VIEWTYPE_GRID).
        string[] reviewed =
        [
            "CepRuleOperation.type", "CepRuleOperationFilterConditionRequest.type", "CepRuleOperationRequest.type",
            "Connector.item_value_type", "ConnectorCreateRequest.item_value_type", "ConnectorUpdateRequest.item_value_type",
            "DiscoveryRule.authtype", "DiscoveryRuleCreateRequest.authtype", "DiscoveryRulePrototype.authtype",
            "DiscoveryRulePrototypeCreateRequest.authtype", "DiscoveryRulePrototypeUpdateRequest.authtype", "DiscoveryRuleUpdateRequest.authtype",
            "Item.authtype", "ItemCreateRequest.authtype", "ItemPrototype.authtype", "ItemPrototypeCreateRequest.authtype",
            "ItemPrototypeUpdateRequest.authtype", "ItemUpdateRequest.authtype",
            "MapElement.viewtype", "MapElementRequest.viewtype",
            "MediaType.tokens_status", "MediaTypeCreateRequest.tokens_status", "MediaTypeUpdateRequest.tokens_status",
        ];
        var code = new System.Text.RegularExpressions.Regex("(type|mode|status|state|automatic)$|^show_", System.Text.RegularExpressions.RegexOptions.CultureInvariant);

        string[] integers = [.. ApiDescription.Read(File.ReadAllText(SchemaPath)).Types
            .SelectMany(t => t.Fields.Where(f => f.Kind is "int" or "list:int" && code.IsMatch(f.Wire)).Select(f => t.Name + "." + f.Wire))
            .Order(StringComparer.Ordinal)];

        Assert.Equal(reviewed.Order(StringComparer.Ordinal), integers);
        Assert.Equal(typeof(ZabbixGraphYAxisType?), typeof(ZabbixGraph).GetProperty(nameof(ZabbixGraph.YMaxType))!.PropertyType);
        Assert.Equal(typeof(bool?), typeof(ZabbixGraph).GetProperty(nameof(ZabbixGraph.ShowLegend))!.PropertyType);
        Assert.Equal(typeof(ZabbixPreprocessingType?), typeof(ZabbixPreprocessingStep).GetProperty(nameof(ZabbixPreprocessingStep.Type))!.PropertyType);
        Assert.Equal(typeof(ZabbixDiscoveryState?), typeof(ZabbixHostDiscovery).GetProperty(nameof(ZabbixHostDiscovery.Status))!.PropertyType);
    }

    [Theory]
    [InlineData(typeof(ZabbixHost), "Tags", typeof(ZabbixTag))]
    [InlineData(typeof(ZabbixItem), "Tags", typeof(ZabbixTag))]
    [InlineData(typeof(ZabbixTrigger), "Tags", typeof(ZabbixTag))]
    [InlineData(typeof(ZabbixEvent), "Tags", typeof(ZabbixTag))]
    [InlineData(typeof(ZabbixProblem), "Tags", typeof(ZabbixTag))]
    [InlineData(typeof(ZabbixTemplate), "InheritedTags", typeof(ZabbixInheritedTag))]
    [InlineData(typeof(ZabbixHttpTest), "InheritedTags", typeof(ZabbixInheritedTag))]
    [InlineData(typeof(ZabbixHostCreateRequest), "Tags", typeof(ZabbixTagRequest))]
    [InlineData(typeof(ZabbixServiceUpdateRequest), "Tags", typeof(ZabbixTagRequest))]
    [InlineData(typeof(ZabbixHostGetParameters), "Tags", typeof(ZabbixTagFilter))]
    [InlineData(typeof(ZabbixEventGetParameters), "Tags", typeof(ZabbixTagFilter))]
    [InlineData(typeof(ZabbixSlaGetParameters), "ServiceTags", typeof(ZabbixTagFilter))]
    [InlineData(typeof(ZabbixItem), "Preprocessing", typeof(ZabbixPreprocessingStep))]
    [InlineData(typeof(ZabbixDiscoveryRulePrototype), "Preprocessing", typeof(ZabbixPreprocessingStep))]
    [InlineData(typeof(ZabbixItemCreateRequest), "Preprocessing", typeof(ZabbixPreprocessingStepRequest))]
    [InlineData(typeof(ZabbixDiscoveryRuleUpdateRequest), "Preprocessing", typeof(ZabbixPreprocessingStepRequest))]
    [InlineData(typeof(ZabbixDiscoveryRule), "DiscoveryData", typeof(ZabbixItemDiscoveryData))]
    [InlineData(typeof(ZabbixHostPrototype), "DiscoveryData", typeof(ZabbixHostDiscoveryData))]
    [InlineData(typeof(ZabbixGraphPrototype), "DiscoveryData", typeof(ZabbixGraphDiscoveryData))]
    [InlineData(typeof(ZabbixTriggerPrototype), "DiscoveryData", typeof(ZabbixTriggerDiscoveryData))]
    public void One_shape_is_one_record_whichever_object_carries_it(Type owner, string property, Type shared)
    {
        Type type = owner.GetProperty(property)!.PropertyType;
        Type element = type.IsGenericType ? type.GetGenericArguments()[0] : Nullable.GetUnderlyingType(type) ?? type;
        Assert.Equal(shared, element);
    }

    [Theory]
    [InlineData("ZabbixHostTag")]
    [InlineData("ZabbixItemTagRequest")]
    [InlineData("ZabbixEventTagFilter")]
    [InlineData("ZabbixHostInheritedTag")]
    [InlineData("ZabbixItemPreprocessing")]
    [InlineData("ZabbixItemPrototypePreprocessingRequest")]
    [InlineData("ZabbixDiscoveryRuleDiscoveryData")]
    [InlineData("ZabbixHostPrototypeDiscoveryData")]
    public void A_per_object_copy_of_a_shared_record_is_not_part_of_the_model(string name)
        => Assert.Null(typeof(ZabbixTag).Assembly.GetType("Community.Zabbix." + name));

    [Fact]
    public void A_read_tag_becomes_the_request_that_sets_it_again()
    {
        Assert.Equal(new ZabbixTagRequest { Tag = "env", Value = "prod" }, new ZabbixTag { Tag = "env", Value = "prod", Automatic = ZabbixTagOrigin.Manual }.ToRequest());
        Assert.Equal(new ZabbixTagRequest { Tag = "env", Value = string.Empty }, new ZabbixTag { Tag = "env" }.ToRequest());
        Assert.Throws<InvalidOperationException>(() => new ZabbixTag { Value = "prod" }.ToRequest());
    }

    [Fact]
    public async Task An_api_error_on_a_typed_call_is_the_designed_exception()
    {
        using var scripted = new ScriptedApi();                                                   // nothing scripted: every method is "not found"
        (ZabbixClient client, _) = Build(scripted);

        ZabbixApiException error = await Assert.ThrowsAsync<ZabbixApiException>(() => client.Hosts.GetAsync(cancellationToken: TestContext.Current.CancellationToken));

        Assert.Equal(-32601, error.Code);
        Assert.Equal("host.get", error.Method);
    }

    // ----- ids that are not numbers ------------------------------------------------------------------------------

    [Fact]
    public void A_cuid_id_is_a_string_not_a_zabbix_id()
    {
        // auditlog's auditid and recordsetid and ha_node's ha_nodeid are CUIDs (ZBX_TYPE_CUID, validated as API_CUID and
        // API_CUIDS): 25-character strings such as "ckr8jg5sa0000k6y5q11vcd2h", which no numeric id type can hold.
        Assert.Equal(typeof(string), typeof(ZabbixAuditLogRecord).GetProperty(nameof(ZabbixAuditLogRecord.AuditId))!.PropertyType);
        Assert.Equal(typeof(string), typeof(ZabbixAuditLogRecord).GetProperty(nameof(ZabbixAuditLogRecord.RecordSetId))!.PropertyType);
        Assert.Equal(typeof(string), typeof(ZabbixHaNode).GetProperty(nameof(ZabbixHaNode.HaNodeId))!.PropertyType);
        Assert.Equal(typeof(IReadOnlyList<string>), typeof(ZabbixAuditLogGetParameters).GetProperty(nameof(ZabbixAuditLogGetParameters.AuditIds))!.PropertyType);
        Assert.Equal(typeof(IReadOnlyList<string>), typeof(ZabbixHaNodeGetParameters).GetProperty(nameof(ZabbixHaNodeGetParameters.HaNodeIds))!.PropertyType);
    }

    // ----- a .get's own required options ------------------------------------------------------------------------

    [Fact]
    public void A_get_option_the_source_requires_is_a_required_member_and_its_get_takes_the_parameters()
    {
        // hostdashboard.get refuses a call without hostids (CHostDashboard.php 8.0:33, API_REQUIRED); serverinfo.get one
        // without serverid (CServerInfo.php 8.0:46). The record cannot be built without them, and GetAsync has no
        // "everything" overload a caller could reach for: it's one overload, whose token is optional.
        Assert.NotNull(typeof(ZabbixHostDashboardGetParameters).GetProperty(nameof(ZabbixHostDashboardGetParameters.HostIds))!.GetCustomAttribute<System.Runtime.CompilerServices.RequiredMemberAttribute>());
        Assert.NotNull(typeof(ZabbixServerInfoGetParameters).GetProperty(nameof(ZabbixServerInfoGetParameters.ServerId))!.GetCustomAttribute<System.Runtime.CompilerServices.RequiredMemberAttribute>());
        Assert.Null(typeof(ZabbixHostDashboardGetParameters).GetProperty(nameof(ZabbixHostDashboardGetParameters.DashboardIds))!.GetCustomAttribute<System.Runtime.CompilerServices.RequiredMemberAttribute>());
        Assert.All([typeof(ZabbixHostDashboardClient).GetMethod(nameof(ZabbixHostDashboardClient.GetAsync))!, typeof(ZabbixHostDashboardClient).GetMethod(nameof(ZabbixHostDashboardClient.CountAsync))!,
            typeof(ZabbixServerInfoClient).GetMethod(nameof(ZabbixServerInfoClient.GetAsync))!], method => Assert.False(method.GetParameters()[0].IsOptional));
        Assert.All([typeof(ZabbixHostDashboardClient).GetMethod(nameof(ZabbixHostDashboardClient.GetAsync))!, typeof(ZabbixServerInfoClient).GetMethod(nameof(ZabbixServerInfoClient.GetAsync))!],
            method => Assert.True(method.GetParameters()[^1].IsOptional));
        // The others take everything, the parameters or a lambda, each with a token and without one.
        Assert.NotNull(typeof(ZabbixHostClient).GetMethod(nameof(ZabbixHostClient.GetAsync), System.Type.EmptyTypes));
        Assert.NotNull(typeof(ZabbixHostClient).GetMethod(nameof(ZabbixHostClient.GetAsync), [typeof(CancellationToken)]));
        Assert.False(typeof(ZabbixHostClient).GetMethod(nameof(ZabbixHostClient.GetAsync), [typeof(ZabbixHostGetParameters), typeof(CancellationToken)])!.GetParameters()[0].IsOptional);
    }

    [Fact]
    public async Task Hostdashboard_get_sends_its_required_host_ids()
    {
        using ScriptedApi api = new ScriptedApi()
            .On("hostdashboard.get", """[{"hostid":"10084","dashboardid":"17","name":"System performance","display_period":"30","auto_start":"0","templateid":"10001"}]""");
        (ZabbixClient client, _) = Build(api);

        IReadOnlyList<ZabbixHostDashboard> dashboards = await client.HostDashboards.GetAsync(new ZabbixHostDashboardGetParameters { HostIds = [10084] }, TestContext.Current.CancellationToken);

        Assert.Equal(17UL, Assert.Single(dashboards).DashboardId);
        Assert.Equal(["10084"], Assert.Single(api.ParamsOf("hostdashboard.get")).GetProperty("hostids").EnumerateArray().Select(id => id.GetString()));
    }

    [Fact]
    public async Task Serverinfo_get_answers_the_one_object_it_names()
    {
        // CServerInfo::get returns the object itself ($server_data), not a list of rows.
        using ScriptedApi api = new ScriptedApi()
            .On("apiinfo.version", "\"8.0.0\"")
            .On("serverinfo.get", """{"serverid":"01a0ce65-17a5-73cb-a0c8-e2f82a5b7515","lastaccess":1791323857}""");
        (ZabbixClient client, _) = Build(api);

        ZabbixServerInfoRecord info = await client.ServerInfo.GetAsync(new ZabbixServerInfoGetParameters { ServerId = "01a0ce65-17a5-73cb-a0c8-e2f82a5b7515" }, TestContext.Current.CancellationToken);

        Assert.Equal("01a0ce65-17a5-73cb-a0c8-e2f82a5b7515", info.ServerId);
        Assert.Equal("01a0ce65-17a5-73cb-a0c8-e2f82a5b7515", Assert.Single(api.ParamsOf("serverinfo.get")).GetProperty("serverid").GetString());
    }

    // ----- version knowledge ------------------------------------------------------------------------------------

    [Fact]
    public void The_capability_table_knows_reads_anonymous_methods_and_versions_from_the_source()
    {
        Assert.True(ZabbixApiMethods.IsRead("host.get"));
        Assert.True(ZabbixApiMethods.IsRead("apiinfo.version"));
        Assert.False(ZabbixApiMethods.IsRead("host.create"));
        Assert.False(ZabbixApiMethods.IsRead("script.execute"));                                 // it talks to the server
        Assert.False(ZabbixApiMethods.IsRead("history.push"));
        Assert.True(ZabbixApiMethods.IsIdempotent("user.checkAuthentication"));                  // reviewed: updates lastaccess only
        Assert.False(ZabbixApiMethods.RequiresAuthentication("apiinfo.version"));
        Assert.False(ZabbixApiMethods.RequiresAuthentication("user.login"));
        Assert.False(ZabbixApiMethods.RequiresAuthentication("USER.CHECKAUTHENTICATION"));       // Zabbix lower-cases the method
        Assert.True(ZabbixApiMethods.RequiresAuthentication("host.get"));
        Assert.Equal(ZabbixRoleType.SuperAdmin, ZabbixApiMethods.All["connector.get"].MinimumUserType);
        Assert.True(ZabbixApiMethods.Supports("host.massupdate", new Version(7, 4, 15)));
        Assert.False(ZabbixApiMethods.Supports("host.massupdate", new Version(8, 0, 0)));
        Assert.False(ZabbixApiMethods.Supports("discoveryruleprototype.get", new Version(7, 0, 31)));
        Assert.True(ZabbixApiMethods.Supports("hostdashboard.get", new Version(7, 0, 31)));
        Assert.True(ZabbixApiMethods.Supports("no.such", new Version(7, 0, 0)));                 // unknown: the server's to judge
    }

    [Fact]
    public async Task A_method_the_server_s_version_lacks_is_refused_before_anything_is_sent()
    {
        using ScriptedApi api = new ScriptedApi().On("apiinfo.version", "\"7.0.31\"");
        (ZabbixClient client, _) = Build(api);

        ZabbixVersionException refused = await Assert.ThrowsAsync<ZabbixVersionException>(() => client.DiscoveryRulePrototypes.GetAsync(cancellationToken: TestContext.Current.CancellationToken));

        Assert.Equal("discoveryruleprototype.get", refused.Method);
        Assert.Equal(new Version(7, 4), refused.Since);
        Assert.Empty(api.ParamsOf("discoveryruleprototype.get"));
        Assert.IsAssignableFrom<NotSupportedException>(refused);
    }

    /// <summary>
    /// The 8.0 model is the tag 8.0.0rc1's (<c>tools/zabbix-sources.lock</c>): an event processing rule's window types are
    /// <c>CCepRuleHelper::WINDOW_*</c> as rc1 numbers them, its four APM objects exist from 8.0 only, and the version gates
    /// the 7.0, 7.4 and 8.0 sources state hold on the client.
    /// </summary>
    [Fact]
    public void The_8_0_model_is_the_8_0_0rc1_tag_s_and_the_version_gates_follow_the_sources()
    {
        Assert.Equal(
            [(0, "None"), (1, "SimpleMultiple"), (2, "CauseSymptom"), (3, "SimpleOnce"), (4, "PatternMatch")],
            System.Enum.GetValues<ZabbixCepRuleWindowType>().Select(value => ((int)value, value.ToString())));
        Version v70 = new(7, 0, 31), v74 = new(7, 4, 15), v80 = new(8, 0, 0);
        foreach (string api in new[] { "apmlog.get", "apmmetric.get", "apmspan.get", "apmtrace.get", "ceprule.get", "serverinfo.get", "device.get" })
        {
            Assert.False(ZabbixApiMethods.Supports(api, v74), api);
            Assert.True(ZabbixApiMethods.Supports(api, v80), api);
        }

        Assert.True(ZabbixApiMethods.Supports("discoveryrule.copy", v70));
        Assert.False(ZabbixApiMethods.Supports("discoveryrule.copy", v74));
        foreach (string api in new[] { "host.massupdate", "hostgroup.massupdate", "template.massupdate", "templategroup.massupdate" })
        {
            Assert.True(ZabbixApiMethods.Supports(api, v74), api);
            Assert.False(ZabbixApiMethods.Supports(api, v80), api);
        }

        Assert.False(ZabbixApiMethods.SupportsField("mediatype.create", "content_type", v74));
        Assert.False(ZabbixApiMethods.SupportsField("mediatype.create", "exec_params", v74));
        Assert.False(ZabbixApiMethods.SupportsField("usergroup.create", "rights", v74));
        Assert.False(ZabbixApiMethods.SupportsField("hostgroup.get", "monitored_hosts", v74));
        Assert.False(ZabbixApiMethods.SupportsField("hostgroup.get", "real_hosts", v74));
        Assert.True(ZabbixApiMethods.SupportsField("hostgroup.get", "real_hosts", v70));
        foreach (Type parameters in new[] { typeof(ZabbixTemplateGetParameters), typeof(ZabbixScriptGetParameters), typeof(ZabbixTriggerGetParameters), typeof(ZabbixGraphGetParameters) })
            Assert.Equal("7.4", parameters.GetProperty("SelectGroups")!.GetCustomAttribute<ZabbixUnsupportedAttribute>()!.Version);
        Assert.Equal("7.4", typeof(ZabbixUserGroupGetParameters).GetProperty("SelectRights")!.GetCustomAttribute<ZabbixUnsupportedAttribute>()!.Version);
        Assert.Equal("8.0", typeof(ZabbixRegExp).GetProperty(nameof(ZabbixRegExp.Description))!.GetCustomAttribute<ZabbixSupportedAttribute>()!.Version);
        Assert.Equal("8.0", typeof(ZabbixEvent).GetProperty(nameof(ZabbixEvent.CepRuleId))!.GetCustomAttribute<ZabbixSupportedAttribute>()!.Version);
    }

    /// <summary>
    /// A read record carries what its object's get returns, typed as the source returns it: a template is not a host
    /// (the hosts table's host-only columns are not its members), a host's and a proxy's pre-shared key is write-only, a
    /// proxy's version is the integer version (70002), a template's <c>templates</c> are its children and
    /// <c>parentTemplates</c> its parents, and a trigger's <c>description</c> is its name.
    /// </summary>
    [Fact]
    public void A_read_record_carries_what_its_get_returns_named_for_what_it_is()
    {
        foreach (string hostOnly in new[] { "HostId", "ProxyId", "Status", "IpmiAuthType", "MaintenanceStatus", "TlsConnect", "TlsPsk", "MonitoredBy", "Flags" })
            Assert.Null(typeof(ZabbixTemplate).GetProperty(hostOnly));
        Assert.Null(typeof(ZabbixHost).GetProperty("TlsPsk"));
        Assert.Null(typeof(ZabbixHost).GetProperty("TlsPskIdentity"));
        Assert.Null(typeof(ZabbixProxy).GetProperty("TlsPsk"));
        Assert.Null(typeof(ZabbixProxy).GetProperty("TlsPskIdentity"));
        Assert.NotNull(typeof(ZabbixHostUpdateRequest).GetProperty(nameof(ZabbixHostUpdateRequest.TlsPsk)));
        Assert.Equal(typeof(int?), typeof(ZabbixProxy).GetProperty(nameof(ZabbixProxy.Version))!.PropertyType);
        Assert.Equal(typeof(ZabbixProxyCompatibility?), typeof(ZabbixProxy).GetProperty(nameof(ZabbixProxy.Compatibility))!.PropertyType);
        Assert.Equal("templates", WireName(typeof(ZabbixTemplate), nameof(ZabbixTemplate.ChildTemplates)));
        Assert.Equal("parentTemplates", WireName(typeof(ZabbixTemplate), nameof(ZabbixTemplate.ParentTemplates)));
        Assert.Equal("templates", WireName(typeof(ZabbixTemplateUpdateRequest), nameof(ZabbixTemplateUpdateRequest.LinkedTemplates)));
        Assert.Equal("description", WireName(typeof(ZabbixTrigger), nameof(ZabbixTrigger.Name)));
        Assert.Equal("comments", WireName(typeof(ZabbixTrigger), nameof(ZabbixTrigger.Description)));
        Assert.Equal("description", WireName(typeof(ZabbixTriggerCreateRequest), nameof(ZabbixTriggerCreateRequest.Name)));
        Assert.Null(typeof(ZabbixTrigger).GetProperty("Discover"));
        Assert.Null(typeof(ZabbixTriggerCreateRequest).GetProperty("Discover"));
        Assert.Equal(7, (int)ZabbixTaskType.Data);
        Assert.Equal([1, 2, 3], System.Enum.GetValues<ZabbixSnmpVersion>().Select(value => (int)value));
        Assert.Equal(typeof(IReadOnlyList<string>), typeof(ZabbixCepRuleWindow).GetProperty(nameof(ZabbixCepRuleWindow.Tags))!.PropertyType);
        Assert.Equal(typeof(IReadOnlyList<ZabbixSlaRecordSchedule>), typeof(ZabbixSlaRecord).GetProperty(nameof(ZabbixSlaRecord.Schedule))!.PropertyType);
        Assert.Equal(typeof(IReadOnlyList<ZabbixUserDirectoryProvisionMedia>), typeof(ZabbixUserDirectory).GetProperty(nameof(ZabbixUserDirectory.ProvisionMedia))!.PropertyType);
        Assert.Equal(typeof(int?), typeof(ZabbixUserDirectory).GetProperty(nameof(ZabbixUserDirectory.Port))!.PropertyType);
        Assert.Equal(["AccessUserId", "UserGroupId"], typeof(ZabbixReportUserGroup).GetProperties().Where(p => p.Name.EndsWith("Id", StringComparison.Ordinal)).Select(p => p.Name).Order(StringComparer.Ordinal));

        static string WireName(Type record, string member)
            => record.GetProperty(member)!.GetCustomAttribute<System.Text.Json.Serialization.JsonPropertyNameAttribute>()!.Name;
    }

    [Fact]
    public void Members_carry_the_versions_they_arrived_in_or_left()
    {
        Assert.Equal("8.0", typeof(ZabbixItemValueType).GetField(nameof(ZabbixItemValueType.Json))!.GetCustomAttribute<ZabbixSupportedAttribute>()!.Version);
        ZabbixUnsupportedAttribute removed = typeof(ZabbixHostClient).GetMethod(nameof(ZabbixHostClient.MassUpdateAsync))!.GetCustomAttribute<ZabbixUnsupportedAttribute>()!;
        Assert.Equal("8.0", removed.Version);
        Assert.Equal("use host.update", removed.Message);
        Assert.NotNull(typeof(ZabbixHostGetParameters).GetProperty("SelectHostDiscovery")!.GetCustomAttribute<ObsoleteAttribute>());   // API_DEPRECATED
    }

    [Fact]
    public async Task Host_massupdate_is_sent_as_itself_where_the_server_has_it()
    {
        using ScriptedApi api = new ScriptedApi().On("apiinfo.version", "\"7.4.15\"").On("host.massupdate", """{"hostids":["1"]}""");
        (ZabbixClient client, _) = Build(api);

        IReadOnlyList<ulong> ids = await client.Hosts.MassUpdateAsync(new ZabbixHostMassUpdateRequest { Hosts = [new ZabbixHostMassHostRequest { HostId = 1 }], Status = ZabbixHostStatus.Disabled }, TestContext.Current.CancellationToken);

        Assert.Equal<ulong>([1], ids);
        Assert.Single(api.ParamsOf("host.massupdate"));
        Assert.Empty(api.ParamsOf("host.update"));
    }

    [Fact]
    public async Task Host_massupdate_on_8_0_is_one_host_update_array_with_the_existing_macro_ids_attached()
    {
        using ScriptedApi api = new ScriptedApi()
            .On("apiinfo.version", "\"8.0.0\"")
            .On("host.get", """[{"hostid":"1","macros":[{"hostmacroid":"91","macro":"{$A}"}]},{"hostid":"2","macros":[]}]""")
            .On("host.update", """{"hostids":["1","2"]}""");
        (ZabbixClient client, _) = Build(api);

        IReadOnlyList<ulong> ids = await client.Hosts.MassUpdateAsync(new ZabbixHostMassUpdateRequest
        {
            Hosts = [new ZabbixHostMassHostRequest { HostId = 1 }, new ZabbixHostMassHostRequest { HostId = 2 }],
            Status = ZabbixHostStatus.Disabled,
            Macros = [new ZabbixHostMassMacroRequest { Macro = "{$A}", Value = "x" }],
        }, TestContext.Current.CancellationToken);

        Assert.Equal<ulong>([1, 2], ids);
        Assert.Empty(api.ParamsOf("host.massupdate"));
        JsonElement update = api.ParamsOf("host.update").Single();
        Assert.Equal(2, update.GetArrayLength());
        Assert.Equal("1", update[0].GetProperty("hostid").GetString());
        Assert.Equal(1, update[0].GetProperty("status").GetInt32());
        Assert.Equal("91", update[0].GetProperty("macros")[0].GetProperty("hostmacroid").GetString());
        Assert.False(update[1].GetProperty("macros")[0].TryGetProperty("hostmacroid", out _));
    }

    [Fact]
    public async Task Hostgroup_massupdate_on_8_0_moves_the_members_in_one_host_update_and_refuses_to_leave_a_host_without_a_group()
    {
        using ScriptedApi api = new ScriptedApi()
            .On("apiinfo.version", "\"8.0.0\"")
            .On("host.get", p => p.TryGetProperty("hostids", out _)
                ? """[{"hostid":"1","hostgroups":[{"groupid":"7"}]}]"""
                : """[{"hostid":"2","hostgroups":[{"groupid":"5"},{"groupid":"8"}]}]""")
            .On("host.update", """{"hostids":["1","2"]}""");
        (ZabbixClient client, _) = Build(api);

        IReadOnlyList<ulong> ids = await client.HostGroups.MassUpdateAsync(new ZabbixHostGroupMassUpdateRequest
        {
            Groups = [new ZabbixHostGroupMassGroupRequest { GroupId = 5 }],
            Hosts = [new ZabbixHostGroupMassHostRequest { HostId = 1 }],
        }, TestContext.Current.CancellationToken);

        Assert.Equal<ulong>([5], ids);
        JsonElement update = api.ParamsOf("host.update").Single();
        Assert.Equal(["7", "5"], update[0].GetProperty("groups").EnumerateArray().Select(g => g.GetProperty("groupid").GetString()));   // host 1 joins group 5
        Assert.Equal(["8"], update[1].GetProperty("groups").EnumerateArray().Select(g => g.GetProperty("groupid").GetString()));        // host 2 leaves it

        ScriptedApi lonely = new ScriptedApi()
            .On("apiinfo.version", "\"8.0.0\"")
            .On("host.get", p => p.TryGetProperty("hostids", out _) ? "[]" : """[{"hostid":"2","hostgroups":[{"groupid":"5"}]}]""");
        (ZabbixClient refusing, _) = Build(lonely);
        ZabbixApiException refused = await Assert.ThrowsAsync<ZabbixApiException>(() => refusing.HostGroups.MassUpdateAsync(new ZabbixHostGroupMassUpdateRequest { Groups = [new ZabbixHostGroupMassGroupRequest { GroupId = 5 }], Hosts = [] }, TestContext.Current.CancellationToken));
        Assert.Equal(ZabbixApiErrorKind.Validation, refused.Kind);
        Assert.Empty(lonely.ParamsOf("host.update"));
    }

    [Fact]
    public async Task Replacehostinterfaces_on_8_0_is_a_host_update_with_the_interfaces_and_answers_with_their_ids()
    {
        using ScriptedApi api = new ScriptedApi()
            .On("apiinfo.version", "\"8.0.0\"")
            .On("host.update", """{"hostids":["1"]}""")
            .On("hostinterface.get", """[{"interfaceid":"30"}]""");
        (ZabbixClient client, _) = Build(api);

        IReadOnlyList<ulong> ids = await client.HostInterfaces.ReplaceHostInterfacesAsync(new ZabbixHostInterfaceReplaceHostInterfacesRequest
        {
            HostId = 1,
            Interfaces = [new ZabbixHostInterfaceReplaceHostInterfacesInterfaceRequest { Type = ZabbixInterfaceType.Agent, Main = ZabbixInterfacePriority.Primary, UseIp = ZabbixHostInterfaceUseIp.Ip, Ip = "127.0.0.1", Dns = "", Port = "10050" }],
        }, TestContext.Current.CancellationToken);

        Assert.Equal<ulong>([30], ids);
        Assert.Equal("127.0.0.1", api.ParamsOf("host.update").Single()[0].GetProperty("interfaces")[0].GetProperty("ip").GetString());
    }

    // ----- golden checks: the description carries what a reflected model gets wrong ------------------------------------

    private static JsonElement Type(string name)
        => Description.Value.RootElement.GetProperty("types").EnumerateArray().Single(t => t.GetProperty("name").GetString() == name);

    private static IEnumerable<string> Wires(string type)
        => Type(type).GetProperty("fields").EnumerateArray().Select(f => f.GetProperty("wire").GetString()!);

    private static JsonElement Field(string type, string wire)
        => Type(type).GetProperty("fields").EnumerateArray().Single(f => f.GetProperty("wire").GetString() == wire);

    private static JsonElement Enum(string name)
        => Description.Value.RootElement.GetProperty("enums").EnumerateArray().Single(e => e.GetProperty("name").GetString() == name);

    [Fact]
    public void The_get_options_are_the_ones_the_source_accepts()
    {
        Assert.Contains("selectDHosts", Wires("NetworkDiscoveryRuleGetParameters"));
        Assert.DoesNotContain("selectDiscoveredHosts", Wires("NetworkDiscoveryRuleGetParameters"));
        Assert.Contains("withUnacknowledgedEvents", Wires("TriggerGetParameters"));
        Assert.DoesNotContain("with_unacknowledged_events", Wires("TriggerGetParameters"));
        Assert.DoesNotContain("gitemids", Wires("GraphItemGetParameters"));
        Assert.DoesNotContain("with_simple_graph_items", Wires("ItemGetParameters"));
        Assert.Contains("eventsource", Wires("AlertGetParameters"));
        Assert.Contains("eventobject", Wires("AlertGetParameters"));
        Assert.Contains("objectids", Wires("AlertGetParameters"));
    }

    [Fact]
    public void Task_get_takes_only_its_three_options_so_it_is_not_queryable_and_cannot_send_limit()
    {
        Assert.Equal(["taskids", "output", "preservekeys"], Wires("TaskGetParameters"));
        Assert.Null(typeof(ZabbixTaskGetParameters).GetProperty("Limit"));
        Assert.False(typeof(IZabbixGetParameters).IsAssignableFrom(typeof(ZabbixTaskGetParameters)));
        Assert.Null(typeof(ZabbixTaskClient).GetMethod("Query"));
    }

    [Fact]
    public void Requests_carry_the_fields_and_the_required_flags_the_source_validates()
    {
        Assert.Contains("users", Wires("UserGroupCreateRequest"));
        Assert.Equal("7.4", Field("UserGroupCreateRequest", "userids").GetProperty("removed").GetString());
        Assert.True(Field("ItemCreateRequest", "hostid").GetProperty("required").GetBoolean());      // API_REQUIRED (CItem.php)
        Assert.True(typeof(ZabbixItemCreateRequest).GetProperty(nameof(ZabbixItemCreateRequest.HostId))!.GetCustomAttribute<System.Text.Json.Serialization.JsonRequiredAttribute>() is not null);
        Assert.Contains("operator", Wires("MaintenanceTagRequest"));
        Assert.Equal("list:object:MaintenanceTagRequest", Field("MaintenanceCreateRequest", "tags").GetProperty("kind").GetString());
    }

    /// <summary>
    /// An event's and a problem's <c>flags</c> are the server's <c>ZBX_EVENT_NORMAL</c>/<c>ZBX_EVENT_COPIED</c>
    /// (<c>cep/zbx_cep.h</c>), a closed set no PHP file declares: the overlay states the defines, and the field is the enum.
    /// </summary>
    [Fact]
    public void An_event_s_flags_are_its_origin_from_the_server_s_defines()
    {
        Assert.Equal(ZabbixEventOrigin.Copied, JsonSerializer.Deserialize("""{"eventid":"1","flags":"1"}""", ZabbixApiJson.Default.ZabbixEvent)!.Flags);
        Assert.Equal(ZabbixEventOrigin.Normal, JsonSerializer.Deserialize("""{"eventid":"1","flags":"0"}""", ZabbixApiJson.Default.ZabbixProblem)!.Flags);
        Assert.Equal(["ZBX_EVENT_NORMAL", "ZBX_EVENT_COPIED"], Enum("EventOrigin").GetProperty("members").EnumerateArray().Select(member => member.GetProperty("define").GetString()));
        Assert.Equal("enum:EventOrigin", Field("Problem", "flags").GetProperty("kind").GetString());
    }

    [Fact]
    public void Map_labels_and_problem_display_are_enums_from_the_defines_not_swapped_and_not_booleans()
    {
        Assert.Equal(ZabbixMapShowElementLabel.AutoHide, JsonSerializer.Deserialize("""{"show_element_label":"0"}""", ZabbixApiJson.Default.ZabbixMap)!.ShowElementLabel);
        Assert.Equal(ZabbixMapShowUnacknowledged.Both, JsonSerializer.Deserialize("""{"show_unack":"2"}""", ZabbixApiJson.Default.ZabbixMap)!.ShowUnacknowledged);
        Assert.Equal(ZabbixMapExpandProblem.ProblemsNumberCritical, JsonSerializer.Deserialize("""{"expandproblem":"2"}""", ZabbixApiJson.Default.ZabbixMap)!.ExpandProblem);
        Assert.Equal(3, Enum("MapExpandProblem").GetProperty("members").GetArrayLength());
        Assert.Equal("SYSMAP_PROBLEMS_NUMBER_CRITICAL", Enum("MapExpandProblem").GetProperty("members")[2].GetProperty("define").GetString());
    }

    [Fact]
    public void Settings_carry_every_key_the_source_returns_and_http_step_posts_are_untyped_not_a_string()
    {
        Assert.Contains("ha_failover_delay", Wires("SettingsRecord"));
        Assert.Equal("json", Field("HttpTestStepRequest", "posts").GetProperty("kind").GetString());
        Assert.Contains("automatic", Wires("Tag"));
    }

    [Fact]
    public void Statuses_are_per_object_enums_from_their_define_families()
    {
        Assert.Equal("HOST_STATUS", Enum("HostStatus").GetProperty("family").GetString());
        Assert.Equal("ZBX_CONNECTOR_STATUS", Enum("ConnectorStatus").GetProperty("family").GetString());
        Assert.Equal(0, (int)ZabbixConnectorStatus.Disabled);
        Assert.Equal(0, (int)ZabbixHostStatus.Enabled);
        Assert.Equal(0, (int)ZabbixItemStatus.Active);
    }

    [Fact]
    public async Task A_service_reads_its_children_and_parents_as_lists_and_its_status_as_ok_or_a_severity()
    {
        // CService.php 7.0:740/8.0:717 fills children with array_values(…); status is ZBX_SEVERITY_OK (-1) or a
        // TRIGGER_SEVERITY_* (7.0:1102-1130), never SERVICE_STATUS_* (the frontend filter's -1 "any").
        using ScriptedApi api = new ScriptedApi().On("service.get", """
            [{"serviceid":"1","name":"root","status":"-1",
              "children":[{"serviceid":"2","name":"leaf","status":"4"}],
              "parents":[],
              "status_rules":[{"type":"0","limit_value":"1","limit_status":"-1","new_status":"5"}]}]
            """);
        (ZabbixClient client, _) = Build(api);

        ZabbixServiceRecord service = Assert.Single(await client.Services.GetAsync(new ZabbixServiceGetParameters
        {
            SelectChildren = ZabbixOutput.Extend,
            SelectParents = ZabbixOutput.Extend,
            SelectStatusRules = ZabbixOutput.Extend,
        }, TestContext.Current.CancellationToken));

        Assert.Equal(ZabbixServiceStatus.Ok, service.Status);
        Assert.Equal(-1, (int)ZabbixServiceStatus.Ok);
        ZabbixServiceRecord child = Assert.Single(service.Children!);                        // children are services (CService: $this->doGet)
        Assert.Equal(ZabbixServiceStatus.High, child.Status);
        Assert.Empty(service.Parents!);
        ZabbixServiceRecordStatusRule rule = Assert.Single(service.StatusRules!);
        Assert.Equal(ZabbixServiceStatus.Ok, rule.LimitStatus);
        Assert.Equal(ZabbixSeverity.Disaster, rule.NewStatus);
        Assert.Equal("ZBX_SEVERITY+TRIGGER_SEVERITY", Enum("ServiceStatus").GetProperty("family").GetString());
    }

    [Theory]
    [InlineData("8.0.0")]
    [InlineData("7.4.15")]
    public async Task A_maintenance_with_hostids_is_refused_before_sending_where_the_server_no_longer_accepts_it_and_names_hosts(string version)
    {
        // CMaintenance.php 7.4/8.0 validate create/update without hostids/groupids and refuse what they do not list (no
        // API_ALLOW_UNEXPECTED); 7.0 still converts them (zbx_toObject(…, 'hostid'), 7.0:329). An 8.0.0 server answers
        // Invalid parameter "/1": unexpected parameter "hostids".
        using ScriptedApi api = new ScriptedApi().On("apiinfo.version", "\"" + version + "\"").On("maintenance.create", """{"maintenanceids":["9"]}""");
        (ZabbixClient client, _) = Build(api);
#pragma warning disable CS0618 // the deprecated field is the subject
        var request = new ZabbixMaintenanceCreateRequest { Name = "m", ActiveSince = ZabbixClock.UnixEpoch.ToDateTimeOffset(), ActiveTill = DateTimeOffset.FromUnixTimeSeconds(1), TimePeriods = [], HostIds = [10] };
#pragma warning restore CS0618

        ZabbixVersionException refused = await Assert.ThrowsAsync<ZabbixVersionException>(() => client.Maintenances.CreateAsync(request, TestContext.Current.CancellationToken));
#pragma warning disable CS0618
        ZabbixMaintenanceCreateRequest clean = request with { HostIds = null };
#pragma warning restore CS0618
        ZabbixVersionException batch = await Assert.ThrowsAsync<ZabbixVersionException>(() => client.Maintenances.CreateAsync([clean, request], TestContext.Current.CancellationToken));

        Assert.Equal("hostids", refused.Field);
        Assert.Equal("hosts", refused.Replacement);
        Assert.Equal("maintenance.create", refused.Method);
        Assert.Equal(new Version(7, 4), refused.RemovedIn);
        Assert.Contains("use hosts", refused.Message, StringComparison.Ordinal);
        Assert.Equal("hostids", batch.Field);
        Assert.Empty(api.ParamsOf("maintenance.create"));
        Assert.False(ZabbixApiMethods.SupportsField("maintenance.update", "groupids", Version.Parse(version)));
        ZabbixUnsupportedAttribute marked = typeof(ZabbixMaintenanceUpdateRequest).GetProperty("GroupIds")!.GetCustomAttribute<ZabbixUnsupportedAttribute>()!;
        Assert.Equal(("7.4", "use groups"), (marked.Version, marked.Message));
    }

    [Fact]
    public async Task A_maintenance_with_hostids_is_sent_to_7_0_which_still_converts_them()
    {
        using ScriptedApi api = new ScriptedApi().On("apiinfo.version", "\"7.0.31\"").On("maintenance.create", """{"maintenanceids":["9"]}""");
        (ZabbixClient client, _) = Build(api);
#pragma warning disable CS0618
        var request = new ZabbixMaintenanceCreateRequest { Name = "m", ActiveSince = ZabbixClock.UnixEpoch.ToDateTimeOffset(), ActiveTill = DateTimeOffset.FromUnixTimeSeconds(1), TimePeriods = [], HostIds = [10] };
#pragma warning restore CS0618

        Assert.Equal(9UL, await client.Maintenances.CreateAsync(request, TestContext.Current.CancellationToken));

        Assert.Equal(["10"], api.ParamsOf("maintenance.create").Single().GetProperty("hostids").EnumerateArray().Select(e => e.GetString()));
        Assert.True(ZabbixApiMethods.SupportsField("maintenance.create", "hostids", new Version(7, 0, 31)));
        Assert.True(ZabbixApiMethods.SupportsField("maintenance.create", "name", new Version(8, 0, 0)));   // not in the table: the server's to judge
    }

    [Fact]
    public async Task A_maintenance_without_a_refused_field_does_not_ask_the_server_s_version()
    {
        using ScriptedApi api = new ScriptedApi().On("maintenance.create", """{"maintenanceids":["9"]}""");
        (ZabbixClient client, _) = Build(api);

        await client.Maintenances.CreateAsync(new ZabbixMaintenanceCreateRequest { Name = "m", ActiveSince = ZabbixClock.UnixEpoch.ToDateTimeOffset(), ActiveTill = DateTimeOffset.FromUnixTimeSeconds(1), TimePeriods = [], Hosts = [new() { HostId = 10 }] }, TestContext.Current.CancellationToken);

        Assert.Empty(api.ParamsOf("apiinfo.version"));
    }

    // ----- requirements, list shapes and enums the extractor reads from the services' validation rules -----------------

    private static bool IsRequired(Type record, string member)
        => record.GetProperty(member)!.GetCustomAttributes().Any(a => a.GetType().FullName == "System.Runtime.CompilerServices.RequiredMemberAttribute");

    /// <summary>
    /// A member the API requires only for one item type (CItemGeneral.php 8.0:319, one
    /// rule map per type) or only on some paths (<c>$api_required = 0; if (…) { $api_required = API_REQUIRED; }</c>,
    /// CCepRule.php 8.0:513-519) is optional, so a C# caller writes a plain trapper item without <c>= null!</c>; one the
    /// update of an existing element may omit (a widget with its <c>widgetid</c>, CDashboard.php 8.0:317) is optional there.
    /// </summary>
    [Fact]
    public void A_member_required_only_for_some_types_or_paths_is_optional_and_a_trapper_item_needs_only_its_five()
    {
        var trapper = new ZabbixItemCreateRequest { HostId = 10, Name = "n", Type = ZabbixItemType.Trapper, Key = "k", ValueType = ZabbixItemValueType.Text };

        Assert.Null(trapper.MasterItemId);
        Assert.False(IsRequired(typeof(ZabbixItemCreateRequest), nameof(ZabbixItemCreateRequest.MasterItemId)));
        Assert.False(IsRequired(typeof(ZabbixItemCreateRequest), nameof(ZabbixItemCreateRequest.Url)));
        Assert.False(IsRequired(typeof(ZabbixItemCreateRequest), nameof(ZabbixItemCreateRequest.SnmpOid)));
        Assert.False(IsRequired(typeof(ZabbixCepRuleCreateRequest), nameof(ZabbixCepRuleCreateRequest.Window)));
        Assert.False(IsRequired(typeof(ZabbixCepRuleCreateRequest), nameof(ZabbixCepRuleCreateRequest.Operations)));
        Assert.True(IsRequired(typeof(ZabbixDashboardPageWidgetRequest), nameof(ZabbixDashboardPageWidgetRequest.Type)));
        Assert.False(IsRequired(typeof(ZabbixDashboardUpdatePageWidgetRequest), nameof(ZabbixDashboardUpdatePageWidgetRequest.Type)));
        Assert.Equal(typeof(IReadOnlyList<ZabbixDashboardUpdatePageWidgetRequest>), typeof(ZabbixDashboardUpdatePageRequest).GetProperty(nameof(ZabbixDashboardUpdatePageRequest.Widgets))!.PropertyType);
    }

    /// <summary>
    /// A list the API takes either as the elements one arm names or as an empty list (an <c>API_OBJECTS</c> arm of length
    /// zero) is a list of those elements: the correlation window's tags are strings (CCepRule.php 8.0:597),
    /// a percentile's parameters numbers (CItemTypeTelemetryQuery.php 8.0:513); and the window's group-by switches are
    /// flags (<c>CCepRuleHelper::GROUP_BY_NO/_YES</c>).
    /// </summary>
    [Fact]
    public void An_empty_list_arm_does_not_hide_the_element_type_and_class_constant_flags_are_booleans()
    {
        Assert.Equal(typeof(IReadOnlyList<string>), typeof(ZabbixCepRuleWindowRequest).GetProperty(nameof(ZabbixCepRuleWindowRequest.Tags))!.PropertyType);
        Assert.Equal(typeof(bool?), typeof(ZabbixCepRuleWindowRequest).GetProperty(nameof(ZabbixCepRuleWindowRequest.GroupByTags))!.PropertyType);
        Assert.Equal("list:double", Field("ItemQueryAggregatedColumnRequest", "parameters").GetProperty("kind").GetString());
    }

    /// <summary>
    /// Enums the newer services validate with class constants (<c>CCepRuleHelper::WINDOW_*</c>, CCepRule.php 8.0:409;
    /// <c>CItemTypeTelemetryQuery::SIGNAL_TYPE_*</c>; <c>CMediatypeHelper::EMAIL_PROVIDER_*</c>) are enums like the defines'.
    /// </summary>
    [Fact]
    public void Class_constant_families_are_enums()
    {
        Assert.Equal(typeof(ZabbixCepRuleWindowType?), typeof(ZabbixCepRuleCreateRequest).GetProperty(nameof(ZabbixCepRuleCreateRequest.WindowType))!.PropertyType);
        Assert.Equal(4, (int)ZabbixCepRuleWindowType.PatternMatch);
        Assert.Equal(typeof(ZabbixTelemetrySignalType), typeof(ZabbixItemQueryRequest).GetProperty(nameof(ZabbixItemQueryRequest.SignalType))!.PropertyType);
        Assert.Equal(typeof(ZabbixMediaTypeEmailProvider?), typeof(ZabbixMediaTypeCreateRequest).GetProperty(nameof(ZabbixMediaTypeCreateRequest.Provider))!.PropertyType);
        Assert.Equal(typeof(IReadOnlyList<ZabbixSeverity>), typeof(ZabbixProblemGetParameters).GetProperty(nameof(ZabbixProblemGetParameters.Severities))!.PropertyType);
    }

    /// <summary>
    /// A map element carries what CMapElement stores (the sysmaps_elements row: icons, position, label,
    /// CMapElement.php 8.0:300-314), its elements (one id by element type, :110-170) and urls; an interface's SNMP details
    /// are the interface_snmp row, typed; a graph is created with its items (CGraph.php <c>gitems</c>).
    /// </summary>
    [Fact]
    public void A_map_element_an_interface_s_details_and_a_graph_s_items_are_typed_records()
    {
        var element = new ZabbixMapElementRequest
        {
            ElementType = ZabbixMapElementType.Host,
            IconIdOff = 151,
            X = 10,
            Y = 20,
            Elements = [new ZabbixMapElementElementRequest { HostId = 10084 }],
        };
        string json = JsonSerializer.Serialize(element, ZabbixApiJson.Default.ZabbixMapElementRequest);

        Assert.Equal("""{"elementtype":0,"iconid_off":"151","x":10,"y":20,"elements":[{"hostid":"10084"}]}""", json);
        Assert.Equal(typeof(ZabbixSnmpVersion?), typeof(ZabbixHostInterfaceDetailRequest).GetProperty(nameof(ZabbixHostInterfaceDetailRequest.Version))!.PropertyType);
        Assert.Equal(typeof(ZabbixHostInterfaceDetailRequest), typeof(ZabbixHostInterfaceCreateRequest).GetProperty(nameof(ZabbixHostInterfaceCreateRequest.Details))!.PropertyType);
        Assert.Contains("gitems", Wires("GraphCreateRequest"));
    }

    /// <summary>
    /// <c>user.get</c> answers a media's <c>sendto</c> as an array for e-mail and push media and as a
    /// string for the others (CUser.php 8.0:3540-3553); one reply carries both, and both read.
    /// </summary>
    [Fact]
    public void A_user_media_s_sendto_reads_as_a_list_whether_the_server_wrote_a_string_or_an_array()
    {
        IReadOnlyList<ZabbixUser>? users = JsonSerializer.Deserialize(
            """[{"userid":"1","medias":[{"mediatypeid":"1","sendto":["a@x","b@x"],"active":"0"},{"mediatypeid":"3","sendto":"+1555","active":"1"}]}]""",
            ZabbixApiJson.Default.IReadOnlyListZabbixUser);

        IReadOnlyList<ZabbixUserMedia> medias = Assert.Single(users!).Medias!;
        Assert.Equal(["a@x", "b@x"], medias[0].SendTo);
        Assert.Equal(["+1555"], medias[1].SendTo);
        Assert.Equal(ZabbixMediaStatus.Disabled, medias[1].Active);
        Assert.Throws<JsonException>(() => JsonSerializer.Deserialize("""[{"sendto":5}]""", ZabbixApiJson.Default.IReadOnlyListZabbixUserMedia));
    }

    /// <summary>
    /// A maintenance period's <c>every</c> is an interval for daily and weekly periods
    /// and a week of the month for monthly ones, so it is a number; <c>map.update</c> takes the shapes, users, user groups
    /// and urls <c>map.create</c> does (CMap.php 8.0:3097-3110); what the selects return is typed (map lines, links, users,
    /// maintenance periods, dashboard widgets, host inventory, trigger dependencies) and the audit log's action and resource
    /// are the <c>CAudit</c> constants.
    /// </summary>
    [Fact]
    public void Maintenance_periods_map_updates_and_the_selects_read_typed()
    {
        Assert.Equal(typeof(int?), typeof(ZabbixMaintenanceTimePeriodRequest).GetProperty(nameof(ZabbixMaintenanceTimePeriodRequest.Every))!.PropertyType);
        Assert.Equal(typeof(IReadOnlyList<ZabbixMapUpdateShapeRequest>), typeof(ZabbixMapUpdateRequest).GetProperty(nameof(ZabbixMapUpdateRequest.Shapes))!.PropertyType);
        Assert.Contains("users", Wires("MapUpdateRequest"));
        Assert.Contains("userGroups", Wires("MapUpdateRequest"));
        Assert.Contains("urls", Wires("MapUpdateRequest"));
        Assert.Contains("urls", Wires("MapCreateRequest"));

        ZabbixMap map = JsonSerializer.Deserialize(
            """{"sysmapid":"1","lines":[{"sysmap_shapeid":"4","x1":"1","y1":"2","x2":"3","y2":"4","line_type":"1"}],"links":[{"linkid":"9","selementid1":"5","selementid2":"6","label":"l"}],"users":[{"sysmapuserid":"2","userid":"1","permission":"3"}]}""",
            ZabbixApiJson.Default.ZabbixMap)!;
        Assert.Equal(3, Assert.Single(map.Lines!).X2);
        Assert.Equal(6UL, Assert.Single(map.Links!).Element2Id);
        Assert.Equal(1UL, Assert.Single(map.Users!).UserId);

        Assert.Equal(typeof(IReadOnlyList<ZabbixMaintenanceRecordTimePeriod>), typeof(ZabbixMaintenanceRecord).GetProperty(nameof(ZabbixMaintenanceRecord.TimePeriods))!.PropertyType);
        Assert.Equal(typeof(IReadOnlyList<ZabbixDashboardPageWidget>), typeof(ZabbixDashboardPage).GetProperty(nameof(ZabbixDashboardPage.Widgets))!.PropertyType);
        Assert.Equal(typeof(ZabbixHostInventory), typeof(ZabbixHost).GetProperty(nameof(ZabbixHost.Inventory))!.PropertyType);
        Assert.Equal(typeof(IReadOnlyList<ZabbixTrigger>), typeof(ZabbixTrigger).GetProperty(nameof(ZabbixTrigger.Dependencies))!.PropertyType);
        Assert.Equal(typeof(ZabbixAuditAction?), typeof(ZabbixAuditLogRecord).GetProperty(nameof(ZabbixAuditLogRecord.Action))!.PropertyType);
        Assert.Equal(58, (int)ZabbixAuditResource.CepRule);
        Assert.Equal(typeof(ZabbixServiceProblemTagOperator?), typeof(ZabbixServiceRecordProblemTag).GetProperty(nameof(ZabbixServiceRecordProblemTag.Operator))!.PropertyType);
    }

    /// <summary>
    /// 8.0's <c>history.get</c> cuts a binary or JSON value at 64 KiB unless <c>maxValueSize</c> is
    /// sent as <c>null</c> (CHistory.php 8.0:121, 238), so the option says three things: left out, <c>null</c>, a size.
    /// </summary>
    [Fact]
    public void History_get_s_max_value_size_is_absent_null_or_a_size()
    {
        string Wire(ZabbixHistoryGetParameters parameters) => JsonSerializer.Serialize(parameters, ZabbixApiJson.Default.ZabbixHistoryGetParameters);

        Assert.DoesNotContain("maxValueSize", Wire(new ZabbixHistoryGetParameters { History = ZabbixItemValueType.Json }), StringComparison.Ordinal);
        Assert.Contains("\"maxValueSize\":null", Wire(new ZabbixHistoryGetParameters { History = ZabbixItemValueType.Json, MaxValueSize = ZabbixOptional<int>.Null }), StringComparison.Ordinal);
        Assert.Contains("\"maxValueSize\":1024", Wire(new ZabbixHistoryGetParameters { History = ZabbixItemValueType.Json, MaxValueSize = 1024 }), StringComparison.Ordinal);
        Assert.False(ZabbixOptional<int>.FromNullable(null).IsSent);
        Assert.True(JsonSerializer.Deserialize("""{"maxValueSize":null}""", ZabbixApiJson.Default.ZabbixHistoryGetParameters)!.MaxValueSize.IsNull);
    }

    private const string Secret = "zbx-test-zabbix-source-SECRET-5d1e";

    private static IEnumerable<Type> GeneratedSecretRecords()
        => typeof(ZabbixHost).Assembly.GetExportedTypes()
            .Where(type => type.GetMethod("<Clone>$") is not null)
            .Where(type => type.GetProperties().Any(p => p.GetCustomAttribute<ZabbixSecretAttribute>() is not null));

    /// <summary>The generated records that hold a secret, by name.</summary>
    public static TheoryData<string> SecretRecordNames => [.. GeneratedSecretRecords().Select(type => type.Name).Order(StringComparer.Ordinal)];

    /// <summary>
    /// Every generated member that holds a password, passphrase, key or token carries
    /// <see cref="ZabbixSecretAttribute"/>: the names the Zabbix audit log masks (<c>CAudit::MASKED_PATHS</c>) and their
    /// kin it does not audit (<c>ipmi_password</c>, <c>tls_psk</c>, SNMPv3 passphrases, <c>http_password</c>).
    /// </summary>
    [Fact]
    public void Every_generated_secret_member_is_classified()
    {
        DesignConformanceTests.AssertNone(AdversarialDesignConformanceTests.UnclassifiedSecrets(typeof(ZabbixHost).Assembly.GetExportedTypes()));
        Assert.NotNull(typeof(ZabbixHostUpdateRequest).GetProperty(nameof(ZabbixHostUpdateRequest.TlsPsk))!.GetCustomAttribute<ZabbixSecretAttribute>());
        Assert.NotNull(typeof(ZabbixUserDirectoryCreateRequest).GetProperty(nameof(ZabbixUserDirectoryCreateRequest.SpPrivateKey))!.GetCustomAttribute<ZabbixSecretAttribute>());
        Assert.Null(typeof(ZabbixHost).GetProperty(nameof(ZabbixHost.TechnicalName))!.GetCustomAttribute<ZabbixSecretAttribute>());
    }

    /// <summary>
    /// A generated record's text form prints every member as the compiler's would and a
    /// secret one as <c>***</c>, so a host with a PSK can be logged, thrown or shown without the key.
    /// </summary>
    /// <param name="name">The record.</param>
    [Theory]
    [MemberData(nameof(SecretRecordNames))]
    public void A_generated_record_s_text_form_never_carries_a_secret(string name)
    {
        Type type = GeneratedSecretRecords().Single(candidate => candidate.Name == name);
        object record = Activator.CreateInstance(type)!;
        foreach (PropertyInfo property in type.GetProperties().Where(p => p.GetCustomAttribute<ZabbixSecretAttribute>() is not null && p.SetMethod is not null))
            property.SetValue(record, property.PropertyType == typeof(string) ? Secret : new[] { Secret });

        string text = record.ToString()!;

        Assert.DoesNotContain(Secret, text, StringComparison.Ordinal);
        Assert.Contains("***", text, StringComparison.Ordinal);
    }

    [Fact]
    public void A_record_s_text_form_keeps_its_other_members()
    {
        var host = new ZabbixHostUpdateRequest { HostId = 10084, TlsPsk = Secret };

        string text = host.ToString();

        Assert.Equal("ZabbixHostUpdateRequest { HostId = 10084, TlsPsk = *** }", text);
    }

    /// <summary>
    /// A record's text form names what is set and nothing else: a read record has some forty members and a reply sets a
    /// handful, a list prints how many it holds (not its type name), the bag how many fields it keeps, and an empty record
    /// is just its name.
    /// </summary>
    [Fact]
    public void A_record_s_text_form_leaves_out_what_is_not_set_and_counts_its_lists()
    {
        ZabbixHost host = JsonSerializer.Deserialize("""{"hostid":"10084","host":"web-01","status":"1","tags":[{"tag":"a","value":"1"},{"tag":"b","value":"2"}],"groups":[{"groupid":"2"}],"brand_new":"x"}""", ZabbixApiJson.Default.ZabbixHost)!;

        Assert.Equal("ZabbixHost { HostId = 10084, TechnicalName = web-01, Status = Disabled, Tags = [2 items], Groups = [1 item], AdditionalFields = [1 item] }", host.ToString());
        Assert.Equal("ZabbixHostGetParameters { }", new ZabbixHostGetParameters().ToString());
        Assert.Equal("ZabbixHistoryGetParameters { MaxValueSize = 10 }", new ZabbixHistoryGetParameters { MaxValueSize = 10 }.ToString());
    }

    /// <summary>
    /// A read record's unmodelled fields are not one of its public properties, so a
    /// serializer that reflects public properties (PowerShell's <c>ConvertTo-Json</c>) does not write the bag's plumbing;
    /// they are reached through <see cref="IZabbixAdditionalFields"/>, and the SDK's own JSON contract still reads and
    /// writes them. A request keeps its public, settable bag (the caller's escape hatch).
    /// </summary>
    [Fact]
    public void A_read_record_s_unmodelled_fields_are_behind_an_interface_not_a_public_property()
    {
        ZabbixHost host = JsonSerializer.Deserialize("""{"hostid":"1","brand_new":"x"}""", ZabbixApiJson.Default.ZabbixHost)!;

        Assert.DoesNotContain(typeof(ZabbixHost).GetProperties(), property => property.Name == "AdditionalFields");
        Assert.Equal("x", ((IZabbixAdditionalFields)host).AdditionalFields!["brand_new"].GetString());
        Assert.Contains("\"brand_new\":\"x\"", JsonSerializer.Serialize(host, ZabbixApiJson.Default.ZabbixHost), StringComparison.Ordinal);
        Assert.NotNull(typeof(ZabbixHostCreateRequest).GetProperty(nameof(ZabbixHostCreateRequest.AdditionalFields))!.SetMethod);
        Type[] readRecords = [.. typeof(ZabbixHost).Assembly.GetExportedTypes().Where(type => typeof(IZabbixAdditionalFields).IsAssignableFrom(type))];
        Assert.True(readRecords.Length >= 60, $"only {readRecords.Length} read records implement IZabbixAdditionalFields");
        Assert.All(readRecords, type => Assert.Null(type.GetProperty("AdditionalFields")));
    }

    /// <summary>
    /// The generator's output is analysed like hand-written code (<c>.editorconfig</c> scopes
    /// <c>generated_code = false</c> to it), and a design rule the Zabbix model answers is suppressed on the one symbol
    /// with its reason, never silently: an enum with no zero (CA1008), a single-code enum whose values look like bits
    /// (CA1027), an enum member named with Zabbix's type word (CA1720), a URL text member (CA1056). A record member
    /// named exactly a type (the wire field <c>object</c>) is renamed instead (<c>ObjectType</c>), the wire name kept.
    /// </summary>
    [Fact]
    public void Generated_code_is_analysed_and_every_suppression_carries_its_reason()
    {
        string editorconfig = File.ReadAllText(Path.Combine(Repository.Root, ".editorconfig")).ReplaceLineEndings("\n");
        Assert.Contains("[**/Zabbix.Generator/Community.Zabbix.Generator.*/**.g.cs]\ngenerated_code = false", editorconfig, StringComparison.Ordinal);
        ApiDescription description = ApiDescription.Read(File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "schema", "zabbix-api.json")));
        var emitter = new ZabbixApiEmitter(description);
        List<string> code = [.. emitter.Emit(ZabbixApiPart.Models, TestContext.Current.CancellationToken).Files.Concat(emitter.Emit(ZabbixApiPart.Clients, TestContext.Current.CancellationToken).Files).Select(file => file.Code)];

        List<string> suppressions = [.. code.SelectMany(text => text.Split('\n')).Where(line => line.Contains("SuppressMessage(", StringComparison.Ordinal))];
        Assert.Contains(suppressions, line => line.Contains("CA1008", StringComparison.Ordinal));
        Assert.Contains(suppressions, line => line.Contains("CA1027", StringComparison.Ordinal));
        Assert.Contains(suppressions, line => line.Contains("CA1720", StringComparison.Ordinal));
        Assert.Contains(suppressions, line => line.Contains("CA1056", StringComparison.Ordinal));
        Assert.All(suppressions, line => Assert.Matches("Justification = \"[^\"]{20,}", line));
        string[] typeWords = ["Object", "String", "Int32", "Single", "Float", "Unsigned", "Long", "Double", "Decimal", "Guid"];
        Assert.DoesNotContain(typeof(ZabbixEvent).Assembly.GetExportedTypes().Where(type => !type.IsEnum).SelectMany(type => type.GetProperties()), property => typeWords.Contains(property.Name));
        Assert.Equal("object", typeof(ZabbixEvent).GetProperty(nameof(ZabbixEvent.ObjectType))!.GetCustomAttribute<System.Text.Json.Serialization.JsonPropertyNameAttribute>()!.Name);
        Assert.NotNull(typeof(ZabbixDiscoveryKinds).GetCustomAttribute<FlagsAttribute>());
        Assert.Equal(0, (int)ZabbixDiscoveryKinds.None);
    }

    /// <summary>
    /// The documentation ratchet: a member without a sentence of its own in the description reads as a sentence made of
    /// its words (<c>DocSentences</c>), never the bare <c>Wire field x.</c> or <c>&lt;c&gt;DEFINE&lt;/c&gt;.</c>; and how many
    /// still lack one can only go down (the ceilings are the counts when the ratchet was set; lower them as the overlay
    /// gains sentences).
    /// </summary>
    [Fact]
    public void A_member_without_its_own_sentence_reads_as_one_and_their_count_only_goes_down()
    {
        ApiDescription api = ApiDescription.Read(File.ReadAllText(SchemaPath));
        int fields = api.Types.Sum(type => type.Fields.Count(field => DocSentences.IsFallback(field.Summary)));
        int members = api.Enums.Sum(e => e.Members.Count(member => DocSentences.IsFallback(member.Summary)));
        int enums = api.Enums.Count(e => DocSentences.IsFallback(e.Summary));
        List<string> code = [.. new ZabbixApiEmitter(api).Emit(ZabbixApiPart.Models, TestContext.Current.CancellationToken).Files.Select(file => file.Code)];

        Assert.True(fields <= FieldsWithoutASentence, $"{fields} fields lack a sentence of their own (ceiling {FieldsWithoutASentence})");
        Assert.True(members <= EnumMembersWithoutASentence, $"{members} enum members lack one (ceiling {EnumMembersWithoutASentence})");
        Assert.True(enums <= EnumsWithoutASentence, $"{enums} enums lack one (ceiling {EnumsWithoutASentence})");
        Assert.Empty(code.SelectMany(text => text.Split('\n')).Where(line => line.Contains("/// <summary>", StringComparison.Ordinal)
            && (line.Contains("<summary>Wire field", StringComparison.Ordinal) || line.Contains(". Wire field ", StringComparison.Ordinal)
                || System.Text.RegularExpressions.Regex.IsMatch(line, "<summary><c>[A-Z0-9_]+</c>\\.</summary>"))).Take(5));
        Assert.Equal("The password check rules (<c>passwd_check_rules</c>).", DocSentences.Field(null, "PasswordCheckRules", "passwd_check_rules"));
        Assert.Equal("The host ID (<c>hostid</c>).", DocSentences.Field("Hostid. Wire field hostid.", "HostId", "hostid"));
        Assert.Equal("TLS PSK (<c>HOST_ENCRYPTION_PSK</c>, 2 on the wire).", DocSentences.Member("<c>HOST_ENCRYPTION_PSK</c>.", "TlsPsk", "HOST_ENCRYPTION_PSK", 2));
    }

    private const int FieldsWithoutASentence = 0;

    private const int EnumMembersWithoutASentence = 720;

    private const int EnumsWithoutASentence = 144;

    /// <summary>
    /// CA2227 is answered for a family in one counted place: every public record with a settable <c>AdditionalFields</c>
    /// bag, and only those, is named in <c>ZabbixApiSuppressions.g.cs</c>; no record suppresses it on its own.
    /// </summary>
    [Fact]
    public void The_bag_suppression_is_one_counted_place_that_names_exactly_the_settable_bags()
    {
        var emitter = new ZabbixApiEmitter(ApiDescription.Read(File.ReadAllText(SchemaPath)));
        EquatableArray<GeneratedSource> models = emitter.Emit(ZabbixApiPart.Models, TestContext.Current.CancellationToken).Files;
        string place = models.Single(file => file.Name == "ZabbixApiSuppressions.g.cs").Code;
        string[] named = [.. System.Text.RegularExpressions.Regex.Matches(place, "~P:Community\\.Zabbix\\.(\\w+)\\.AdditionalFields").Select(match => match.Groups[1].Value).Order(StringComparer.Ordinal)];
        string[] settable = [.. typeof(ZabbixHost).Assembly.GetExportedTypes()
            .Where(type => type.GetProperty("AdditionalFields") is { CanWrite: true, SetMethod.IsPublic: true })
            .Select(type => type.Name).Order(StringComparer.Ordinal)];

        Assert.Equal(settable, named);
        Assert.Contains("// CA2227 on the AdditionalFields of " + settable.Length + " sendable records", place, StringComparison.Ordinal);
        Assert.DoesNotContain(models.Where(file => file.Name != "ZabbixApiSuppressions.g.cs"), file => file.Code.Contains("CA2227", StringComparison.Ordinal));
    }

    /// <summary>
    /// A set is plural, a choice is singular (decisions.md): every generated <c>[Flags]</c> enum is named in the plural,
    /// no single-code enum is, and the empty set is <c>None = 0</c> where the API accepts it.
    /// </summary>
    [Fact]
    public void A_flags_enum_is_plural_and_a_single_code_enum_is_not()
    {
        Type[] enums = [.. typeof(ZabbixHost).Assembly.GetExportedTypes().Where(type => type.IsEnum)];
        string[] flags = [.. enums.Where(type => type.GetCustomAttribute<FlagsAttribute>() is not null).Select(type => type.Name).Order(StringComparer.Ordinal)];
        string[] pluralChoices = [.. enums.Where(type => type.GetCustomAttribute<FlagsAttribute>() is null)
            .Select(type => type.Name).Where(name => name.EndsWith('s') && !name.EndsWith("ss", StringComparison.Ordinal) && !name.EndsWith("us", StringComparison.Ordinal))];

        Assert.Equal(["ZabbixDiscoveryKinds", "ZabbixEventActions", "ZabbixPasswordCheckRules", "ZabbixTlsAcceptModes"], flags);
        Assert.Empty(pluralChoices);
        Assert.Equal(0, (int)ZabbixPasswordCheckRules.None);
        Assert.Equal(0, (int)ZabbixEventActions.None);
        Assert.DoesNotContain("None", System.Enum.GetNames<ZabbixTlsAcceptModes>());
    }

    /// <summary>
    /// Every emitted record with a list, a dictionary, the bag or JSON among its members writes its own value
    /// equality, since the compiler's compares those by reference (the reflection sweep in
    /// <c>DesignConformanceTests.Equality.cs</c> proves the equality holds; this proves no emitted record goes without).
    /// </summary>
    [Fact]
    public void Every_emitted_record_with_a_collection_or_json_member_writes_its_own_equality()
    {
        var emitter = new ZabbixApiEmitter(ApiDescription.Read(File.ReadAllText(SchemaPath)));
        string[] shapes = ["global::System.Collections.Generic.IReadOnlyList<", "global::System.Collections.Generic.IReadOnlyDictionary<", "global::System.Collections.Generic.IDictionary<", "global::System.Text.Json.JsonElement"];

        EquatableArray<GeneratedSource> models = emitter.Emit(ZabbixApiPart.Models, TestContext.Current.CancellationToken).Files;
        List<string> records = [.. models.Where(file => file.Code.Contains("partial record", StringComparison.Ordinal))
            .Where(file => file.Code.Split('\n').Any(line => line.Contains("{ get;", StringComparison.Ordinal) && shapes.Any(shape => line.Contains(shape, StringComparison.Ordinal))))
            .Select(file => file.Name)];
        List<string> without = [.. models.Where(file => records.Contains(file.Name) && !file.Code.Contains("    public bool Equals(", StringComparison.Ordinal)).Select(file => file.Name)];

        Assert.True(records.Count > 300, $"{records.Count} records hold a collection or JSON member");
        Assert.Empty(without);
    }

    /// <summary>
    /// A map link's draw type, label display and indicator, and a map URL's element type read as the
    /// enums the map's own create validates them with (<c>CMap.php</c> 8.0:1115, 1866, 2092).
    /// </summary>
    [Fact]
    public void Map_links_and_urls_read_their_enums()
    {
        ZabbixMapLink link = JsonSerializer.Deserialize("""{"linkid":"1","drawtype":"4","show_label":"-1","indicator_type":"2"}""", ZabbixApiJson.Default.ZabbixMapLink)!;
        ZabbixMapUrl url = JsonSerializer.Deserialize("""{"sysmapurlid":"1","elementtype":"3"}""", ZabbixApiJson.Default.ZabbixMapUrl)!;

        Assert.Equal(ZabbixDrawType.DashedLine, link.DrawType);
        Assert.Equal(ZabbixMapShowElementLabel.Default, link.ShowLabel);
        Assert.Equal(ZabbixMapIndicatorType.ItemValue, link.IndicatorType);
        Assert.Equal(ZabbixMapElementType.HostGroup, url.ElementType);
    }

    /// <summary>
    /// host.create's and host.massadd's interfaces carry the interface enums
    /// <c>hostinterface.create</c> has (<c>INTERFACE_TYPE_*</c>, <c>INTERFACE_USE_*</c>, <c>INTERFACE_PRIMARY/SECONDARY</c>),
    /// from record-path <c>fieldEnums</c> keys, so a host macro's <c>type</c> stays its own enum.
    /// </summary>
    [Fact]
    public void Host_interfaces_are_typed_by_the_interface_enums_and_host_macros_keep_theirs()
    {
        var request = new ZabbixHostInterfaceRequest { Type = ZabbixInterfaceType.Snmp, UseIp = ZabbixHostInterfaceUseIp.Dns, Main = ZabbixInterfacePriority.Primary };

        Assert.Equal("""{"type":2,"useip":0,"main":1}""", JsonSerializer.Serialize(request, ZabbixApiJson.Default.ZabbixHostInterfaceRequest));
        Assert.Equal(ZabbixHostInterfaceUseIp.Ip, JsonSerializer.Deserialize("""{"useip":"Ip"}""", ZabbixApiJson.Default.ZabbixHostInterfaceRequest)!.UseIp);
        Assert.Equal(typeof(ZabbixInterfaceType?), typeof(ZabbixHostMassAddInterfaceRequest).GetProperty(nameof(ZabbixHostMassAddInterfaceRequest.Type))!.PropertyType);
        Assert.Equal(typeof(ZabbixInterfacePriority), typeof(ZabbixHostInterfaceCreateRequest).GetProperty(nameof(ZabbixHostInterfaceCreateRequest.Main))!.PropertyType);
        Assert.Equal(typeof(ZabbixMacroType?), typeof(ZabbixHostMacroRequest).GetProperty(nameof(ZabbixHostMacroRequest.Type))!.PropertyType);
    }

    /// <summary>
    /// <c>history.push</c> answers <c>{response, data: [{itemid?, error?}]}</c>, one row per value in
    /// order (CHistory.php 8.0:459, trapper_history_push.c 8.0:465-545), the item id a JSON number (as an 8.0.0rc1 server sends it).
    /// </summary>
    [Fact]
    public async Task History_push_answers_one_typed_row_per_value_and_reads_a_numeric_item_id()
    {
        using ScriptedApi api = new ScriptedApi().On("history.push", """{"response":"success","data":[{"itemid":73184},{"itemid":73184,"error":"Client IP is not in item's allowed hosts list."},{"error":"No permissions to referred object or it does not exist."}]}""");
        (ZabbixClient client, _) = Build(api);

        ZabbixHistoryPushResult result = await client.History.PushAsync([new ZabbixHistoryPushRequest { ItemId = 73184, Value = JsonSerializer.SerializeToElement(1) }], TestContext.Current.CancellationToken);

        Assert.Equal("success", result.Response);
        Assert.Equal<ulong?>([73184, 73184, null], result.Values!.Select(row => row.ItemId));
        Assert.Equal([null, "Client IP is not in item's allowed hosts list.", "No permissions to referred object or it does not exist."], result.Values!.Select(row => row.Error));
        Assert.Throws<JsonException>(() => JsonSerializer.Deserialize("""{"itemid":-1}""", ZabbixApiJson.Default.ZabbixHistoryPushResultValue));
    }

    /// <summary>
    /// An action's filter and operations, a correlation's and a discovery rule's
    /// filter read typed (CAction::addRelatedObjects, 8.0:1501-1840), not as untyped JSON.
    /// </summary>
    [Fact]
    public void An_action_s_filter_and_operations_read_typed()
    {
        ZabbixAction action = JsonSerializer.Deserialize("""
            {"actionid":"3","filter":{"evaltype":"0","formula":"","eval_formula":"A","conditions":[{"conditiontype":"4","operator":"5","value":"2","value2":"","formulaid":"A"}]},
             "operations":[{"operationid":"7","actionid":"3","operationtype":"0","esc_period":"0","esc_step_from":"1","esc_step_to":"1","evaltype":"0",
               "opmessage":{"default_msg":"1","subject":"","message":"","mediatypeid":"0"},"opmessage_grp":[{"usrgrpid":"7"}]}]}
            """, ZabbixApiJson.Default.ZabbixAction)!;

        Assert.Equal("A", Assert.Single(action.Filter!.Conditions!).FormulaId);
        Assert.Equal(ZabbixConditionOperator.MoreEqual, action.Filter.Conditions![0].Operator);
        ZabbixActionReadOperation operation = Assert.Single(action.Operations!);
        Assert.Equal(7UL, Assert.Single(operation.OperationMessageGroup!).UserGroupId);
        Assert.Equal(typeof(ZabbixLldReadFilter), typeof(ZabbixDiscoveryRule).GetProperty(nameof(ZabbixDiscoveryRule.Filter))!.PropertyType);
        Assert.Equal(typeof(ZabbixCorrelationReadFilter), typeof(ZabbixCorrelationRecord).GetProperty(nameof(ZabbixCorrelationRecord.Filter))!.PropertyType);
    }

    [Fact]
    public async Task The_api_and_its_clients_can_be_substituted_where_a_caller_takes_them()
    {
        ZabbixApi api = new FakeApi();

        IReadOnlyList<ZabbixHost> hosts = await api.Hosts.GetAsync(cancellationToken: TestContext.Current.CancellationToken);

        Assert.Equal("web-01", Assert.Single(hosts).TechnicalName);
        Assert.False(typeof(ZabbixApi).IsSealed);
        Assert.All(typeof(ZabbixApi).GetProperties().Where(p => p.PropertyType.Name.EndsWith("Client", StringComparison.Ordinal)), p => Assert.True(p.GetMethod!.IsVirtual, p.Name));
        Assert.True(typeof(ZabbixApi).GetConstructor(BindingFlags.Instance | BindingFlags.NonPublic, System.Type.EmptyTypes)?.IsFamily);
        Assert.All(typeof(ZabbixApi).GetProperties().Select(p => p.PropertyType).Where(t => t.Name.EndsWith("Client", StringComparison.Ordinal)),
            client => Assert.True(!client.IsSealed && client.GetConstructor(BindingFlags.Instance | BindingFlags.NonPublic, System.Type.EmptyTypes)?.IsFamily == true, client.Name));
    }

    private sealed class FakeHosts : ZabbixHostClient
    {
        public override Task<IReadOnlyList<ZabbixHost>> GetAsync(ZabbixHostGetParameters parameters, CancellationToken cancellationToken)
            => Task.FromResult<IReadOnlyList<ZabbixHost>>([new ZabbixHost { HostId = 1, TechnicalName = "web-01" }]);
    }

    private sealed class FakeApi : ZabbixApi
    {
        public override ZabbixHostClient Hosts { get; } = new FakeHosts();
    }

    private sealed class InMemoryText(string path, string text) : AdditionalText
    {
        public override string Path => path;

        public override SourceText GetText(CancellationToken cancellationToken = default) => SourceText.From(text);
    }

    private sealed class PartOptions(string part) : AnalyzerConfigOptionsProvider
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
