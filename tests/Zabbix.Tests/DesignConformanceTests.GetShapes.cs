// This Source Code Form is subject to the terms of the Mozilla Public
// License, v. 2.0. Copyright (c) 2026 schroeder247. SPDX-License-Identifier: MPL-2.0
using System.Reflection;
using System.Text.Json;
using System.Text.Json.Serialization;
using Community.Zabbix.Tests.Conformance;

namespace Community.Zabbix.Tests;

/// <summary>
/// A typed get reads its reply as its records (docs/adr/0013-builders-verbs-and-typed-values.md, section 4), so no get's
/// parameters change the shape of the reply: <c>countOutput</c> (a count), <c>groupCount</c> and <c>groupBy</c> (rows of
/// counts) and <c>preservekeys</c> (the records keyed by id) are no public member of a get's parameters, their bag refuses
/// them, and an output has no <c>count</c> keyword (a select's field as a number). <c>CountAsync</c> counts, adding
/// <c>countOutput</c> to the parameters it sends.
/// </summary>
public sealed partial class DesignConformanceTests
{
    private static readonly string[] ReplyShapes = ["countOutput", "groupCount", "groupBy", "preservekeys"];

    [Fact]
    public void No_get_parameters_change_the_shape_of_the_reply()
    {
        List<Type> gets = [.. Libraries.PublicTypes.Where(type => type.Name.EndsWith("GetParameters", StringComparison.Ordinal) && !type.IsInterface)];
        List<string> offenders = [.. gets.SelectMany(type => type.GetProperties(BindingFlags.Public | BindingFlags.Instance)
            .Where(property => property.Name is "CountOutput" or "GroupCount" or "GroupBy" or "PreserveKeys"
                || (property.GetCustomAttribute<JsonPropertyNameAttribute>() is { Name: var wire } && ReplyShapes.Contains(wire, StringComparer.Ordinal)))
            .Select(property => $"{type.Name}.{property.Name}"))];

        Assert.True(gets.Count > 60, $"only {gets.Count} get parameter types found");
        Assert.All(gets, type => Assert.Null(type.GetProperty("CountOutput", BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance)));
        Assert.DoesNotContain(typeof(IZabbixGetParameters).GetProperties(), property => property.Name == "CountOutput");
        Assert.DoesNotContain(typeof(ZabbixOutput).GetProperties(BindingFlags.Public | BindingFlags.Static), property => property.Name == "Count");
        AssertNone(offenders);
    }

    [Theory]
    [InlineData("countOutput")]
    [InlineData("groupCount")]
    [InlineData("groupBy")]
    [InlineData("preservekeys")]
    public void A_get_s_bag_refuses_what_would_change_the_shape_of_the_reply(string wire)
    {
        var parameters = new ZabbixHostGetParameters { AdditionalFields = new Dictionary<string, JsonElement> { [wire] = JsonSerializer.SerializeToElement(true, ZabbixApiJson.Default.Boolean) } };

        ZabbixHostGetParameters bound = JsonSerializer.Deserialize($$"""{"{{wire}}":true}""", ZabbixApiJson.Default.ZabbixHostGetParameters)!;   // a shell's hashtable binds so

        ArgumentException refused = Assert.Throws<ArgumentException>(() => JsonSerializer.Serialize(parameters, ZabbixApiJson.Default.ZabbixHostGetParameters));
        ArgumentException alsoRefused = Assert.Throws<ArgumentException>(() => JsonSerializer.Serialize(bound, ZabbixApiJson.Default.ZabbixHostGetParameters));

        Assert.Contains($"'{wire}' changes the shape of the reply", refused.Message, StringComparison.Ordinal);
        Assert.Contains($"'{wire}' changes the shape of the reply", alsoRefused.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void A_select_s_count_keyword_is_refused_where_json_spells_it()
    {
        JsonException refused = Assert.Throws<JsonException>(() => JsonSerializer.Deserialize("""{"selectItems":"count"}""", ZabbixApiJson.Default.ZabbixHostGetParameters));

        Assert.Contains("CountAsync", refused.Message, StringComparison.Ordinal);
        Assert.Equal("extend", JsonSerializer.Deserialize("""{"selectItems":"extend"}""", ZabbixApiJson.Default.ZabbixHostGetParameters)!.SelectItems!.Keyword);
    }
}
