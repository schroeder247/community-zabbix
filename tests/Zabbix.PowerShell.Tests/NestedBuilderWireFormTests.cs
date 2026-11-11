// This Source Code Form is subject to the terms of the Mozilla Public
// License, v. 2.0. Copyright (c) 2026 schroeder247. SPDX-License-Identifier: MPL-2.0
using System.Collections;
using System.Management.Automation;
using System.Numerics;
using System.Reflection;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.Json.Serialization.Metadata;

namespace Community.Zabbix.PowerShell.Tests;

/// <summary>
/// The nested-record builders (<c>New-ZbxActionCondition</c>, <c>New-ZbxMapLink</c>, ...) write every value in its
/// <b>wire</b> form: an enum as the number the JSON contract writes, an id as its
/// string, a nested record as a wire-keyed hashtable. So the hashtable binds onto its owner's record, a script's
/// <c>[string]$c.conditiontype</c> is <c>'0'</c>, and a raw <c>Invoke-ZbxApi</c> body built from it is what the API
/// accepts. Also the value forms the builders normalize: LLD macro names, named map colors and
/// widget types.
/// </summary>
public sealed class NestedBuilderWireFormTests
{
    private const string Generated = "Community.Zabbix.PowerShell.Commands.Generated";

    /// <summary>Every generated builder, with the record it is modelled on (its <c>Contract</c>).</summary>
    public static TheoryData<string> Builders()
    {
        var data = new TheoryData<string>();
        foreach (Type type in BuilderTypes()) data.Add(type.Name);
        return data;
    }

    private static IEnumerable<Type> BuilderTypes()
        => typeof(ZabbixCmdlet).Assembly.GetTypes()
            .Where(t => t.Namespace == Generated && t.GetProperty("Contract", BindingFlags.NonPublic | BindingFlags.Static) is not null)
            .OrderBy(t => t.Name, StringComparer.Ordinal);

    [Fact]
    public void Every_generated_builder_names_the_record_it_writes()
        => Assert.Equal(18, BuilderTypes().Count());

    [Theory]
    [MemberData(nameof(Builders))]
    public void A_builder_given_every_parameter_writes_only_wire_values_that_bind_onto_its_record_and_round_trip_to_the_same_json(string builder)
    {
        Type type = BuilderTypes().Single(t => t.Name == builder);
        var contract = (JsonTypeInfo)type.GetProperty("Contract", BindingFlags.NonPublic | BindingFlags.Static)!.GetValue(null)!;
        CmdletAttribute cmdlet = type.GetCustomAttribute<CmdletAttribute>()!;
        List<PropertyInfo> parameters = [.. type.GetProperties().Where(p => p.DeclaringType == type && p.GetCustomAttribute<ParameterAttribute>() is not null)];
        using Shell shell = Shell.Open();
        using var powershell = System.Management.Automation.PowerShell.Create(shell.Runspace);
        powershell.AddCommand(cmdlet.VerbName + "-" + cmdlet.NounName);
        foreach (PropertyInfo parameter in parameters) powershell.AddParameter(parameter.Name, Sample(parameter.PropertyType, Member(contract, parameter.Name)));

        var table = Assert.IsType<Hashtable>(Assert.Single(Shell.Invoke(powershell, builder)).BaseObject);

        Assert.Empty(powershell.Streams.Error);
        // Every bound parameter is present, under its wire name; a list member's parameter is its singular (Highlights:
        // -Highlight, Dependencies: -Dependency).
        HashSet<string> names = [.. parameters.Select(p => p.Name)];
        static string Singular(string member) => member.EndsWith("ies", StringComparison.Ordinal) ? member[..^3] + "y" : member.EndsWith('s') && !member.EndsWith("ss", StringComparison.Ordinal) && !member.EndsWith("us", StringComparison.Ordinal) ? member[..^1] : member;
        string[] expected = [.. contract.Properties.Where(p => p.AttributeProvider is MemberInfo m && (names.Contains(m.Name) || names.Contains(Singular(m.Name)))).Select(p => p.Name).Order(StringComparer.Ordinal)];
        Assert.Equal(expected, table.Keys.Cast<string>().Order(StringComparer.Ordinal));
        AssertWireForm(table, builder);
        // The owner binds it (the PSTypeConverter the types.ps1xml registers), and the record writes the same JSON.
        object record = new ZabbixRecordConverter().ConvertFrom(table, contract.Type, System.Globalization.CultureInfo.InvariantCulture, true)!;
        JsonNode? fromRecord = JsonSerializer.SerializeToNode(record, contract);
        JsonNode? fromTable = JsonNode.Parse(ZabbixJsonShape.FromShell(table).GetRawText());
        Assert.True(JsonNode.DeepEquals(fromRecord, fromTable), builder + ": " + fromRecord?.ToJsonString() + " != " + fromTable?.ToJsonString());
    }

    [Fact]
    public void An_enum_parameter_is_stored_as_the_number_the_wire_carries()
    {
        // Holding the CLR member HostGroup in $c.conditiontype would make a stringified raw-API body send "HostGroup",
        // and action.create answers "an integer is expected".
        using Shell shell = Shell.Open();

        Run run = shell.Invoke("$c = New-ZbxActionCondition -ConditionType HostGroup -Operator 0 -Value 7; [string]$c.conditiontype; [string]$c.operator; $c.conditiontype -eq 0");

        Assert.Empty(run.Errors);
        Assert.Equal(["0", "0", "True"], run.Output.Select(o => o.ToString()));
    }

    [Fact]
    public async Task A_builder_given_an_id_binds_onto_its_owner_record()
    {
        // An id parameter stores the id's string, not a ulong, which the owner's record could not bind ($.triggerid).
        await using FakeZabbix server = await FakeZabbix.StartAsync();
        server.On("map.create", """{"sysmapids":["9"]}""");
        server.On("map.get", """[{"sysmapid":"9","name":"next-m"}]""");
        using Shell shell = Shell.Open();
        shell.Connect(server);

        Run run = shell.Invoke("""
            $link = New-ZbxMapLink -Element1Id 1 -Element2Id 2 -LinkTrigger @(New-ZbxMapLinkTrigger -TriggerId 99 -Color DarkRed)
            $link.linktriggers[0].triggerid
            New-ZbxMap -Name next-m -Width 800 -Height 600 -Link @($link) -Confirm:$false | Out-Null
            """);

        Assert.Empty(run.Errors);
        Assert.Equal("99", Assert.Single(run.Output).ToString());
        JsonElement link = Assert.Single(Assert.Single(server.ParamsOf("map.create")).GetProperty("links").EnumerateArray());
        Assert.Equal("1", link.GetProperty("selementid1").GetString());
        JsonElement trigger = Assert.Single(link.GetProperty("linktriggers").EnumerateArray());
        Assert.Equal("99", trigger.GetProperty("triggerid").GetString());
        Assert.Equal("AA0000", trigger.GetProperty("color").GetString());
    }

    [Fact]
    public void A_builder_refuses_a_reference_given_by_name_since_it_resolves_nothing()
    {
        // A builder talks to no server, so a name it cannot turn into an id is an error, never a silently dropped id.
        using Shell shell = Shell.Open();

        Run byName = shell.Invoke("New-ZbxActionOperation -OperationType GroupAdd -OperationGroup 'Linux servers'");
        Run byId = shell.Invoke("(New-ZbxActionOperation -OperationType GroupAdd -OperationGroup 5).opgroup[0].groupid");

        Assert.Contains(byName.Errors, e => e.Exception.Message.Contains("Linux servers", StringComparison.Ordinal));
        Assert.Empty(byId.Errors);
        Assert.Equal("5", Assert.Single(byId.Output).ToString());
    }

    [Theory]
    [InlineData("ifname")]
    [InlineData("IFNAME")]
    [InlineData("#IFNAME")]
    [InlineData("{#IFNAME}")]
    [InlineData("{#ifname}")]
    public void An_LLD_macro_name_is_written_in_its_braced_form(string given)
    {
        // An LLD macro name is written in its braced form (NAME -> {#NAME}).
        using Shell shell = Shell.Open();

        Run run = shell.Invoke($"(New-ZbxLldFilterCondition -Macro '{given}' -Value x).macro; (New-ZbxLldMacroPath -LowLevelDiscoveryMacro '{given}' -Path '$.x').lld_macro");

        Assert.Empty(run.Errors);
        Assert.Equal(["{#IFNAME}", "{#IFNAME}"], run.Output.Select(o => o.ToString()));
    }

    [Theory]
    [InlineData("New-ZbxMapShape -Type Rectangle -FontColor DarkGray", "font_color", "333333")]
    [InlineData("New-ZbxMapShape -Type Rectangle -BackgroundColor lightgray", "background_color", "CCCCCC")]
    [InlineData("New-ZbxMapShape -Type Rectangle -BorderColor DDEEFF", "border_color", "DDEEFF")]
    [InlineData("New-ZbxMapShape -Type Rectangle -FontColor 112233", "font_color", "112233")]
    [InlineData("New-ZbxMapLine -LineColor Red", "line_color", "DD0000")]
    [InlineData("New-ZbxMapLine -LineColor AABBCC", "line_color", "AABBCC")]
    [InlineData("New-ZbxMapLink -Element1Id 0 -Element2Id 1 -Color BrightGreen", "color", "00CC00")]
    [InlineData("New-ZbxMapLink -Element1Id 0 -Element2Id 1 -Color aabbcc", "color", "aabbcc")]
    [InlineData("New-ZbxMapLinkTrigger -TriggerId 456 -Color FF8800", "color", "FF8800")]
    [InlineData("New-ZbxDashboardWidget -Type ProblemsBySeverity", "type", "problemsbysv")]
    [InlineData("New-ZbxDashboardWidget -Type TriggerOverview", "type", "trigover")]
    [InlineData("New-ZbxDashboardWidget -Type ScatterPlot", "type", "scatterplot")]
    [InlineData("New-ZbxDashboardWidget -Type clock", "type", "clock")]
    [InlineData("New-ZbxDashboardWidget -Type custom.myplugin", "type", "custom.myplugin")]
    public void A_named_color_or_widget_type_is_written_as_its_wire_value_and_a_raw_value_passes_through(string build, string key, string wire)
    {
        // A color or widget type is a name or the wire value; one parameter takes both.
        using Shell shell = Shell.Open();

        Run run = shell.Invoke("(" + build + ")['" + key + "']");

        Assert.Empty(run.Errors);
        Assert.Equal(wire, Assert.Single(run.Output).ToString());
    }

    [Theory]
    [InlineData("New-ZbxMapLink -Element1Id 0 -Element2Id 1 -Color ZZZZZZ")]
    [InlineData("New-ZbxMapLink -Element1Id 0 -Element2Id 1 -Color FFF")]
    [InlineData("New-ZbxMapShape -Type Rectangle -FontColor Mauve")]
    public void A_color_that_is_neither_a_name_nor_six_hex_digits_is_refused_at_binding(string build)
    {
        using Shell shell = Shell.Open();

        Run run = shell.Invoke(build);

        Assert.IsType<ParameterBindingException>(Assert.Single(run.Errors).Exception, exactMatch: false);
    }

    [Theory]
    [InlineData("New-ZbxMapShape -Type Rectangle -FontColor Dark", "DarkGray")]
    [InlineData("New-ZbxMapLinkTrigger -TriggerId 1 -Color Bri", "BrightGreen")]
    [InlineData("New-ZbxDashboardWidget -Type ProblemsBy", "ProblemsBySeverity")]
    public void The_named_values_complete(string input, string expected)
    {
        using Shell shell = Shell.Open();

        CommandCompletion completion = shell.Complete(input);

        Assert.Contains(expected, completion.CompletionMatches.Select(match => match.CompletionText));
    }

    // Only the shapes the wire's JSON has, as ConvertFrom-Json gives them: no CLR enum, no ulong, no typed record.
    private static void AssertWireForm(object? value, string where)
    {
        switch (value)
        {
            case null or string or long or bool or double or BigInteger:
                return;
            case Hashtable table:
                foreach (DictionaryEntry entry in table) AssertWireForm(entry.Value, where + "." + entry.Key);
                return;
            case object[] items:
                foreach (object? item in items) AssertWireForm(item, where + "[]");
                return;
            default:
                Assert.Fail(where + " holds a " + value.GetType().FullName + ", not a wire value");
                return;
        }
    }

    // The type of the record member a parameter writes, when the builder names it alike.
    private static Type? Member(JsonTypeInfo contract, string parameter)
        => contract.Properties.FirstOrDefault(p => p.AttributeProvider is MemberInfo m && m.Name == parameter)?.PropertyType is { } type
            ? Nullable.GetUnderlyingType(type) ?? type
            : null;

    // A value for one builder parameter: "AABBCC" for a string passes the color check, stays itself as a widget type and
    // becomes {#AABBCC} as an LLD macro, and "1h" for a string that writes a length of time; an enum's second member
    // (non-zero where there is one); a nested record built from its own contract.
    private static object? Sample(Type type, Type? member)
    {
        Type bare = Nullable.GetUnderlyingType(type) ?? type;
        if (bare == typeof(string)) return member == typeof(ZabbixDuration) || member == typeof(ZabbixInterval) ? "1h" : "AABBCC";
        if (bare == typeof(int) || bare == typeof(long) || bare == typeof(ulong) || bare == typeof(double)) return 3;
        if (bare == typeof(bool)) return true;
        if (bare == typeof(ulong)) return "5";
        if (bare == typeof(TimeOnly)) return new TimeOnly(2, 0);
        if (bare == typeof(DateTimeOffset)) return DateTimeOffset.FromUnixTimeSeconds(1_700_000_000);
        if (bare == typeof(JsonElement)) return JsonDocument.Parse("\"a=b\"").RootElement.Clone();
        if (bare.IsEnum)
        {
            Array values = Enum.GetValues(bare);
            return values.GetValue(values.Length > 1 ? 1 : 0);
        }
        if (bare == typeof(string[])) return new[] { "5" };
        if (bare.IsArray)
        {
            Type element = bare.GetElementType()!;
            if (element.IsGenericType && element.GetGenericTypeDefinition() == typeof(ZabbixReference<>)) return new[] { "5" };
            Array array = Array.CreateInstance(element, 1);
            array.SetValue(SampleRecord(element, 0), 0);
            return array;
        }
        return SampleRecord(bare, 0);
    }

    private static object SampleRecord(Type type, int depth)
    {
        JsonTypeInfo info = global::Community.Zabbix.ZabbixApiJson.Default.GetTypeInfo(type)!;
        return JsonSerializer.Deserialize(SampleJson(info, depth).ToJsonString(), info)!;
    }

    private static JsonObject SampleJson(JsonTypeInfo info, int depth)
    {
        var sample = new JsonObject();
        foreach (JsonPropertyInfo property in info.Properties)
            if (SampleNode(property.PropertyType, depth) is { } node) sample[property.Name] = node;
        return sample;
    }

    private static JsonNode? SampleNode(Type type, int depth)
    {
        Type bare = Nullable.GetUnderlyingType(type) ?? type;
        if (bare == typeof(string) || bare == typeof(ulong)) return JsonValue.Create("1");
        if (bare == typeof(int) || bare == typeof(long) || bare == typeof(ulong) || bare == typeof(double)) return JsonValue.Create(1);
        if (bare == typeof(bool)) return JsonValue.Create(true);
        if (bare.IsEnum) return JsonValue.Create(Convert.ToInt64(Enum.GetValues(bare).GetValue(0), System.Globalization.CultureInfo.InvariantCulture));
        if (depth > 2) return null;
        if (bare.IsGenericType && bare.GetGenericTypeDefinition() == typeof(IReadOnlyList<>))
            return SampleNode(bare.GetGenericArguments()[0], depth + 1) is { } item ? new JsonArray(item) : null;
        return bare.Namespace == "Community.Zabbix" && global::Community.Zabbix.ZabbixApiJson.Default.GetTypeInfo(bare) is { Kind: JsonTypeInfoKind.Object } nested
            ? SampleJson(nested, depth + 1)
            : null;
    }
}
