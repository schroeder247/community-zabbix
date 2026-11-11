// This Source Code Form is subject to the terms of the Mozilla Public
// License, v. 2.0. Copyright (c) 2026 schroeder247. SPDX-License-Identifier: MPL-2.0
using System.Text.Json;
using Microsoft.Extensions.DependencyInjection;

namespace Community.Zabbix.Tests;

/// <summary>
/// The API client against replies a server should not send: the compositions and the tree read a reply's shape before
/// they read it, so a malformed reply is the server's fault (<see cref="InvalidDataException"/>, error.type
/// invalid_response), never a <see cref="KeyNotFoundException"/> (the base of <see cref="ZabbixNotFoundException"/>) and
/// never a wrong answer.
/// </summary>
public sealed class AdversarialApiTests
{
    private const long S = 1_700_000_100;

    private static (ZabbixClient Server, ServiceProvider Provider) Build(ScriptedApi api)
    {
        var services = new ServiceCollection();
        services.AddZabbix(options => { options.Endpoint = new Uri("https://zbx.example/api_jsonrpc.php"); options.ApiToken = "tok"; });
        services.AddHttpClient(ZabbixHttp.GetClientName()).ConfigurePrimaryHttpMessageHandler(() => api);
        ServiceProvider provider = services.BuildValidated();
        return (provider.GetRequiredService<ZabbixClient>(), provider);
    }

    [Theory]
    [InlineData("""[{"hostid":"1"}]""")]                                                         // no macros, though they were selected
    [InlineData("""[{"hostid":"1","macros":{"macro":"{$A}"}}]""")]                                // macros that are not a list
    [InlineData("""[{"hostid":"1","macros":[{"macro":5,"hostmacroid":"9"}]}]""")]                 // a macro name that is not text
    [InlineData("""{"hostid":"1"}""")]                                                            // a reply that is not rows
    public async Task A_malformed_macro_read_in_the_8_0_host_mass_update_is_invalid_data_not_not_found(string reply)
    {
        using ScriptedApi api = new ScriptedApi().On("apiinfo.version", "\"8.0.0\"").On("host.get", reply).On("host.update", """{"hostids":["1"]}""");
        (ZabbixClient client, ServiceProvider provider) = Build(api);
        using ServiceProvider owned = provider;

        Exception failure = await Assert.ThrowsAnyAsync<Exception>(() => client.Hosts.MassUpdateAsync(new ZabbixHostMassUpdateRequest
        {
            Hosts = [new ZabbixHostMassHostRequest { HostId = 1 }],
            Macros = [new ZabbixHostMassMacroRequest { Macro = "{$A}", Value = "1" }],
        }, TestContext.Current.CancellationToken));

        Assert.IsType<InvalidDataException>(failure);
        Assert.Empty(api.ParamsOf("host.update"));                                               // nothing is written from a reply it could not read
    }

    [Fact]
    public async Task A_replaced_interface_read_back_without_its_id_is_invalid_data_not_an_empty_id()
    {
        using ScriptedApi api = new ScriptedApi().On("apiinfo.version", "\"8.0.0\"").On("host.update", """{"hostids":["10"]}""")
            .On("hostinterface.get", """[{"interfaceid":"31"},{"type":"1"}]""");
        (ZabbixClient client, ServiceProvider provider) = Build(api);
        using ServiceProvider owned = provider;

        await Assert.ThrowsAsync<InvalidDataException>(() => client.HostInterfaces.ReplaceHostInterfacesAsync(new ZabbixHostInterfaceReplaceHostInterfacesRequest
        {
            HostId = 10,
            Interfaces = [new ZabbixHostInterfaceReplaceHostInterfacesInterfaceRequest { Type = ZabbixInterfaceType.Agent, Ip = "127.0.0.1", Dns = "", Port = "10050" }],
        }, TestContext.Current.CancellationToken));
    }

    [Theory]
    [InlineData("""[{"itemid":"99","clock":"1700000100","ns":"0","value":"1"}]""")]              // an item the read did not ask about
    [InlineData("""[{"itemid":"1","clock":"1700000100","ns":"0"}]""")]                            // no value
    public async Task A_history_row_the_read_did_not_ask_for_is_invalid_data(string rows)
    {
        const string Items = """[{"itemid":"1","hostid":"10","key_":"cpu","name":"CPU","delay":"30s","value_type":"0","lastvalue":"1","lastclock":"1700000000","lastns":"0","hosts":[{"hostid":"10","host":"web-01"}]}]""";
        using ScriptedApi api = new ScriptedApi().On("apiinfo.version", "\"7.0.0\"").On("item.get", Items)
            .On("history.get", parameters => parameters.GetProperty("time_from").GetInt64() <= S ? rows : "[]");
        (ZabbixClient client, ServiceProvider provider) = Build(api);
        using ServiceProvider owned = provider;

        await Assert.ThrowsAsync<InvalidDataException>(async () => await client.Hosts["web-01"].GetHistoryAsync(DateTimeOffset.FromUnixTimeSeconds(S), cancellationToken: TestContext.Current.CancellationToken)
            .ToListAsync(cancellationToken: TestContext.Current.CancellationToken));
    }

    public static TheoryData<Type> ModelEnums()
    {
        var rows = new TheoryData<Type>();
        foreach (Type type in typeof(ZabbixHost).Assembly.GetExportedTypes().Where(type => type.IsEnum && type.GetCustomAttributes(typeof(System.Text.Json.Serialization.JsonConverterAttribute), false).Length > 0))
            rows.Add(type);
        return rows;
    }

    // A server newer than the model sends a code the model does not name: it is kept as the number and written back as
    // the same number, never refused and never mapped to another member; a number past the enum's range is refused.
    [Theory]
    [MemberData(nameof(ModelEnums))]
    public void Every_model_enum_round_trips_a_value_it_does_not_name(Type type)
    {
        var options = new JsonSerializerOptions { TypeInfoResolver = new System.Text.Json.Serialization.Metadata.DefaultJsonTypeInfoResolver() };
        object fromText = JsonSerializer.Deserialize("\"97\"", type, options)!;
        object fromNumber = JsonSerializer.Deserialize("97", type, options)!;

        Assert.Equal(97L, Convert.ToInt64(fromText, System.Globalization.CultureInfo.InvariantCulture));
        Assert.Equal(fromText, fromNumber);
        Assert.Equal("97", JsonSerializer.Serialize(fromText, type, options));
        Assert.Throws<JsonException>(() => JsonSerializer.Deserialize("99999999999999999999", type, options));
        if (Enum.GetUnderlyingType(type) == typeof(int))
            Assert.Throws<JsonException>(() => JsonSerializer.Deserialize("4294967296", type, options));     // past int: refused, not truncated to 0
    }

    [Theory]
    [InlineData("x")]
    [InlineData("N")]
    [InlineData("D9")]
    public void A_format_the_sdk_s_values_do_not_know_is_a_format_exception(string format)
    {
        IFormattable[] values = [new ZabbixClock(1, 2), ZabbixComponentVersion.Parse("7.0.1", null), ZabbixItemKey.Parse("agent.ping", null), new ZabbixServerNode("zbx", 10051)];

        Assert.All(values, value => Assert.Throws<FormatException>(() => value.ToString(format, null)));
        Assert.All(values, value => Assert.Equal(value.ToString(), value.ToString("G", null)));
        Assert.All(values, value => Assert.Equal(value.ToString(), $"{value}"));
    }

    [Fact]
    public void A_request_record_read_without_a_required_member_is_a_json_failure()
        => Assert.Throws<JsonException>(() => JsonSerializer.Deserialize("""{"macro":"{$A}"}""", ZabbixApiJson.Default.ZabbixHostMassMacroRequest));

    [Fact]
    public void An_optional_member_writes_null_when_asked_and_nothing_when_absent()
    {
        string absent = JsonSerializer.Serialize(new ZabbixHistoryGetParameters(), ZabbixJson.Options);
        string asNull = JsonSerializer.Serialize(new ZabbixHistoryGetParameters { MaxValueSize = ZabbixOptional<int>.Null }, ZabbixJson.Options);
        string valued = JsonSerializer.Serialize(new ZabbixHistoryGetParameters { MaxValueSize = 10 }, ZabbixJson.Options);

        Assert.DoesNotContain("maxValueSize", absent, StringComparison.Ordinal);
        Assert.Contains("\"maxValueSize\":null", asNull, StringComparison.Ordinal);
        Assert.Contains("\"maxValueSize\":10", valued, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("""[{"hostid":10084}]""", "10084")]
    [InlineData("""[{"hostid":"10084"}]""", "10084")]
    public void An_id_the_server_writes_as_a_number_or_as_text_reads_as_the_same_id(string reply, string id)
        => Assert.Equal(ulong.Parse(id, null), Assert.Single(JsonSerializer.Deserialize(reply, ZabbixApiJson.Default.IReadOnlyListZabbixHost)!).HostId);

    [Theory]
    [InlineData("""[{"hostid":-1}]""")]
    [InlineData("""[{"hostid":1.5}]""")]
    [InlineData("""[{"hostid":true}]""")]
    [InlineData("""[{"hostid":{}}]""")]
    public void An_id_that_is_no_id_is_a_json_failure_not_a_wrong_id(string reply)
        => Assert.Throws<JsonException>(() => JsonSerializer.Deserialize(reply, ZabbixApiJson.Default.IReadOnlyListZabbixHost));

    [Fact]
    public void Extension_data_that_names_a_modelled_field_is_refused_when_the_request_is_written()
    {
        var request = new ZabbixHostMassUpdateRequest
        {
            Hosts = [new ZabbixHostMassHostRequest { HostId = 1 }],
            AdditionalFields = new Dictionary<string, JsonElement> { ["hosts"] = JsonElement.Parse("[]") },
        };

        Assert.Throws<ArgumentException>(() => JsonSerializer.Serialize(request, ZabbixJson.Options));
    }

    // A record is only as immutable as the lists it is given: its equality compares their contents, so a list the caller
    // keeps and changes changes what the record equals and its hash (docs/KNOWN-LIMITATIONS.md).
    [Fact]
    public void A_record_compares_the_lists_it_holds_by_their_contents_now_so_a_shared_list_changes_it()
    {
        var groups = new List<ZabbixHostGroup> { new() { GroupId = 2 } };
        var held = new ZabbixHost { HostId = 1, HostGroups = groups };
        var copied = new ZabbixHost { HostId = 1, HostGroups = [new ZabbixHostGroup { GroupId = 2 }] };
        int before = held.GetHashCode();

        Assert.Equal(copied, held);
        groups.Add(new ZabbixHostGroup { GroupId = 3 });

        Assert.NotEqual(copied, held);
        Assert.NotEqual(before, held.GetHashCode());
    }

    [Fact]
    public void An_output_list_holding_a_number_is_a_json_failure_not_an_accessor_s()
        => Assert.Throws<JsonException>(() => JsonSerializer.Deserialize("""{"output":["hostid",5]}""", ZabbixApiJson.Default.ZabbixHostGetParameters));
}
