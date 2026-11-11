// This Source Code Form is subject to the terms of the Mozilla Public
// License, v. 2.0. Copyright (c) 2026 schroeder247. SPDX-License-Identifier: MPL-2.0
using System.Text.Json;
using System.Text.Json.Serialization;

namespace Community.Zabbix.Tests;

/// <summary>
/// The JSON dialect's values and converters: equal when their JSON is, strict where PHP's looseness would otherwise be
/// read as something else, and the platform converters the owned ones were measured against.
/// </summary>
public sealed class ZabbixJsonTests
{
    [Fact]
    public void Outputs_are_equal_when_their_keyword_or_fields_are()
    {
        ZabbixOutput<ZabbixHost> both = [ZabbixHostFields.HostId, ZabbixHostFields.VisibleName];

        Assert.Equal(both, ZabbixOutput.Of(ZabbixHostFields.HostId, ZabbixHostFields.VisibleName));
        Assert.Equal(both.GetHashCode(), ZabbixOutput.Of(ZabbixHostFields.HostId, ZabbixHostFields.VisibleName).GetHashCode());
        Assert.Equal(ZabbixOutput<ZabbixHost>.FromZabbixOutput(ZabbixOutput.Extend), (ZabbixOutput<ZabbixHost>)ZabbixOutput.Extend);
        Assert.NotEqual(both, ZabbixOutput.Of(ZabbixHostFields.VisibleName, ZabbixHostFields.HostId));
        Assert.NotEqual((ZabbixOutput<ZabbixHost>)ZabbixOutput.Count, ZabbixOutput.Of(new ZabbixField<ZabbixHost>("count")));
    }

    [Fact]
    public void A_collection_expression_is_an_output_of_its_fields()
    {
        ZabbixOutput<ZabbixHost> output = [ZabbixHostFields.HostId, ZabbixHostFields.VisibleName];
        var parameters = new ZabbixHostGetParameters { Output = [ZabbixHostFields.HostId] };

        Assert.Equal("""{"output":["hostid","name"]}""", JsonSerializer.Serialize(new ZabbixHostGetParameters { Output = output }, ZabbixApiJson.Default.ZabbixHostGetParameters));
        Assert.Equal(ZabbixOutput.Of(ZabbixHostFields.HostId), parameters.Output);
        ZabbixField<ZabbixHost>[] fields = [.. output];
        ZabbixField<ZabbixHost>[] none = [.. (ZabbixOutput<ZabbixHost>)ZabbixOutput.Extend];
        Assert.Equal([ZabbixHostFields.HostId, ZabbixHostFields.VisibleName], fields);
        Assert.Empty(none);
    }

    [Fact]
    public void A_sort_direction_is_written_and_read_as_the_api_words()
    {
        var parameters = new ZabbixHostGetParameters { Sort = [ZabbixHostFields.HostId, ZabbixHostFields.VisibleName.Descending()] };

        string json = JsonSerializer.Serialize(parameters, ZabbixApiJson.Default.ZabbixHostGetParameters);
        ZabbixHostGetParameters read = JsonSerializer.Deserialize("""{"sortfield":["name","hostid"],"sortorder":["desc","ASC"]}""", ZabbixApiJson.Default.ZabbixHostGetParameters)!;

        Assert.Equal("""{"sortfield":["hostid","name"],"sortorder":["ASC","DESC"]}""", json);
        Assert.Equal([ZabbixHostFields.VisibleName.Descending(), ZabbixHostFields.HostId.Ascending()], read.Sort!);
        Assert.Throws<JsonException>(() => JsonSerializer.Deserialize("""{"sortfield":["name"],"sortorder":["UP"]}""", ZabbixApiJson.Default.ZabbixHostGetParameters));
    }

    [Fact]
    public void Field_values_are_equal_when_their_json_is()
    {
        ZabbixFieldValue read = JsonSerializer.Deserialize("""["a","b"]""", ZabbixApiJson.Default.ZabbixFieldValue);

        Assert.Equal((ZabbixFieldValue)new[] { "a", "b" }, read);
        Assert.Equal(((ZabbixFieldValue)new[] { "a", "b" }).GetHashCode(), read.GetHashCode());
        Assert.Equal((ZabbixFieldValue)1L, JsonSerializer.Deserialize("1.0", ZabbixApiJson.Default.ZabbixFieldValue));
        Assert.Equal(((ZabbixFieldValue)"x").GetHashCode(), JsonSerializer.Deserialize("\"x\"", ZabbixApiJson.Default.ZabbixFieldValue).GetHashCode());
        Assert.NotEqual((ZabbixFieldValue)"1", (ZabbixFieldValue)1L);
        Assert.NotEqual((ZabbixFieldValue)new[] { "a", "b" }, (ZabbixFieldValue)new[] { "b", "a" });
        Assert.Equal(default, default(ZabbixFieldValue));
        Assert.NotEqual(default, (ZabbixFieldValue)"x");
    }

    [Fact]
    public void An_empty_array_is_an_absent_object_and_a_non_empty_array_is_an_error()
    {
        EmptyObjectRow empty = JsonSerializer.Deserialize("""{"host":[]}""", TestJson.Default.EmptyObjectRow)!;
        EmptyObjectRow present = JsonSerializer.Deserialize("""{"host":{"hostid":"7"}}""", TestJson.Default.EmptyObjectRow)!;

        Assert.Null(empty.Host);
        Assert.Equal("7", present.Host!.HostId);
        Assert.Throws<JsonException>(() => JsonSerializer.Deserialize("""{"host":[{"hostid":"7"}]}""", TestJson.Default.EmptyObjectRow));
    }

    [Fact]
    public void A_connector_status_is_its_own_enum_from_the_defines_so_zero_reads_disabled_and_an_unknown_value_stays_itself()
    {
        // ZBX_CONNECTOR_STATUS_DISABLED = 0, _ENABLED = 1 (defines.inc.php): no inversion, no converter that maps
        // "anything else" to a member.
        Assert.Equal(ZabbixConnectorStatus.Enabled, JsonSerializer.Deserialize("""{"connectorid":"1","status":"1"}""", ZabbixApiJson.Default.ZabbixConnector)!.Status);
        Assert.Equal(ZabbixConnectorStatus.Disabled, JsonSerializer.Deserialize("""{"connectorid":"1","status":"0"}""", ZabbixApiJson.Default.ZabbixConnector)!.Status);
        Assert.Equal((ZabbixConnectorStatus)5, JsonSerializer.Deserialize("""{"connectorid":"1","status":"5"}""", ZabbixApiJson.Default.ZabbixConnector)!.Status);
        Assert.Equal("""{"connectorid":"1","status":0}""", JsonSerializer.Serialize(new ZabbixConnector { ConnectorId = 1, Status = ZabbixConnectorStatus.Disabled }, ZabbixApiJson.Default.ZabbixConnector));
    }

    [Fact]
    public void The_platform_enum_converters_cannot_speak_the_api_s_numeric_text_measured()
    {
        // The rejection recorded on ZabbixEnumConverter: read "3", write 3. Neither platform converter does both.
        var number = new JsonSerializerOptions { TypeInfoResolver = TestJson.Default, NumberHandling = JsonNumberHandling.AllowReadingFromString, Converters = { new JsonNumberEnumConverter<DayOfWeek>() } };
        var text = new JsonSerializerOptions { TypeInfoResolver = TestJson.Default, Converters = { new JsonStringEnumConverter<DayOfWeek>(allowIntegerValues: true) } };

        Assert.Throws<JsonException>(() => JsonSerializer.Deserialize("\"3\"", number.GetTypeInfo(typeof(DayOfWeek))));
        Assert.Equal(DayOfWeek.Wednesday, JsonSerializer.Deserialize("\"3\"", text.GetTypeInfo(typeof(DayOfWeek))));
        Assert.Equal("\"Wednesday\"", JsonSerializer.Serialize(DayOfWeek.Wednesday, text.GetTypeInfo(typeof(DayOfWeek))));
        Assert.Equal(ZabbixItemValueType.Unsigned, JsonSerializer.Deserialize("\"3\"", ZabbixApiJson.Default.ZabbixItemValueType));
        Assert.Equal("3", JsonSerializer.Serialize(ZabbixItemValueType.Unsigned, ZabbixApiJson.Default.ZabbixItemValueType));
    }

    /// <summary>Locks the value already true of every generated enum (<see cref="ZabbixEnumConverter{TEnum}"/>) — the wire is PHP's, not .NET's.</summary>
    [Fact]
    public void An_enum_goes_out_as_a_json_number()
        => Assert.Equal("3", JsonSerializer.Serialize(ZabbixItemValueType.Unsigned, ZabbixApiJson.Default.ZabbixItemValueType));

    /// <summary>
    /// A code the model does not name (a newer server, a version this build predates) is kept, not rejected — the
    /// value survives a round trip so a caller can still see the number even when no member names it.
    /// </summary>
    [Fact]
    public void An_unknown_enum_code_survives_a_read()
    {
        var unknown = (ZabbixItemValueType)99;

        Assert.Equal(unknown, JsonSerializer.Deserialize("99", ZabbixApiJson.Default.ZabbixItemValueType));
        Assert.False(Enum.IsDefined(unknown));
    }
}
